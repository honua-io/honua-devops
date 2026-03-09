namespace Honua.DevOps.Agent.Operations.ReleaseOrchestration;

internal sealed record ReleaseRollbackSemanticsPlan(
    string ChangeClass,
    string Trigger,
    string RecoveryPath,
    IReadOnlyList<string> EvidenceRequirements);
