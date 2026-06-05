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
    IReadOnlyList<string> RequiredApprovalContext,
    string Trigger = GuidedFixEscalation.AccessRequestedTrigger,
    string Signal = "operator-scoped access requested for the ticket")
{
    // Stable machine codes for the signal that caused the hand-off, so honua-support and
    // Console can render "why escalated" without parsing the justification sentence.
    internal const string MatchedFaultWriteRemediationTrigger = "matched-fault-write-remediation";
    internal const string SeverityEscalationTrigger = "severity-escalation";
    internal const string AccessRequestedTrigger = "access-requested";
}
