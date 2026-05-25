using System.Text.Json.Serialization;
using Honua.DevOps.Agent.Operations.ConsoleBridge;
using Honua.DevOps.Agent.Operations.GitOps;
using Honua.DevOps.Agent.Operations.ReleaseOrchestration;
using Honua.DevOps.Agent.Operations.ServiceBundleReconciliation;

namespace Honua.DevOps.Agent.Operations;

// [JsonIgnore] fields below are intentionally hidden from the LLM-facing wire
// shape: they are large structured objects the model can't act on directly,
// and they bloat the context window. Evidence and BackendSteps are copied into
// the audit journal by Program.EmitAuditAsync via the C# object reference (not
// JSON). The typed plan/projection objects (GitOpsPlan, ReleaseOrchestration,
// ServiceBundleReconciliation, ConsoleBridge) are in-process projections read by
// in-process callers that hold the OperationResponse (e.g. the Console-facing
// bridge surface); they are not serialized to the model or persisted in the journal.
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
    [property: JsonIgnore] IReadOnlyList<OperationBackendStep>? BackendSteps = null,
    [property: JsonIgnore] ConsoleBridgeProjection? ConsoleBridge = null);
