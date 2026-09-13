using Honua.DevOps.Agent.Operations.Actuation;
using Honua.DevOps.Agent.Operations.OperatorPolicy;
using OperatorPolicyModel = Honua.DevOps.Agent.Operations.OperatorPolicy.OperatorPolicy;

namespace Honua.DevOps.Agent.Operations.GitOps;

// Declares and executes a bounded deployment recovery (issue #191).
//
// This is NOT a model-facing tool: it is deliberately absent from CapabilityToolset and never
// takes a fresh, caller-supplied operationId/reason the way RollbackExecutor's model-invocable
// counterpart does. It exists to be triggered deterministically once a deployment's safety
// policy fires (honua-server#4618's durable post-activation observation window) -- "no second
// model decision is required" -- consuming a DeploymentRecoveryGrant minted at the moment the
// deployment itself was approved:
//
//   deployment approved -> ActuationSpine.AuthorizeRecoveryGrant seals actor/tenant/target/
//     prior+candidate revisions/policy digest/expiry/compensation into a DeploymentRecoveryGrant
//     -> [later, when the trigger fires] RecoveryExecutor.ExecuteRecoveryAsync re-verifies the
//        grant against the CURRENTLY OBSERVED operation (compare-and-set: refuse if the
//        candidate has moved) -> ActuationSpine.TryAuthorizeRecovery binds the same at-most-once
//        ledger every other mutation uses -> the existing deploy-control rollback endpoint
//        restores PriorRevision and the failed CandidateRevision is recorded as quarantined.
//
// The server's own OperatorApprovalGate is still honored on the wire: a grant proves the
// DECLARED scope was approved, it does not bypass the server's own data-affecting check.
internal sealed class RecoveryExecutor(
    OperationRuntime runtime,
    BackendGateway gateway,
    OperatorPolicyModel policy,
    ActuationSpine? spine = null)
{
    private readonly OperationRuntime _runtime = runtime;
    private readonly BackendGateway _gateway = gateway;
    private readonly OperatorPolicyModel _policy = policy;
    private readonly ActuationSpine _spine = spine ?? new ActuationSpine(runtime, policy);

    internal async Task<GitOpsExecutionResult> ExecuteRecoveryAsync(
        ActuationSpine.DeploymentRecoveryGrant grant,
        string requestedActor,
        string requestedTarget,
        string reason,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(grant);

        OperationResponse? capabilityRefusal = ReleaseCapabilityGate.GetProtectedRecoveryRefusal(_runtime);
        if (capabilityRefusal is not null)
        {
            return new GitOpsExecutionResult(
                Status: GitOpsExecutionStatus.ExperimentalDisabled,
                OperationId: grant.OperationId,
                ServerStatus: null,
                Mutated: false,
                Decision: GitOpsActuationDecision.PlanOnly(
                    _policy.ApprovalMode.ToConfigValue(),
                    "protected-recovery-disabled",
                    capabilityRefusal.Summary),
                BackendSteps: [],
                Findings: [.. capabilityRefusal.Findings, .. capabilityRefusal.Actions],
                BlockingReasons: ["protected-recovery-disabled"]);
        }

        List<OperationBackendStep> steps = [];
        List<string> findings =
        [
            $"Actuation kind: recovery (operation {grant.OperationId}, permitted compensation {grant.Compensation}).",
            $"Recovery grant bound to actor `{grant.Actor}`, tenant `{grant.Tenant}`, target `{grant.Target}`, expiring {grant.ExpiresAtUtc:O}."
        ];

        // SAFETY INVARIANT: the grant itself carries the decision sealed at approval time. Plan
        // posture there means zero mutation here, regardless of what fires the trigger.
        if (!grant.Decision.Mutating)
        {
            findings.Add($"No recovery was issued for `{grant.OperationId}`; the sealing decision was plan-only.");
            return new GitOpsExecutionResult(
                Status: GitOpsExecutionStatus.PlanOnly,
                OperationId: grant.OperationId,
                ServerStatus: null,
                Mutated: false,
                Decision: grant.Decision,
                BackendSteps: steps,
                Findings: findings,
                BlockingReasons: []);
        }

        // Read the operation first: recovery must consume server-owned truth, never a locally
        // invented candidate revision.
        using BackendJsonResult current = await _gateway.GetDeployOperationJsonAsync(grant.OperationId, cancellationToken);
        steps.Add(OperationBackendStep.From("deploy-operation-read", current.CallResult, mutatesState: false));

        if (!current.CallResult.IsSuccess || current.Payload is null)
        {
            findings.Add($"Could not read deploy-control operation `{grant.OperationId}`; recovery not issued, no operation invented.");
            return new GitOpsExecutionResult(
                Status: GitOpsExecutionStatus.ContractUnavailable,
                OperationId: grant.OperationId,
                ServerStatus: null,
                Mutated: false,
                Decision: grant.Decision,
                BackendSteps: steps,
                Findings: findings,
                BlockingReasons: ["recovery-operation-not-found"]);
        }

        string? observedCandidateRevision = DeployOperationReader.ReadCandidateRevision(current.Payload.RootElement);
        string? serverStatus = DeployOperationReader.ReadStatus(current.Payload.RootElement);
        findings.Add($"Observed candidate revision: {observedCandidateRevision ?? "unknown"}.");

        if (!_spine.TryAuthorizeRecovery(
                grant,
                requestedActor,
                requestedTarget,
                observedCandidateRevision ?? string.Empty,
                ActuationSpine.PermittedCompensation.RestorePriorRevision,
                out ActuationSpine.MutationGrant? mutationGrant,
                out string refusal))
        {
            findings.Add($"Recovery not authorized: {refusal}");
            return new GitOpsExecutionResult(
                Status: GitOpsExecutionStatus.ApprovalRequired,
                OperationId: grant.OperationId,
                ServerStatus: serverStatus,
                Mutated: false,
                Decision: grant.Decision,
                BackendSteps: steps,
                Findings: findings,
                BlockingReasons: ["recovery-not-authorized"]);
        }

        using BackendJsonResult recovered = await _gateway.RollbackDeployOperationJsonAsync(
            grant.OperationId,
            reason,
            mutationGrant!,
            cancellationToken);
        steps.Add(OperationBackendStep.From("deploy-operation-recover", recovered.CallResult, mutatesState: recovered.CallResult.IsSuccess));

        if (!recovered.CallResult.IsSuccess)
        {
            // Includes the server-side OperatorApprovalGate 403 if it still applies to this
            // classification. A pre-authorized grant proves the DECLARED scope was approved; it
            // does not bypass the server's own data-affecting check. Nothing mutated.
            findings.Add($"Recovery refused by deploy-control ({recovered.CallResult.Detail}); operation `{grant.OperationId}` was not compensated.");
            return new GitOpsExecutionResult(
                Status: GitOpsExecutionStatus.ApprovalRequired,
                OperationId: grant.OperationId,
                ServerStatus: recovered.Payload is null ? serverStatus : DeployOperationReader.ReadStatus(recovered.Payload.RootElement),
                Mutated: false,
                Decision: grant.Decision,
                BackendSteps: steps,
                Findings: findings,
                BlockingReasons: recovered.Payload is null
                    ? ["recovery-refused"]
                    : DeployOperationReader.ReadBlockingReasons(recovered.Payload.RootElement));
        }

        string? finalStatus = recovered.Payload is null ? null : DeployOperationReader.ReadStatus(recovered.Payload.RootElement);
        findings.Add($"Restored desired revision: {grant.PriorRevision}.");
        findings.Add(
            $"Quarantined rejected revision: {grant.CandidateRevision} " +
            $"(operation {grant.OperationId}, approval scope digest bound at grant issuance).");
        findings.Add($"Recovery issued for `{grant.OperationId}`; resulting status: {finalStatus ?? "unknown"}.");
        return new GitOpsExecutionResult(
            Status: DeployOperationReader.IsRolledBack(finalStatus)
                ? GitOpsExecutionStatus.RolledBack
                : GitOpsExecutionStatus.Succeeded,
            OperationId: grant.OperationId,
            ServerStatus: finalStatus,
            Mutated: true,
            Decision: grant.Decision,
            BackendSteps: steps,
            Findings: findings,
            BlockingReasons: []);
    }
}
