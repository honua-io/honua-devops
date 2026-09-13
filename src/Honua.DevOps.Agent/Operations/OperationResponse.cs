using System.Text.Json.Serialization;
using Honua.DevOps.Agent.Operations.ConsoleBridge;
using Honua.DevOps.Agent.Operations.Deliverable;
using Honua.DevOps.Agent.Operations.GitOps;
using Honua.DevOps.Agent.Operations.ReleaseOrchestration;
using Honua.DevOps.Agent.Operations.ServiceBundleReconciliation;
using Honua.DevOps.Agent.Operations.Actuation;

namespace Honua.DevOps.Agent.Operations;

// Large advisory projections remain in-process. Identity and exact evidence references
// are explicitly serialized through provisioningLineage and serverOperations, so CLI,
// MCP, receipts and the diagnostic audit replica can join them without summaries.
internal sealed record OperationResponse(
    string Status,
    string Summary,
    IReadOnlyList<string> Findings,
    IReadOnlyList<string> Actions,
    IReadOnlyList<string> ValidationChecks,
    IReadOnlyList<string> Risks,
    [property: JsonPropertyName("provisioningLineage")] ProvisioningLineage? ProvisioningLineage = null,
    [property: JsonIgnore] OperationEvidence? Evidence = null,
    [property: JsonIgnore] GitOpsPlan? GitOpsPlan = null,
    [property: JsonIgnore] ReleaseOrchestrationPlan? ReleaseOrchestration = null,
    [property: JsonIgnore] ServiceBundleReconciliationPlan? ServiceBundleReconciliation = null,
    [property: JsonIgnore] IReadOnlyList<OperationBackendStep>? BackendSteps = null,
    [property: JsonIgnore] ConsoleBridgeProjection? ConsoleBridge = null,
    [property: JsonIgnore] MetadataReleaseChangeSet? MetadataReleaseChangeSet = null,
    [property: JsonIgnore] DeliverableProjection? DeliverableLifecycle = null,
    [property: JsonIgnore] ActuationResult? Actuation = null)
{
    [JsonPropertyName("serverOperations")]
    public IReadOnlyList<ServerOperationLineage> ServerOperations { get; init; } =
        BackendSteps?.Where(step => step.ServerLineage is not null).Select(step => step.ServerLineage!).ToArray() ?? [];

    [JsonPropertyName("auditEventId")]
    public string AuditEventId { get; init; } = Guid.NewGuid().ToString("n");
}
