using Honua.DevOps.Agent.Operations.OperatorPolicy;
using OperatorPolicyModel = Honua.DevOps.Agent.Operations.OperatorPolicy.OperatorPolicy;

namespace Honua.DevOps.Agent.Operations.GitOps;

// Orchestrates a `rollback` of an existing deploy-control operation back to the prior
// known-good revision. Rollback is the highest-risk actuation, so it has its own gate:
//
//   * It reads the target operation's rollbackPlan classification first.
//   * A DATA-AFFECTING rollback (rollbackPlan.IsDataAffecting, i.e. any class other than
//     MetadataOnly, OR an unknown/absent plan which the server treats as destructive)
//     ALWAYS requires explicit approval. The executor never auto-issues it: it surfaces the
//     operationId, the classification, and the evidence for governed approval and STOPS.
//   * A non-data-affecting (MetadataOnly) rollback may proceed when policy/approval allows
//     (direct-allowed / break-glass), but is still routed through the server's own
//     OperatorApprovalGate — if the server refuses (403), the executor reports
//     approval-required and nothing mutates.
//
// As with sync/promote, EXECUTION_MODE=plan (default) makes the whole thing non-mutating.
internal sealed class RollbackExecutor(
    OperationRuntime runtime,
    BackendGateway gateway,
    OperatorPolicyModel policy)
{
    private readonly OperationRuntime _runtime = runtime;
    private readonly BackendGateway _gateway = gateway;
    private readonly OperatorPolicyModel _policy = policy;

    internal async Task<GitOpsExecutionResult> ExecuteRollbackAsync(
        string operationId,
        string reason,
        bool authorizationDryRun,
        string policyGate,
        CancellationToken cancellationToken)
    {
        GitOpsActuationDecision decision = GitOpsActuationDecision.Resolve(
            _runtime.ExecutionMode,
            _policy,
            authorizationDryRun,
            policyGate);

        List<OperationBackendStep> steps = [];
        List<string> findings =
        [
            $"Actuation kind: rollback (operation {operationId}).",
            $"Execution mode: {decision.Mode}; approval mode: {decision.ApprovalMode}; policy gate: {decision.PolicyGate}.",
            decision.Rationale
        ];

        // SAFETY INVARIANT 1: plan posture (default) -> zero mutation, zero backend calls.
        if (!decision.Mutating)
        {
            findings.Add($"No rollback was issued for `{operationId}`; plan-only posture.");
            return new GitOpsExecutionResult(
                Status: GitOpsExecutionStatus.PlanOnly,
                OperationId: operationId,
                ServerStatus: null,
                Mutated: false,
                Decision: decision,
                BackendSteps: steps,
                Findings: findings,
                BlockingReasons: []);
        }

        // Read the operation first to classify the rollback before any mutating call.
        using BackendJsonResult current = await _gateway.GetDeployOperationJsonAsync(operationId, cancellationToken);
        steps.Add(GitOpsExecutor.ToStep("deploy-operation-read", current.CallResult, mutatesState: false));

        if (!current.CallResult.IsSuccess || current.Payload is null)
        {
            findings.Add($"Could not read deploy-control operation `{operationId}`; rollback not issued, no operation invented.");
            return new GitOpsExecutionResult(
                Status: GitOpsExecutionStatus.ContractUnavailable,
                OperationId: operationId,
                ServerStatus: null,
                Mutated: false,
                Decision: decision,
                BackendSteps: steps,
                Findings: findings,
                BlockingReasons: ["rollback-operation-not-found"]);
        }

        bool dataAffecting = DeployOperationReader.ReadRollbackIsDataAffecting(current.Payload.RootElement);
        bool requiresExplicitApproval = DeployOperationReader.ReadRollbackRequiresExplicitApproval(current.Payload.RootElement);
        string rollbackClass = DeployOperationReader.ReadRollbackClass(current.Payload.RootElement) ?? "unknown";
        string? serverStatus = DeployOperationReader.ReadStatus(current.Payload.RootElement);
        findings.Add($"Rollback classification: class={rollbackClass}; dataAffecting={dataAffecting}; requiresExplicitApproval={requiresExplicitApproval}.");

        // SAFETY INVARIANT (rollback): a data-affecting / explicit-approval rollback is NEVER
        // auto-issued. Refuse locally and surface the operationId + classification for a
        // governed approval, even under direct-allowed/break-glass — we never bypass the
        // OperatorApprovalGate for data-affecting ops.
        if (dataAffecting || requiresExplicitApproval)
        {
            findings.Add($"Refusing to auto-issue a data-affecting rollback for `{operationId}`; explicit governed approval is required.");
            findings.Add("Route this rollback through the deploy-control rollback path with an approval record; the agent will not bypass the OperatorApprovalGate.");
            return new GitOpsExecutionResult(
                Status: GitOpsExecutionStatus.ApprovalRequired,
                OperationId: operationId,
                ServerStatus: serverStatus,
                Mutated: false,
                Decision: decision,
                BackendSteps: steps,
                Findings: findings,
                BlockingReasons: ["data-affecting-rollback-requires-approval"]);
        }

        // Non-data-affecting (MetadataOnly) rollback: proceed only when policy allows direct
        // execution; otherwise pause for external approval (pr-first default).
        if (!decision.MayAutoSubmit)
        {
            findings.Add($"Approval mode `{decision.ApprovalMode}` requires external approval before issuing even a non-data-affecting rollback for `{operationId}`.");
            return new GitOpsExecutionResult(
                Status: GitOpsExecutionStatus.AwaitingApproval,
                OperationId: operationId,
                ServerStatus: serverStatus,
                Mutated: false,
                Decision: decision,
                BackendSteps: steps,
                Findings: findings,
                BlockingReasons: []);
        }

        using BackendJsonResult rolledBack = await _gateway.RollbackDeployOperationJsonAsync(operationId, reason, cancellationToken);
        steps.Add(GitOpsExecutor.ToStep("deploy-operation-rollback", rolledBack.CallResult, mutatesState: rolledBack.CallResult.IsSuccess));

        if (!rolledBack.CallResult.IsSuccess)
        {
            // Includes the server-side OperatorApprovalGate 403 if it still applies. Nothing mutated.
            findings.Add($"Rollback refused by deploy-control ({rolledBack.CallResult.Detail}); operation `{operationId}` was not rolled back.");
            return new GitOpsExecutionResult(
                Status: GitOpsExecutionStatus.ApprovalRequired,
                OperationId: operationId,
                ServerStatus: rolledBack.Payload is null ? serverStatus : DeployOperationReader.ReadStatus(rolledBack.Payload.RootElement),
                Mutated: false,
                Decision: decision,
                BackendSteps: steps,
                Findings: findings,
                BlockingReasons: rolledBack.Payload is null
                    ? ["rollback-refused"]
                    : DeployOperationReader.ReadBlockingReasons(rolledBack.Payload.RootElement));
        }

        string? finalStatus = rolledBack.Payload is null ? null : DeployOperationReader.ReadStatus(rolledBack.Payload.RootElement);
        findings.Add($"Rollback issued for `{operationId}`; resulting status: {finalStatus ?? "unknown"}.");
        return new GitOpsExecutionResult(
            Status: DeployOperationReader.IsRolledBack(finalStatus)
                ? GitOpsExecutionStatus.RolledBack
                : GitOpsExecutionStatus.Succeeded,
            OperationId: operationId,
            ServerStatus: finalStatus,
            Mutated: true,
            Decision: decision,
            BackendSteps: steps,
            Findings: findings,
            BlockingReasons: []);
    }
}
