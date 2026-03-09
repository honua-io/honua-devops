namespace Honua.DevOps.Agent.Operations.ServiceBundleReconciliation;

internal sealed record ServiceBundleReconciliationOperation(
    string Surface,
    string Availability,
    string ReadSource,
    string WriteTarget,
    string DriftCommand,
    string DiffSummary,
    IReadOnlyList<string> Semantics,
    IReadOnlyList<string> EvidenceRequirements);
