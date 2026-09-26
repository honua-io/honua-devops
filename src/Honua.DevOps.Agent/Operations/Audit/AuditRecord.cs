using System.Text.Json.Serialization;

namespace Honua.DevOps.Agent.Operations.Audit;

internal sealed record AuditRecord(
    DateTimeOffset Timestamp,
    string SessionId,
    [property: JsonPropertyName("auditEventId")] string AuditEventId,
    string ToolName,
    IReadOnlyDictionary<string, string> Arguments,
    string Status,
    string Summary,
    bool Mutated,
    string ExecutionMode,
    string ExecutionTier,
    string ApprovalMode,
    string? Provider,
    IReadOnlyList<OperationBackendStep>? BackendSteps,
    OperationEvidence? Evidence,
    ProvisioningLineage? ProvisioningLineage = null,
    [property: JsonPropertyName("serverOperations")] IReadOnlyList<ServerOperationLineage>? ServerOperations = null)
{
    // JSONL is a diagnostic replica. This flag is always false; deleting the file does not
    // delete provisioning evidence or a server operation.
    [JsonPropertyName("authoritative")]
    public bool Authoritative => false;
}
