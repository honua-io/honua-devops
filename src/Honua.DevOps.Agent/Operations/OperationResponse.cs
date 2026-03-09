using Honua.DevOps.Agent.Operations.GitOps;
using Honua.DevOps.Agent.Operations.ReleaseOrchestration;
using Honua.DevOps.Agent.Operations.ServiceBundleReconciliation;

namespace Honua.DevOps.Agent.Operations;

internal sealed record OperationResponse(
    string Status,
    string Summary,
    IReadOnlyList<string> Findings,
    IReadOnlyList<string> Actions,
    IReadOnlyList<string> ValidationChecks,
    IReadOnlyList<string> Risks,
    OperationEvidence? Evidence = null,
    GitOpsPlan? GitOpsPlan = null,
    ReleaseOrchestrationPlan? ReleaseOrchestration = null,
    ServiceBundleReconciliationPlan? ServiceBundleReconciliation = null);
