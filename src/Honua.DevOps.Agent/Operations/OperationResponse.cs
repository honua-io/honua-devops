using System.Text.Json.Serialization;
using Honua.DevOps.Agent.Operations.DesiredState;
using Honua.DevOps.Agent.Operations.GitOps;
using Honua.DevOps.Agent.Operations.ReleaseOrchestration;
using Honua.DevOps.Agent.Operations.ServiceBundleReconciliation;

namespace Honua.DevOps.Agent.Operations;

// [JsonIgnore] fields below are intentionally hidden from the LLM-facing wire
// shape: they are large structured objects the model can't act on directly,
// and they bloat the context window. The audit sink reads them via the C#
// object reference (not JSON), so they still land in the operation journal.
internal sealed record OperationResponse(
    string Status,
    string Summary,
    IReadOnlyList<string> Findings,
    IReadOnlyList<string> Actions,
    IReadOnlyList<string> ValidationChecks,
    IReadOnlyList<string> Risks,
    [property: JsonIgnore] OperationEvidence? Evidence = null,
    [property: JsonIgnore] GitOpsPlan? GitOpsPlan = null,
    [property: JsonIgnore] ReleaseOrchestrationPlan? ReleaseOrchestration = null,
    [property: JsonIgnore] ServiceBundleReconciliationPlan? ServiceBundleReconciliation = null,
    [property: JsonIgnore] DesiredStateDriftReport? DesiredStateDrift = null,
    [property: JsonIgnore] IReadOnlyList<OperationBackendStep>? BackendSteps = null);
