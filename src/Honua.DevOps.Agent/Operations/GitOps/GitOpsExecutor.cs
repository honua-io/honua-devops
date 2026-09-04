using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Honua.DevOps.Agent.Operations.Actuation;
using Honua.DevOps.Agent.Operations.DesiredState;
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
//        -> only when policy/approval allows: apply desired state, then submit
//           -> poll /operations/{id} to a terminal status.
//
// Every write on that path goes through ActuationSpine (issue #153): the request identity is
// sealed before the durable operation is created, and the manifest apply and the submit are
// each authorized by a grant bound to that exact operation, digest, and approval. Nothing
// mutates ahead of the operation record.
//
// Default-safe: with EXECUTION_MODE=plan (the default) the decision is non-mutating and the
// executor returns plan-only WITHOUT touching the backend at all.
internal sealed class GitOpsExecutor(
    OperationRuntime runtime,
    BackendGateway gateway,
    OperatorPolicyModel policy,
    DeployPollPolicy? pollPolicy = null,
    Func<TimeSpan, CancellationToken, Task>? delay = null,
    TimeProvider? timeProvider = null,
    ActuationSpine? spine = null,
    IManifestApplyCheckpointStore? checkpointStore = null,
    Func<Task>? afterManifestAcknowledgement = null)
{
    private readonly OperationRuntime _runtime = runtime;
    private readonly BackendGateway _gateway = gateway;
    private readonly OperatorPolicyModel _policy = policy;
    private readonly ActuationSpine _spine = spine ?? new ActuationSpine(runtime, policy);
    private readonly DeployPollPolicy _pollPolicy = pollPolicy ?? DeployPollPolicy.Resolve();
    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly IManifestApplyCheckpointStore _checkpointStore =
        checkpointStore ?? ManifestApplyCheckpointStoreFactory.Create(policy.AuditHookTarget);
    private readonly Func<Task>? _afterManifestAcknowledgement = afterManifestAcknowledgement;

    internal OperationRuntime Runtime => _runtime;

    internal BackendGateway Gateway => _gateway;

    internal OperatorPolicyModel Policy => _policy;

    internal ActuationSpine Spine => _spine;

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
        CancellationToken cancellationToken,
        ManifestApplyRequest? desiredState = null)
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
            cancellationToken,
            desiredState);
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
        CancellationToken cancellationToken,
        ManifestApplyRequest? desiredState = null)
    {
        List<OperationBackendStep> steps = [];
        Dictionary<string, string> sealedParameters = new(parameters, StringComparer.Ordinal);
        string? manifestAcknowledgementDigest = null;
        if (desiredState is not null)
        {
            string sealedJson = JsonSerializer.Serialize(desiredState);
            byte[] sealedBytes = Encoding.UTF8.GetBytes(sealedJson);
            sealedParameters["honua.devops/desired-state-json-base64"] = Convert.ToBase64String(sealedBytes);
            sealedParameters["honua.devops/desired-state-digest"] = Convert.ToHexString(SHA256.HashData(sealedBytes)).ToLowerInvariant();
        }
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

        // Seal the request identity BEFORE any write. The spine fails closed here when the
        // durable operation store, the audit/receipt sink, or the idempotency key is missing:
        // no durable target means we cannot enter the lifecycle, and an operation id is never
        // invented locally.
        ActuationAuthorization authorization = _spine.Authorize(new ActuationRequest(
            ActuatorId: $"honua.gitops.{kind}",
            Action: kind,
            Target: _runtime.DeployTargetId ?? string.Empty,
            Environments: ReadEnvironments(sealedParameters),
            DesiredState: BuildDesiredStateIdentity(desiredRevision, currentRevision, sealedParameters),
            IdempotencyKey: idempotencyKey,
            PolicyGate: decision.PolicyGate,
            AuthorizationDryRun: !decision.Mutating,
            Actor: correlationId,
            LifecycleEntry: BackendMutation.DeployOperationCreate));

        if (!authorization.IsGranted)
        {
            findings.Add(authorization.Reason);
            return new GitOpsExecutionResult(
                Status: GitOpsExecutionStatus.ContractUnavailable,
                OperationId: null,
                ServerStatus: null,
                Mutated: false,
                Decision: decision,
                BackendSteps: steps,
                Findings: findings,
                BlockingReasons: authorization.BlockingReason is null ? [] : [authorization.BlockingReason]);
        }

        ActuationSpine.OperationGrant operationGrant = authorization.Grant!;
        findings.Add(
            $"Sealed actuation request: actuator `{operationGrant.ActuatorId}`, target `{operationGrant.Target}`, " +
            $"digest `{operationGrant.RequestDigest[..12]}`, idempotency key `{operationGrant.IdempotencyKey}`.");

        // Read-only validation against RequiredChecks: preflight + plan.
        BackendCallResult preflight = await _gateway.RequestDeployPreflightAsync(includeDiagnostics: true, cancellationToken);
        steps.Add(OperationBackendStep.From("deploy-preflight", preflight, mutatesState: false));

        // Stamp the sealed lineage onto the server-visible parameters so the durable operation
        // records the same identity the spine authorized. The digest is taken FROM the grant
        // rather than recomputed here: one source of truth, so the value the server stores
        // cannot drift from the value that gated the mutation.
        Dictionary<string, string> lineageParameters = new(sealedParameters, StringComparer.Ordinal)
        {
            ["honua.devops/request-digest"] = operationGrant.RequestDigest,
            ["honua.devops/idempotency-key"] = operationGrant.IdempotencyKey
        };

        BackendCallResult plan = await _gateway.PlanDeployOperationAsync(
            _runtime.DeployTargetId!,
            desiredRevision,
            currentRevision,
            lineageParameters,
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
            lineageParameters,
            operationGrant,
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
        // Both gates must hold before ANY state mutation: the local policy ceiling (a
        // registered direct-execution policy result, never a caller flag) AND the control
        // plane's own decision for this exact operation.
        ApprovalEvidence approval = ApprovalEvidence
            .FromDirectExecutionPolicy(decision)
            .And(ApprovalEvidence.FromControlPlane(
                operationId,
                DeployOperationReader.IsAwaitingApproval(serverStatus),
                createBlockers));

        if (!_spine.TryAuthorizeMutation(
                operationGrant,
                BackendMutation.DeployOperationSubmit,
                operationId,
                approval,
                out ActuationSpine.MutationGrant? submitGrant,
                out string refusal))
        {
            findings.Add($"Submission paused: {refusal}");
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

        findings.Add(
            $"Mutation authorized for operation `{operationId}` by {approval.Kind} ({approval.ReceiptId ?? "no reference"}).");

        // Desired-state write, when this actuation carries one. It happens HERE — after the
        // durable operation exists and its approval gate is satisfied — never on the planning
        // path (issue #153).
        if (desiredState is not null)
        {
            if (!_spine.TryAuthorizeMutation(
                    operationGrant,
                    BackendMutation.ManifestApply,
                    operationId,
                    approval,
                    out ActuationSpine.MutationGrant? applyGrant,
                    out string applyRefusal))
            {
                findings.Add($"Desired-state apply not authorized: {applyRefusal}");
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

            ManifestApplyOutcome apply = await ApplyManifestOnceAsync(
                operationId,
                desiredState,
                applyGrant!,
                cancellationToken,
                "manifest-apply");
            steps.Add(apply.Step);
            if (!apply.Acknowledged)
            {
                // The apply was issued but did not succeed. Do not submit on top of an
                // unknown desired state; report the ambiguity rather than a failure or a
                // success we cannot substantiate.
                findings.Add($"Desired-state apply was not durably acknowledged ({apply.Failure}); operation `{operationId}` was not submitted.");
                return new GitOpsExecutionResult(
                    Status: GitOpsExecutionStatus.Indeterminate,
                    OperationId: operationId,
                    ServerStatus: serverStatus,
                    Mutated: true,
                    Decision: decision,
                    BackendSteps: steps,
                    Findings: findings,
                    BlockingReasons: ["manifest-apply-unconfirmed"]);
            }

            manifestAcknowledgementDigest = apply.AcknowledgementDigest;
            findings.Add($"Desired state applied for operation `{operationId}`.");
        }

        // Approval satisfied -> submit, then poll to terminal.
        return await SubmitAndPollAsync(
            operationId,
            reason,
            decision,
            steps,
            findings,
            submitGrant!,
            cancellationToken,
            desiredState is null ? null : ComputeDesiredStateDigest(desiredState),
            manifestAcknowledgementDigest,
            approval.ReceiptId);
    }

    private static IReadOnlyList<string> ReadEnvironments(IReadOnlyDictionary<string, string> parameters)
        => parameters.TryGetValue("environments", out string? environments) && !string.IsNullOrWhiteSpace(environments)
            ? environments.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [];

    // The canonical request payload the spine takes its digest over. Two deliveries of the
    // same logical request produce the same digest, which is what lets a retry prove it is
    // resuming the original operation rather than starting a second one.
    private static string BuildDesiredStateIdentity(
        string desiredRevision,
        string? currentRevision,
        IReadOnlyDictionary<string, string> parameters)
        => string.Join(
            ";",
            [
                $"desiredRevision={desiredRevision}",
                $"currentRevision={currentRevision ?? string.Empty}",
                .. parameters.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key}={pair.Value}")
            ]);

    // Submits an EXISTING durable operation (the `deploy-submit` runbook). The operation was
    // created by an earlier governed action, so there is no create step here — but the write
    // still goes through the same spine: read the operation, check the control plane's own
    // decision alongside the policy ceiling, then submit under a bound mutation grant.
    internal async Task<GitOpsExecutionResult> ExecuteSubmitAsync(
        string operationId,
        string reason,
        bool confirmed,
        bool authorizationDryRun,
        string policyGate,
        CancellationToken cancellationToken,
        string? approvalReference = null)
    {
        GitOpsActuationDecision decision = GitOpsActuationDecision.Resolve(
            _runtime.ExecutionMode,
            _policy,
            authorizationDryRun,
            policyGate);

        List<OperationBackendStep> steps = [];
        List<string> findings =
        [
            $"Actuation kind: submit (operation {operationId}).",
            $"Execution mode: {decision.Mode}; approval mode: {decision.ApprovalMode}; policy gate: {decision.PolicyGate}.",
            decision.Rationale
        ];

        if (!decision.Mutating)
        {
            findings.Add($"No submit was issued for `{operationId}`; plan-only posture.");
            return new GitOpsExecutionResult(
                GitOpsExecutionStatus.PlanOnly, operationId, null, false, decision, steps, findings, []);
        }

        using BackendJsonResult current = await _gateway.GetDeployOperationJsonAsync(operationId, cancellationToken);
        steps.Add(OperationBackendStep.From("deploy-operation-read", current.CallResult, mutatesState: false));
        if (!current.CallResult.IsSuccess || current.Payload is null)
        {
            findings.Add($"Could not read deploy-control operation `{operationId}`; nothing was submitted, no operation invented.");
            return new GitOpsExecutionResult(
                GitOpsExecutionStatus.ContractUnavailable, operationId, null, false, decision, steps, findings,
                ["submit-operation-not-found"]);
        }

        string? serverStatus = DeployOperationReader.ReadStatus(current.Payload.RootElement);
        IReadOnlyList<string> blockers = DeployOperationReader.ReadBlockingReasons(current.Payload.RootElement);
        string? persistedApprovalReference = DeployOperationReader.ReadApprovalReference(current.Payload.RootElement);
        IReadOnlyDictionary<string, string> persistedParameters = DeployOperationReader.ReadParameters(current.Payload.RootElement);
        if (!TryRecoverDesiredState(
                persistedParameters,
                out ManifestApplyRequest? recoveredDesiredState,
                out string? recoveredDesiredStateDigest,
                out string sealFailure))
        {
            findings.Add($"Sealed desired state could not be recovered: {sealFailure}.");
            return new GitOpsExecutionResult(
                GitOpsExecutionStatus.ContractUnavailable, operationId, serverStatus, false, decision, steps, findings,
                ["desired-state-seal-invalid"]);
        }

        // POST /submit is the server's manual approval transition. Only a still-planned
        // operation can safely enter that transition; unknown, active, and terminal states
        // are not evidence of approval and must not trigger a resumed manifest apply or submit.
        if (!DeployOperationReader.IsPlanned(serverStatus))
        {
            if (DeployOperationReader.IsAwaitingApproval(serverStatus))
            {
                findings.Add($"Deploy operation `{operationId}` is still awaiting approval; nothing was submitted.");
                return new GitOpsExecutionResult(
                    GitOpsExecutionStatus.AwaitingApproval, operationId, serverStatus, false, decision, steps, findings,
                    blockers.Count > 0 ? blockers : ["approval-required"]);
            }

            findings.Add($"Deploy operation status `{serverStatus ?? "unknown"}` is not resumable from the explicit submit runbook.");
            return new GitOpsExecutionResult(
                GitOpsExecutionStatus.ContractUnavailable, operationId, serverStatus, false, decision, steps, findings,
                ["submit-status-not-resumable"]);
        }

        if (persistedApprovalReference is not null
            && !string.Equals(persistedApprovalReference, approvalReference?.Trim(), StringComparison.Ordinal))
        {
            findings.Add("The supplied approval reference does not match the control plane's approved transition.");
            return new GitOpsExecutionResult(
                GitOpsExecutionStatus.AwaitingApproval, operationId, serverStatus, false, decision, steps, findings,
                ["approval-reference-mismatch"]);
        }

        string requestDigest = persistedParameters.GetValueOrDefault("honua.devops/request-digest") ?? string.Empty;
        string idempotencyKey = persistedParameters.GetValueOrDefault("honua.devops/idempotency-key")
            ?? $"honua-devops:submit:{operationId}";

        ActuationAuthorization authorization = _spine.Authorize(new ActuationRequest(
            ActuatorId: "honua.deploy-operation.submit",
            Action: "submit",
            Target: _runtime.DeployTargetId ?? operationId,
            Environments: [],
            DesiredState: string.IsNullOrWhiteSpace(requestDigest) ? $"submit:{operationId}" : requestDigest,
            IdempotencyKey: idempotencyKey,
            PolicyGate: decision.PolicyGate,
            AuthorizationDryRun: !decision.Mutating,
            Actor: $"honua-devops:runbook:deploy-submit",
            LifecycleEntry: BackendMutation.DeployOperationCreate));

        if (!authorization.IsGranted)
        {
            findings.Add(authorization.Reason);
            return new GitOpsExecutionResult(
                GitOpsExecutionStatus.ContractUnavailable, operationId, serverStatus, false, decision, steps, findings,
                authorization.BlockingReason is null ? [] : [authorization.BlockingReason]);
        }

        // This is an explicit resume of an already-created operation. The confirmed runbook
        // is the approval action for the server's Planned -> Submitted transition; it is not
        // inferred from the mere absence of AwaitingApproval or from an unknown status.
        ApprovalEvidence explicitApproval = ApprovalEvidence
            .FromExplicitRunbookConfirmation(operationId, confirmed, approvalReference ?? persistedApprovalReference);
        ApprovalEvidence controlPlaneApproval = ApprovalEvidence.FromControlPlane(
            operationId,
            awaitingApproval: false,
            blockers);
        // Keep the explicit approval receipt when both gates are satisfied. The receipt is
        // the lineage binding carried into the terminal actuator receipt; replacing it with
        // the operation id would lose the approval decision's identity across a restart.
        ApprovalEvidence approval = explicitApproval.Satisfied && controlPlaneApproval.Satisfied
            ? explicitApproval
            : explicitApproval.And(controlPlaneApproval);

        if (!_spine.TryAuthorizeMutation(
                authorization.Grant!,
                BackendMutation.DeployOperationSubmit,
                operationId,
                approval,
                out ActuationSpine.MutationGrant? grant,
                out string refusal))
        {
            findings.Add($"Submission paused: {refusal}");
            return new GitOpsExecutionResult(
                GitOpsExecutionStatus.AwaitingApproval, operationId, serverStatus, false, decision, steps, findings, blockers);
        }

        if (recoveredDesiredState is not null)
        {
            if (!_spine.TryAuthorizeMutation(
                    authorization.Grant!, BackendMutation.ManifestApply, operationId, approval,
                    out ActuationSpine.MutationGrant? applyGrant, out string applyRefusal))
            {
                findings.Add($"Desired-state resume was refused: {applyRefusal}");
                return new GitOpsExecutionResult(
                    GitOpsExecutionStatus.AwaitingApproval, operationId, serverStatus, false, decision, steps, findings, blockers);
            }

            ManifestApplyOutcome apply = await ApplyManifestOnceAsync(
                operationId,
                recoveredDesiredState,
                applyGrant!,
                cancellationToken,
                "manifest-apply-resume");
            steps.Add(apply.Step);
            if (!apply.Acknowledged)
            {
                findings.Add($"Desired-state acknowledgement is ambiguous ({apply.Failure}); reconcile the original operation before retrying.");
                return new GitOpsExecutionResult(
                    GitOpsExecutionStatus.Indeterminate, operationId, serverStatus, true, decision, steps, findings,
                    ["manifest-apply-unconfirmed"]);
            }

            return await SubmitAndPollAsync(
                operationId,
                reason,
                decision,
                steps,
                findings,
                grant!,
                cancellationToken,
                recoveredDesiredStateDigest,
                apply.AcknowledgementDigest,
                approval.ReceiptId);
        }

        return await SubmitAndPollAsync(
            operationId,
            reason,
            decision,
            steps,
            findings,
            grant!,
            cancellationToken,
            null,
            null,
            approval.ReceiptId);
    }

    private static bool TryRecoverDesiredState(
        IReadOnlyDictionary<string, string> parameters,
        out ManifestApplyRequest? desiredState,
        out string? desiredStateDigest,
        out string failure)
    {
        desiredState = null;
        desiredStateDigest = null;
        failure = string.Empty;
        bool hasPayload = parameters.TryGetValue("honua.devops/desired-state-json-base64", out string? encoded);
        bool hasDigest = parameters.TryGetValue("honua.devops/desired-state-digest", out string? expectedDigest);
        if (!hasPayload && !hasDigest)
        {
            return true;
        }
        if (!hasPayload || !hasDigest || string.IsNullOrWhiteSpace(encoded) || string.IsNullOrWhiteSpace(expectedDigest))
        {
            failure = "the durable operation contains an incomplete desired-state seal";
            return false;
        }

        try
        {
            byte[] json = Convert.FromBase64String(encoded);
            string actualDigest = Convert.ToHexString(SHA256.HashData(json)).ToLowerInvariant();
            if (!CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(actualDigest), Encoding.ASCII.GetBytes(expectedDigest.ToLowerInvariant())))
            {
                failure = "the persisted desired-state digest does not match its sealed bytes";
                return false;
            }
            desiredState = JsonSerializer.Deserialize<ManifestApplyRequest>(json);
            if (desiredState is null || desiredState.DryRun)
            {
                failure = "the sealed manifest is absent or not an actuation request";
                return false;
            }
            desiredStateDigest = actualDigest;
            return true;
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            failure = "the persisted desired-state seal is malformed";
            return false;
        }
    }

    private async Task<ManifestApplyOutcome> ApplyManifestOnceAsync(
        string operationId,
        ManifestApplyRequest desiredState,
        ActuationSpine.MutationGrant grant,
        CancellationToken cancellationToken,
        string stepName)
    {
        string desiredStateDigest = ComputeDesiredStateDigest(desiredState);
        ManifestApplyCheckpoint? checkpoint = await _checkpointStore.ReadAsync(
            operationId,
            desiredStateDigest,
            cancellationToken);
        if (checkpoint is not null)
        {
            return new ManifestApplyOutcome(
                new OperationBackendStep(
                    Name: $"{stepName}-replay",
                    Endpoint: BackendGateway.BuildEndpoint(
                        _gateway.Configuration.HonuaApiBaseUri,
                        _gateway.Configuration.HonuaManifestApplyPath).ToString(),
                    Success: true,
                    Detail: "manifest acknowledgement recovered from the durable checkpoint; no second mutation was issued",
                    PayloadPreview: checkpoint.AcknowledgementDigest,
                    MutatesState: false),
                Acknowledged: true,
                AcknowledgementDigest: checkpoint.AcknowledgementDigest,
                Failure: null);
        }

        BackendCallResult applied = await _gateway.ApplyManifestAsync(desiredState, grant, cancellationToken);
        if (!applied.IsSuccess)
        {
            return new ManifestApplyOutcome(
                OperationBackendStep.From(stepName, applied, mutatesState: false),
                Acknowledged: false,
                AcknowledgementDigest: null,
                Failure: applied.Detail);
        }

        string acknowledgementDigest = ComputeAcknowledgementDigest(applied);
        try
        {
            // This hook is deliberately between the backend acknowledgement and the durable
            // checkpoint commit so the denominator can fault the exact split boundary.
            if (_afterManifestAcknowledgement is not null)
            {
                await _afterManifestAcknowledgement().ConfigureAwait(false);
            }

            await _checkpointStore.CommitAsync(
                new ManifestApplyCheckpoint(operationId, desiredStateDigest, acknowledgementDigest),
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return new ManifestApplyOutcome(
                OperationBackendStep.From(stepName, applied, mutatesState: true),
                Acknowledged: false,
                AcknowledgementDigest: acknowledgementDigest,
                Failure: $"backend acknowledged the manifest but checkpoint commit failed ({exception.GetType().Name})");
        }

        return new ManifestApplyOutcome(
            OperationBackendStep.From(stepName, applied, mutatesState: true),
            Acknowledged: true,
            AcknowledgementDigest: acknowledgementDigest,
            Failure: null);
    }

    private static string ComputeDesiredStateDigest(ManifestApplyRequest desiredState)
        => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(desiredState))).ToLowerInvariant();

    private static string ComputeAcknowledgementDigest(BackendCallResult result)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(result.PayloadPreview))).ToLowerInvariant();

    private sealed record ManifestApplyOutcome(
        OperationBackendStep Step,
        bool Acknowledged,
        string? AcknowledgementDigest,
        string? Failure);

    internal async Task<GitOpsExecutionResult> SubmitAndPollAsync(
        string operationId,
        string reason,
        GitOpsActuationDecision decision,
        List<OperationBackendStep> steps,
        List<string> findings,
        ActuationSpine.MutationGrant grant,
        CancellationToken cancellationToken,
        string? desiredStateDigest = null,
        string? manifestAcknowledgementDigest = null,
        string? approvalReference = null)
    {
        List<string> receiptBindingFailures = [];
        using BackendJsonResult submitted = await _gateway.SubmitDeployOperationJsonAsync(operationId, reason, grant, cancellationToken);
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
        string? receiptId = submitted.Payload is null ? null : DeployOperationReader.ReadActuatorReceiptId(submitted.Payload.RootElement);
        string? receiptOperationId = submitted.Payload is null ? null : DeployOperationReader.ReadActuatorReceiptOperationId(submitted.Payload.RootElement);
        if (desiredStateDigest is not null && submitted.Payload is not null)
        {
            receiptBindingFailures.AddRange(DeployOperationReader.ReadActuatorReceiptBindingFailures(
                submitted.Payload.RootElement,
                operationId,
                grant.IdempotencyKey,
                desiredStateDigest,
                manifestAcknowledgementDigest ?? string.Empty,
                approvalReference ?? string.Empty,
                decision.PolicyGate));
        }
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
                receiptId = DeployOperationReader.ReadActuatorReceiptId(polled.Payload.RootElement) ?? receiptId;
                receiptOperationId = DeployOperationReader.ReadActuatorReceiptOperationId(polled.Payload.RootElement) ?? receiptOperationId;
                if (desiredStateDigest is not null && DeployOperationReader.IsTerminal(status))
                {
                    receiptBindingFailures =
                    [
                        .. DeployOperationReader.ReadActuatorReceiptBindingFailures(
                            polled.Payload.RootElement,
                            operationId,
                            grant.IdempotencyKey,
                            desiredStateDigest,
                            manifestAcknowledgementDigest ?? string.Empty,
                            approvalReference ?? string.Empty,
                            decision.PolicyGate)
                    ];
                }
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

        if (resultStatus == GitOpsExecutionStatus.Succeeded && receiptBindingFailures.Count > 0)
        {
            findings.Add(
                $"Operation `{operationId}` reached success with incomplete actuator receipt lineage: {string.Join(", ", receiptBindingFailures)}.");
            return new GitOpsExecutionResult(
                Status: GitOpsExecutionStatus.Indeterminate,
                OperationId: operationId,
                ServerStatus: status,
                Mutated: true,
                Decision: decision,
                BackendSteps: steps,
                Findings: findings,
                BlockingReasons: receiptBindingFailures);
        }

        if (resultStatus == GitOpsExecutionStatus.Succeeded &&
            (string.IsNullOrWhiteSpace(receiptId) ||
             (!string.IsNullOrWhiteSpace(receiptOperationId) &&
              !string.Equals(receiptOperationId, operationId, StringComparison.Ordinal))))
        {
            findings.Add($"Operation `{operationId}` reached success without an authoritative actuator receipt bound to that operation; result is indeterminate.");
            findings.Add(
                "The submit succeeded and the operation polled to a terminal state, so backend state may have changed; " +
                "treat this as an unverified mutation and reconcile against the deploy-control operation before retrying.");
            return new GitOpsExecutionResult(
                Status: GitOpsExecutionStatus.Indeterminate,
                OperationId: operationId,
                ServerStatus: status,
                // The submit already succeeded and the operation reached a terminal state, so
                // cloud state may well have changed — only the receipt evidence is missing.
                // Reporting Mutated=false here would tell MCP consumers nothing happened and
                // would contradict the in-progress/terminal paths, which mark a successful
                // submission as mutated. The status stays fail-closed; the mutation attempt
                // is reported truthfully.
                Mutated: true,
                Decision: decision,
                BackendSteps: steps,
                Findings: findings,
                BlockingReasons: ["actuator-receipt-missing-or-mismatched"]);
        }

        if (resultStatus == GitOpsExecutionStatus.Succeeded)
        {
            findings.Add($"Authoritative actuator receipt `{receiptId}` is bound to operation `{operationId}`.");
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
