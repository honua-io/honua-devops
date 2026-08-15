using Honua.DevOps.Agent.Operations.OperatorPolicy;
using OperatorPolicyModel = Honua.DevOps.Agent.Operations.OperatorPolicy.OperatorPolicy;

namespace Honua.DevOps.Agent.Operations.GitOps;

// Orchestrates a `promote` (advance a validated revision into the next environment, e.g.
// staging -> prod) THROUGH the server deploy-control operation. A promotion is a deploy of
// an already-validated revision into a higher environment, so it reuses the same governed
// create -> approval-gate -> submit -> poll spine as sync. The promotion-specific safety
// rules live in the toolkit's AuthorizeDeployment (promote-prod tier requires action
// `promote`; pr-first blocks direct execution), which is folded into `authorizationDryRun`
// here, and the deploy plan's own RequiresApproval which parks the op at AwaitingApproval.
internal sealed class PromotionExecutor(
    OperationRuntime runtime,
    BackendGateway gateway,
    OperatorPolicyModel policy)
{
    private readonly GitOpsExecutor _executor = new(runtime, gateway, policy);

    internal Task<GitOpsExecutionResult> ExecutePromotionAsync(
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
            runtime.ExecutionMode,
            policy,
            authorizationDryRun,
            policyGate);

        // A promotion advances the next environment; the deploy plan's RequiresApproval (set
        // for prod/gated promotions) parks the operation at AwaitingApproval, and pr-first
        // keeps MayAutoSubmit=false, so a prod promotion is never auto-submitted by default.
        return _executor.ActuateAsync(
            kind: "promote",
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
}
