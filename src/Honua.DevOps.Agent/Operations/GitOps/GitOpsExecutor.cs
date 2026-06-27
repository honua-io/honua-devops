using System.Text.Json;

using Honua.DevOps.Agent.Operations.OperatorPolicy;
using OperatorPolicyModel = Honua.DevOps.Agent.Operations.OperatorPolicy.OperatorPolicy;

namespace Honua.DevOps.Agent.Operations.GitOps;

// Orchestrates GitOps `sync` actuation THROUGH the honua-server deploy-control endpoints.
// It never reimplements deployment execution: DeployWorkflowService + its reconciler do the
// real reconcile. The executor's job is the safe orchestration spine:
//
//   validate (preflight + plan, read-only)
//     -> create the durable operation (submitImmediately=false)
//        -> if the server parked it at AwaitingApproval, or policy is pr-first: STOP and
//           surface the operationId + evidence (never auto-submit)
//        -> only when policy/approval allows: submit
//           -> poll /operations/{id} to a terminal status.
//
// Default-safe: with EXECUTION_MODE=plan (the default) the decision is non-mutating and the
// executor returns plan-only WITHOUT touching the backend at all.
internal sealed class GitOpsExecutor(
    OperationRuntime runtime,
    BackendGateway gateway,
    OperatorPolicyModel policy,
    DeployPollPolicy? pollPolicy = null,
    Func<TimeSpan, CancellationToken, Task>? delay = null,
    TimeProvider? timeProvider = null)
{
    private readonly OperationRuntime _runtime = runtime;
    private readonly BackendGateway _gateway = gateway;
    private readonly OperatorPolicyModel _policy = policy;
    private readonly DeployPollPolicy _pollPolicy = pollPolicy ?? DeployPollPolicy.Resolve();
    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    internal OperationRuntime Runtime => _runtime;

    internal BackendGateway Gateway => _gateway;

    internal OperatorPolicyModel Policy => _policy;

    // Actuate a sync. `desiredRevision`/`reason`/`correlationId`/`priority`/`parameters` are
    // already validated/sanitized by the caller. `authorizationDryRun` is the toolkit's
    // per-request dry-run verdict (from AuthorizeDeployment); it folds the execution tier
    // into the safety decision so e.g. a plan/observe tier can never mutate.
    internal Task<GitOpsExecutionResult> ExecuteSyncAsync(
        string desiredRevision,
        string? currentRevision,
        string reason,
        string idempotencyKey,
        string correlationId,
        string priority,
        IReadOnlyDictionary<string, string> parameters,
        bool authorizationDryRun,
        string policyGate,
        CancellationToken cancellationToken)
    {
        GitOpsActuationDecision decision = GitOpsActuationDecision.Resolve(
            _runtime.ExecutionMode,
            _policy,
            authorizationDryRun,
            policyGate);

        return ActuateAsync(
            kind: "sync",
            desiredRevision,
            currentRevision,
            reason,
            idempotencyKey,
            correlationId,
            priority,
            parameters,
            decision,
            cancellationToken);
    }

    // Core create -> gate -> submit -> poll spine shared by sync and promote. Rollback has a
    // distinct entry point because it operates on an existing operation and honors the
    // server's data-affecting approval gate.
    internal async Task<GitOpsExecutionResult> ActuateAsync(
        string kind,
        string desiredRevision,
        string? currentRevision,
        string reason,
        string idempotencyKey,
        string correlationId,
        string priority,
        IReadOnlyDictionary<string, string> parameters,
        GitOpsActuationDecision decision,
        CancellationToken cancellationToken)
    {
        List<OperationBackendStep> steps = [];
        List<string> findings =
        [
            $"Actuation kind: {kind}.",
            $"Execution mode: {decision.Mode}; approval mode: {decision.ApprovalMode}; policy gate: {decision.PolicyGate}.",
            decision.Rationale
        ];

        // SAFETY INVARIANT 1: plan posture (default) -> zero mutation, zero backend calls.
        if (!decision.Mutating)
        {
            findings.Add("No durable operation was created; no submit or rollback occurred.");
            return new GitOpsExecutionResult(
                Status: GitOpsExecutionStatus.PlanOnly,
                OperationId: null,
                ServerStatus: null,
                Mutated: false,
                Decision: decision,
                BackendSteps: steps,
                Findings: findings,
                BlockingReasons: []);
        }

        // No durable target -> cannot enter the lifecycle. Do not invent an operation id.
        if (string.IsNullOrWhiteSpace(_runtime.DeployTargetId))
        {
            findings.Add("HONUA_DEVOPS_DEPLOY_TARGET_ID is not configured; cannot create a durable server operation.");
            return new GitOpsExecutionResult(
                Status: GitOpsExecutionStatus.ContractUnavailable,
                OperationId: null,
                ServerStatus: null,
                Mutated: false,
                Decision: decision,
                BackendSteps: steps,
                Findings: findings,
                BlockingReasons: ["deploy-target-unconfigured"]);
        }

        // Read-only validation against RequiredChecks: preflight + plan.
        BackendCallResult preflight = await _gateway.RequestDeployPreflightAsync(includeDiagnostics: true, cancellationToken);
        steps.Add(OperationBackendStep.From("deploy-preflight", preflight, mutatesState: false));

        BackendCallResult plan = await _gateway.PlanDeployOperationAsync(
            _runtime.DeployTargetId!,
            desiredRevision,
            currentRevision,
            parameters,
            cancellationToken);
        steps.Add(OperationBackendStep.From("deploy-plan", plan, mutatesState: false));

        // Create the durable operation with submitImmediately=false: this records a durable
        // server operation (a real write) but executes NOTHING. Submission is a separate,
        // gated step below.
        using BackendJsonResult created = await _gateway.CreateDeployOperationJsonAsync(
            _runtime.DeployTargetId!,
            desiredRevision,
            currentRevision,
            reason,
            submitImmediately: false,
            idempotencyKey,
            correlationId,
            priority,
            parameters,
            cancellationToken);
        bool operationRecorded = created.CallResult.IsSuccess && created.Payload is not null;
        steps.Add(OperationBackendStep.From("deploy-operation-create", created.CallResult, mutatesState: operationRecorded));

        string? operationId = created.Payload is null ? null : DeployOperationReader.ReadOperationId(created.Payload.RootElement);
        string? serverStatus = created.Payload is null ? null : DeployOperationReader.ReadStatus(created.Payload.RootElement);
        IReadOnlyList<string> createBlockers = created.Payload is null
            ? []
            : DeployOperationReader.ReadBlockingReasons(created.Payload.RootElement);

        if (string.IsNullOrWhiteSpace(operationId))
        {
            findings.Add("honua-server deploy-control did not return a durable operation id; nothing was submitted, no operation invented.");
            return new GitOpsExecutionResult(
                Status: GitOpsExecutionStatus.ContractUnavailable,
                OperationId: null,
                ServerStatus: serverStatus,
                Mutated: operationRecorded,
                Decision: decision,
                BackendSteps: steps,
                Findings: findings,
                BlockingReasons: createBlockers.Count > 0 ? createBlockers : ["deploy-operation-id-missing"]);
        }

        findings.Add($"Durable deploy-control operation created: {operationId} (status {serverStatus ?? "unknown"}).");

        // SAFETY INVARIANT 2: approval gate. We must NOT submit when:
        //   * the server parked the operation at AwaitingApproval (plan required approval), or
        //   * the operation carries blocking reasons, or
        //   * policy is pr-first (decision.MayAutoSubmit == false).
        // In all of these we STOP and surface the operationId + evidence for an external approver.
        bool serverRequiresApproval = DeployOperationReader.IsAwaitingApproval(serverStatus);
        bool blocked = createBlockers.Count > 0;
        if (!decision.MayAutoSubmit || serverRequiresApproval || blocked)
        {
            string why = blocked
                ? $"Operation has blocking reasons: {string.Join("; ", createBlockers)}."
                : serverRequiresApproval
                    ? "Server parked the operation at AwaitingApproval; deploy plan requires explicit approval."
                    : "Approval mode `pr-first` requires external approval before submission.";
            findings.Add($"Submission paused: {why}");
            findings.Add($"Surface operationId `{operationId}` and the recorded evidence for governed approval; submit it through the deploy-control approval path.");
            return new GitOpsExecutionResult(
                Status: GitOpsExecutionStatus.AwaitingApproval,
                OperationId: operationId,
                ServerStatus: serverStatus,
                Mutated: operationRecorded,
                Decision: decision,
                BackendSteps: steps,
                Findings: findings,
                BlockingReasons: createBlockers);
        }

        // Approval satisfied (direct-allowed / break-glass) -> submit, then poll to terminal.
        return await SubmitAndPollAsync(operationId, reason, decision, steps, findings, cancellationToken);
    }

    internal async Task<GitOpsExecutionResult> SubmitAndPollAsync(
        string operationId,
        string reason,
        GitOpsActuationDecision decision,
        List<OperationBackendStep> steps,
        List<string> findings,
        CancellationToken cancellationToken)
    {
        using BackendJsonResult submitted = await _gateway.SubmitDeployOperationJsonAsync(operationId, reason, cancellationToken);
        steps.Add(OperationBackendStep.From("deploy-operation-submit", submitted.CallResult, mutatesState: submitted.CallResult.IsSuccess));

        if (!submitted.CallResult.IsSuccess)
        {
            // A non-success here includes the OperatorApprovalGate 403 (when a gate applies)
            // and any backend/registration failure. Treat as a refusal: nothing reconciled.
            findings.Add($"Submit was refused by deploy-control ({submitted.CallResult.Detail}); operation `{operationId}` was not advanced.");
            return new GitOpsExecutionResult(
                Status: GitOpsExecutionStatus.ApprovalRequired,
                OperationId: operationId,
                ServerStatus: submitted.Payload is null ? null : DeployOperationReader.ReadStatus(submitted.Payload.RootElement),
                Mutated: false,
                Decision: decision,
                BackendSteps: steps,
                Findings: findings,
                BlockingReasons: submitted.Payload is null
                    ? ["submit-refused"]
                    : DeployOperationReader.ReadBlockingReasons(submitted.Payload.RootElement));
        }

        string? status = submitted.Payload is null ? null : DeployOperationReader.ReadStatus(submitted.Payload.RootElement);
        findings.Add($"Operation `{operationId}` submitted (status {status ?? "unknown"}).");

        // Poll to a terminal status with capped exponential backoff up to the configured
        // total timeout. The reconciler advances the operation server-side, so a real deploy
        // routinely takes longer than a single cycle. The first read happens immediately; on
        // each subsequent attempt we back off (initial -> *2 -> ... -> max), never sleeping
        // past the deadline. If the budget is exhausted while the op is still non-terminal we
        // DO NOT collapse that into Failed — it is reported as `in-progress` below.
        DateTimeOffset deadline = _timeProvider.GetUtcNow() + _pollPolicy.Timeout;
        TimeSpan interval = _pollPolicy.InitialInterval;
        int attempt = 0;
        while (!DeployOperationReader.IsTerminal(status))
        {
            DateTimeOffset now = _timeProvider.GetUtcNow();
            if (now >= deadline)
            {
                break;
            }

            if (attempt > 0)
            {
                TimeSpan remaining = deadline - now;
                await _delay(interval < remaining ? interval : remaining, cancellationToken);
                interval = TimeSpan.FromMilliseconds(
                    Math.Min(_pollPolicy.MaxInterval.TotalMilliseconds, interval.TotalMilliseconds * 2));
            }

            attempt++;
            using BackendJsonResult polled = await _gateway.GetDeployOperationJsonAsync(operationId, cancellationToken);
            steps.Add(OperationBackendStep.From($"deploy-operation-poll-{attempt}", polled.CallResult, mutatesState: false));
            if (polled.CallResult.IsSuccess && polled.Payload is not null)
            {
                status = DeployOperationReader.ReadStatus(polled.Payload.RootElement);
            }
        }

        // Map the last-observed status. CRITICAL: only a genuine terminal status is allowed to
        // resolve to Succeeded/RolledBack/Failed. A still-reconciling (non-terminal) status at
        // budget exhaustion is `in-progress`, never Failed — the operation is healthy and the
        // server keeps advancing it.
        string resultStatus;
        if (DeployOperationReader.IsSuccess(status))
        {
            resultStatus = GitOpsExecutionStatus.Succeeded;
        }
        else if (DeployOperationReader.IsRolledBack(status))
        {
            resultStatus = GitOpsExecutionStatus.RolledBack;
        }
        else if (DeployOperationReader.IsTerminal(status))
        {
            resultStatus = GitOpsExecutionStatus.Failed;
        }
        else
        {
            resultStatus = GitOpsExecutionStatus.InProgress;
        }

        if (resultStatus == GitOpsExecutionStatus.InProgress)
        {
            findings.Add(
                $"Operation `{operationId}` is still reconciling (last-observed status: {status ?? "unknown"}) after the poll budget " +
                $"of {_pollPolicy.Timeout.TotalSeconds:0}s; the reconciler advances it server-side. This is NOT a failure. " +
                $"Keep watching it via the deploy-control operation/approval path. Tune the budget with " +
                $"`{DeployPollPolicy.TimeoutSecondsVariable}` if longer waits are expected.");
        }
        else
        {
            findings.Add($"Terminal status for `{operationId}`: {status ?? "unknown"}.");
        }

        return new GitOpsExecutionResult(
            Status: resultStatus,
            OperationId: operationId,
            ServerStatus: status,
            Mutated: true,
            Decision: decision,
            BackendSteps: steps,
            Findings: findings,
            BlockingReasons: []);
    }
}
