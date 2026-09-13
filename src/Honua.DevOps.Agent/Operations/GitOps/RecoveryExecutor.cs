using Honua.DevOps.Agent.Operations.Actuation;
using Honua.DevOps.Agent.Operations.ConsoleBridge;
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
//        grant against the CURRENTLY OBSERVED operation -> ActuationSpine.TryAuthorizeRecovery
//        binds the same in-process ledger every other mutation uses -> the existing
//        deploy-control rollback endpoint reports recovery progress. This read-side check is
//        NOT an atomic target compare-and-set or a durable Git restoration/quarantine write.
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

        // Validate the caller even on an observation-only retry. A terminal operation is
        // server truth, not authority to widen the sealed recovery scope.
        if (!grant.TryAuthorize(requestedActor, requestedTarget, observedCandidateRevision ?? string.Empty,
                ActuationSpine.PermittedCompensation.RestorePriorRevision, DateTimeOffset.UtcNow, out string scopeRefusal)
            || !string.Equals(DeployOperationReader.ReadOperationId(current.Payload.RootElement), grant.OperationId, StringComparison.Ordinal)
            || !string.Equals(DeployOperationReader.ReadTargetId(current.Payload.RootElement), grant.Target, StringComparison.Ordinal))
        {
            return Result(GitOpsExecutionStatus.ApprovalRequired, serverStatus, false,
                ["recovery-scope-mismatch"], scopeRefusal);
        }

        // A new process must observe recovery already claimed by the server. Reusing the
        // in-memory spine does not prove restart safety; no POST is needed in these states.
        ServerOperationStatus recognized = ServerOperationStatusParser.Recognize(serverStatus);
        if (recognized is ServerOperationStatus.RolledBack or ServerOperationStatus.RollbackRequested
            or ServerOperationStatus.Failed or ServerOperationStatus.ManualInterventionRequired
            || DeployOperationReader.ReadProtectionPhase(current.Payload.RootElement) == "recovering")
        {
            return Observe(current, false);
        }

        string? protectionRefusal = ReleaseCapabilityGate.GetProtectedRecoveryScopeRefusal(grant, current.Payload.RootElement);
        if (protectionRefusal is not null)
        {
            return Result(GitOpsExecutionStatus.ApprovalRequired, serverStatus, false,
                ["recovery-protection-unavailable"], protectionRefusal);
        }

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
            // A failed HTTP acknowledgement cannot establish whether the provider mutated.
            // Observe the durable operation on retry; never assert that nothing happened.
            findings.Add($"Recovery acknowledgement unavailable ({recovered.CallResult.Detail}); observe operation `{grant.OperationId}` to resolve the outcome.");
            return new GitOpsExecutionResult(
                Status: GitOpsExecutionStatus.Indeterminate,
                OperationId: grant.OperationId,
                ServerStatus: recovered.Payload is null ? serverStatus : DeployOperationReader.ReadStatus(recovered.Payload.RootElement),
                Mutated: false,
                Decision: grant.Decision,
                BackendSteps: steps,
                Findings: findings,
                BlockingReasons: ["recovery-acknowledgement-unavailable", .. recovered.Payload is null
                    ? Array.Empty<string>()
                    : DeployOperationReader.ReadBlockingReasons(recovered.Payload.RootElement)]);
        }

        return Observe(recovered, true);

        GitOpsExecutionResult Observe(BackendJsonResult response, bool mutated)
        {
            string? status = response.Payload is null ? null : DeployOperationReader.ReadStatus(response.Payload.RootElement);
            if (response.Payload is null
                || !string.Equals(DeployOperationReader.ReadOperationId(response.Payload.RootElement), grant.OperationId, StringComparison.Ordinal)
                || !string.Equals(DeployOperationReader.ReadTargetId(response.Payload.RootElement), grant.Target, StringComparison.Ordinal)
                || !string.Equals(DeployOperationReader.ReadCandidateRevision(response.Payload.RootElement), grant.CandidateRevision, StringComparison.Ordinal))
            {
                return Result(GitOpsExecutionStatus.Indeterminate, status, mutated,
                    ["recovery-response-unverified"], "Recovery response identity is missing or does not match the approved operation.");
            }

            IReadOnlyList<string> blockers = DeployOperationReader.ReadBlockingReasons(response.Payload.RootElement);
            string outcome = ServerOperationStatusParser.Recognize(status) switch
            {
                ServerOperationStatus.RolledBack when blockers.Count == 0 => GitOpsExecutionStatus.RolledBack,
                ServerOperationStatus.RollbackRequested or ServerOperationStatus.Reconciling when blockers.Count == 0
                    => GitOpsExecutionStatus.InProgress,
                ServerOperationStatus.Failed or ServerOperationStatus.ManualInterventionRequired => GitOpsExecutionStatus.Failed,
                _ => GitOpsExecutionStatus.Indeterminate
            };

            // A rollback receipt says nothing about the Git desired-state branch. Do not
            // invent a commit, quarantine, or a guarantee about the next reconciliation.
            findings.Add("Desired-state restoration and candidate quarantine are unverified; no Git convergence receipt is available.");
            return Result(outcome, status, mutated, blockers,
                $"Server recovery status for `{grant.OperationId}`: {status ?? "unknown"}.");
        }

        GitOpsExecutionResult Result(string status, string? observedStatus, bool mutated,
            IReadOnlyList<string> blockers, string finding)
        {
            findings.Add(finding);
            return new(status, grant.OperationId, observedStatus, mutated, grant.Decision, steps, findings, blockers);
        }
    }
}
