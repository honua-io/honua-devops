using Honua.DevOps.Agent.Operations.Troubleshooting;

namespace Honua.DevOps.Agent.Operations.GuidedFix;

internal sealed record GuidedFixResult(
    GuidedFixMode Mode,
    string DiagnosisSummary,
    string Confidence,
    IReadOnlyList<string> MissingEvidence,
    string RecommendedNextAction,
    IReadOnlyList<string> GuidedCommands,
    IReadOnlyList<string> ValidationSteps,
    GuidedFixEscalation? Escalation,
    FaultMatch? MatchedFault = null);

internal sealed record FaultMatch(
    string ScenarioId,
    string ScenarioName,
    string FaultCategory,
    double MatchScore,
    IReadOnlyList<string> MatchedIndicators,
    IReadOnlyList<string> RemediationSteps,
    string RollbackPath,
    string CleanupPath,
    RemediationScope RemediationScope);

internal sealed record GuidedFixEscalation(
    string Justification,
    string AccessScope,
    int TtlMinutes,
    string RollbackIntent,
    IReadOnlyList<string> RequiredApprovalContext);
