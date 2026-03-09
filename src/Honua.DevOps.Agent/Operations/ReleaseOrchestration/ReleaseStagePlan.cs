namespace Honua.DevOps.Agent.Operations.ReleaseOrchestration;

internal sealed record ReleaseStagePlan(
    ReleaseStageKind Kind,
    string Summary,
    string ExecutionCondition,
    string Gate,
    IReadOnlyList<string> EvidenceRequirements,
    IReadOnlyList<string> SuggestedCommands);
