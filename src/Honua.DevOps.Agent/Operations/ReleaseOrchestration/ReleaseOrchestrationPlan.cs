namespace Honua.DevOps.Agent.Operations.ReleaseOrchestration;

internal sealed record ReleasePromotionPolicy(
    string Gate,
    IReadOnlyList<string> RequiredEvidence,
    IReadOnlyList<string> Blockers);

internal sealed record ReleaseRollbackPolicy(
    string FallbackMode,
    IReadOnlyList<string> Triggers,
    IReadOnlyList<string> RequiredEvidence);

internal sealed record ReleaseOrchestrationPlan(
    string Strategy,
    string MigrationMode,
    string PromotionMode,
    ReleasePromotionPolicy PromotionPolicy,
    ReleaseRollbackPolicy RollbackPolicy,
    IReadOnlyList<ReleaseStagePlan> Stages,
    IReadOnlyList<string> RollbackClasses,
    IReadOnlyList<ReleaseRollbackSemanticsPlan> RollbackSemantics,
    IReadOnlyList<ReleasePromotionStep> PromotionSequence);
