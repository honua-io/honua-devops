namespace Honua.DevOps.Agent.Operations.Observability;

internal sealed record OpsLoopBounds(
    int PageSize,
    int LookbackHours,
    int MaxFindings,
    int MaxEvidenceRefsPerFinding,
    int MaxTextCharacters,
    bool FindingsTruncated);

internal sealed record OpsLoopRecommendedAction(
    string Kind,
    string Summary,
    string Reason,
    bool AutoSafe,
    int BlastRadius,
    bool Supported);

internal sealed record OpsLoopProposal(
    string FindingId,
    string GatewayStatus,
    string? ProposalId,
    string? ExecutionOperationId,
    string? Message);

internal sealed record OpsLoopFindingReport(
    string FindingId,
    string Rule,
    string Severity,
    string Title,
    string Explanation,
    string DetectedAt,
    string? TargetId,
    string? OperationId,
    string? ReleaseVersion,
    IReadOnlyList<string> EvidenceRefs,
    OpsLoopRecommendedAction? RecommendedAction,
    IReadOnlyList<string> RelatedAlertIds,
    IReadOnlyList<string> RelatedEventIds,
    IReadOnlyList<string> RelatedDeployOperationIds,
    OpsLoopProposal? Proposal = null);

internal sealed record OpsLoopAlertEvidence(
    string EventId,
    string Severity,
    string OccurredAt,
    string LifecycleStatus,
    string? RuleName,
    string? ResourceRef);

internal sealed record OpsLoopEventEvidence(
    string EventId,
    string Kind,
    string Severity,
    string OccurredAt,
    string Title,
    string? Summary,
    string? OperationId,
    string? ReleaseId,
    string? ResourceRef);

internal sealed record OpsLoopDeployEvidence(
    string OperationId,
    string Kind,
    string Status,
    string? TargetId,
    string? Environment,
    string? CurrentRevision,
    string? DesiredRevision,
    string? CurrentPhase,
    string UpdatedAt);

internal sealed record OpsLoopReport(
    string Status,
    string ObservabilitySource,
    string? OverallHealth,
    string? PlatformReleaseVersion,
    bool? PlatformReleaseCoVersioned,
    IReadOnlyList<string> PlatformReleaseSkewedIds,
    bool SupportedKindsVerified,
    IReadOnlyList<string> SupportedKinds,
    IReadOnlyList<OpsLoopFindingReport> Findings,
    IReadOnlyList<OpsLoopAlertEvidence> AlertHistory,
    IReadOnlyList<OpsLoopEventEvidence> OperateTimeline,
    IReadOnlyList<OpsLoopDeployEvidence> DeployOperations,
    IReadOnlyList<string> McpToolsUsed,
    OpsLoopBounds Bounds,
    IReadOnlyList<string> Limitations);
