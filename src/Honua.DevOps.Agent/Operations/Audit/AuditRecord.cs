namespace Honua.DevOps.Agent.Operations.Audit;

internal sealed record AuditRecord(
    DateTimeOffset Timestamp,
    string SessionId,
    string AuditEventId,
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
    ProvisioningLineage? ProvisioningLineage = null);
