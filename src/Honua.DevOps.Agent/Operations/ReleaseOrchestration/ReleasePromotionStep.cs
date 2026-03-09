namespace Honua.DevOps.Agent.Operations.ReleaseOrchestration;

internal sealed record ReleasePromotionStep(
    string? SourceEnvironment,
    string TargetEnvironment,
    string Gate,
    IReadOnlyList<string> RequiredEvidence,
    IReadOnlyList<string> SuggestedCommands);
