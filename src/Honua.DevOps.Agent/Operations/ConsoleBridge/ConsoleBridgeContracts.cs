namespace Honua.DevOps.Agent.Operations.ConsoleBridge;

// Console-facing AI DevOps bridge contracts. These projections let Console create or
// view GitOps proposals, follow a single durable operation across PR/CI/promotion/SLO
// watch/rollback, and render advisory AI briefs without scraping Git, CI, or agent prose.
// honua-devops owns these DTOs and the mapping; honua-server and honua-console consume
// them through their own bounded child tickets.

// Sensitivity markers for evidence references surfaced to Console.
internal static class EvidenceSensitivity
{
    internal const string Public = "public";
    internal const string Internal = "internal";
    internal const string Redacted = "redacted";
}

// Bridge-facing status values that are not 1:1 with a server workflow status.
internal static class BridgeStatus
{
    internal const string Proposed = "proposed";
    internal const string TargetUnconfigured = "target-unconfigured";
    internal const string ContractUnavailable = "contract-unavailable";
    internal const string Advisory = "advisory";
    internal const string EvidenceMissing = "evidence-missing";
    internal const string Unknown = "unknown";
}

// Release-readiness classification surfaced on an explanation. These are the scenario
// buckets Console renders (issue #58 fixtures): everything green; advisory warnings that
// do not block; a hard block; missing/unparseable evidence; and a release whose safe path
// forward is a rollback. The bridge never invents these — it derives them from the supplied
// release-package evidence.
internal static class ReleaseReadiness
{
    internal const string Ready = "ready";
    internal const string Warning = "warning";
    internal const string Blocked = "blocked";
    internal const string Unknown = "unknown";
    internal const string RollbackRequired = "rollback-required";
}

// Rollback classification carried on an explanation so Console can render the rollback
// posture without parsing prose. "automatic" means the release can self-revert via the
// governed path; "manual" needs an operator-run rollback; "irreversible" means forward-fix
// only; "not-required" applies to a healthy/ready release; "unknown" when evidence is absent.
internal static class RollbackClass
{
    internal const string Automatic = "automatic";
    internal const string Manual = "manual";
    internal const string Irreversible = "irreversible";
    internal const string NotRequired = "not-required";
    internal const string Unknown = "unknown";
}

// A raw, traceable reference back to the system of record (server event/job/release/log
// or external CI/Git artifact). RawRef is the durable pointer; Url is a resolved link
// when a base URL is configured. Sensitivity tells Console whether the payload is safe
// to surface verbatim.
internal sealed record EvidenceRef(
    string Type,
    string Source,
    string? RawRef,
    string? Url,
    string Summary,
    string CapturedAt,
    string Sensitivity);

// A deep link into a workflow surface. Available is false when no base URL is configured
// so Console can render the link as pending rather than fabricating a destination.
internal sealed record WorkflowLink(
    string Rel,
    string Label,
    string? Href,
    bool Available);

// A suggested next action. Mutating actions carry RequiresApproval/MutatesState and a
// TargetOperationId so Console can route them through the governed submit/rollback path.
// The bridge never invokes these itself.
internal sealed record SuggestedAction(
    string Id,
    string Title,
    string Description,
    bool RequiresApproval,
    bool MutatesState,
    string? TargetOperationId,
    WorkflowLink? WorkflowLink,
    string Kind);

// A resource touched by an operation or brief (service, environment, deploy target,
// revision).
internal sealed record AffectedResource(
    string Kind,
    string Name,
    string? Environment,
    string? Detail);

// One stage of the operation lifecycle. Status is "evidence-missing" when the stage is
// not represented by the current honua-server deploy-control contract (e.g. PR/CI) so
// Console shows the gap instead of the bridge scraping GitHub or CI.
internal sealed record WorkflowStageStatus(
    string Stage,
    string Status,
    string Detail,
    IReadOnlyList<EvidenceRef> Evidence);

internal sealed record GitOpsProposalBridge(
    string ProposalId,
    string? OperationId,
    string IdempotencyKey,
    string Status,
    string Service,
    IReadOnlyList<string> TargetEnvironments,
    string DesiredRevision,
    string? CurrentRevision,
    string RequestedAction,
    string EffectiveAction,
    string Owner,
    bool ApprovalRequired,
    IReadOnlyList<WorkflowLink> WorkflowLinks,
    IReadOnlyList<EvidenceRef> Evidence,
    IReadOnlyList<SuggestedAction> SuggestedActions,
    string CreatedAt,
    string UpdatedAt);

internal sealed record AiDevOpsBrief(
    string BriefId,
    string? OperationId,
    string Title,
    string Summary,
    IReadOnlyList<AffectedResource> AffectedResources,
    IReadOnlyList<EvidenceRef> Evidence,
    IReadOnlyList<SuggestedAction> SuggestedActions,
    string Confidence,
    string Owner,
    string Status,
    IReadOnlyList<WorkflowLink> WorkflowLinks,
    string CreatedAt);

internal sealed record DevOpsOperationStatus(
    string OperationId,
    string Kind,
    string Status,
    string Phase,
    string? ProviderOperationId,
    WorkflowStageStatus Proposal,
    WorkflowStageStatus Pr,
    WorkflowStageStatus Ci,
    WorkflowStageStatus Promotion,
    WorkflowStageStatus Smoke,
    WorkflowStageStatus SloWatch,
    WorkflowStageStatus RollbackReadiness,
    WorkflowStageStatus RollbackExecution,
    IReadOnlyList<EvidenceRef> Evidence,
    IReadOnlyList<string> BlockingReasons,
    IReadOnlyList<string> Warnings,
    string LastUpdated);

// One structured section of a release-package explanation (compatibility, script coverage,
// PR preview, promotion gates, rollback plan, ...). Status uses ReleaseReadiness values so
// Console can colour the section without parsing Detail. Findings are short, already-redacted
// human-readable bullets; Evidence links back to the system of record.
internal sealed record ReleaseExplanationSection(
    string Section,
    string Status,
    string Detail,
    IReadOnlyList<string> Findings,
    IReadOnlyList<EvidenceRef> Evidence);

// A gate Console must clear before promotion (e.g. compatibility-clean, scripts-covered,
// approvals-met). Satisfied flags whether the supplied evidence clears it; Blocking marks a
// gate that fails the release outright rather than merely warning.
internal sealed record PromotionGate(
    string Id,
    string Label,
    bool Satisfied,
    bool Blocking,
    string Detail);

// The structured explanation of a release package. Read-only by construction: it interprets
// supplied release-package evidence and never creates server operations. Mode is
// "explanation" (read-only) or "proposal" (gated handoff); proposal mode still requires the
// governed create/submit path and never executes here. Summary is the single human-readable
// paragraph Console can show verbatim; everything else is structured so Console renders
// without scraping prose. ResidualRisk and RequiredApprovals make the post-merge risk and
// approval posture explicit; RollbackClassification states how the release can be undone.
internal sealed record ReleaseExplanation(
    string ExplanationId,
    string? OperationId,
    string CorrelationId,
    string Mode,
    string ReleaseId,
    string Service,
    IReadOnlyList<string> TargetEnvironments,
    string DesiredRevision,
    string Readiness,
    string Summary,
    IReadOnlyList<ReleaseExplanationSection> Sections,
    IReadOnlyList<PromotionGate> PromotionGates,
    IReadOnlyList<string> RequiredApprovals,
    IReadOnlyList<string> ResidualRisks,
    string RollbackClassification,
    IReadOnlyList<EvidenceRef> Evidence,
    IReadOnlyList<SuggestedAction> SuggestedActions,
    IReadOnlyList<WorkflowLink> WorkflowLinks,
    IReadOnlyList<string> BlockingReasons,
    string CreatedAt);

// Delegated-session access modes Console renders for the L2/L3 trust layer. These are the
// exact wire values of OperatorPolicy's SupportSessionAccess.ToConfigValue(): the bridge
// reuses them verbatim rather than minting a parallel vocabulary.
internal static class DelegatedSessionAccess
{
    internal const string Disabled = "disabled";
    internal const string ReadOnly = "read-only";
    internal const string OperatorScoped = "operator-scoped";
}

// Posture Console renders for the guided-fix mode a ticket resolves to. Mirrors
// GuidedFixMode.ToConfigValue() so Console never re-derives it from prose.
internal static class SupportPosture
{
    internal const string ReadOnlyTriage = "read-only-triage";
    internal const string GuidedFix = "guided-fix";
    internal const string OperatorScoped = "operator-scoped";
}

// Live delegated-session state for a ticket: the access mode the operator is actually
// allowed to use, the effective TTL (already min-clamped against policy), an absolute
// expiry timestamp Console can render a countdown from, and the customer-visible flag.
// Active is false when access is disabled or the session is not operator-scoped, so
// Console can show "no live session" without parsing the mode string. ExpiresAt is null
// when no session is active (nothing to expire) and is computed from EstablishedAt + TTL.
internal sealed record DelegatedSessionState(
    string TicketId,
    string AccessMode,
    string Posture,
    int TtlMinutes,
    string? EstablishedAt,
    string? ExpiresAt,
    bool CustomerVisible,
    bool Active,
    IReadOnlyList<string> RequiredApprovalContext);

// Console-consumable projection of the DiagnosisScorecard posted back to honua-support.
// OverallResult/CompositeScore are the scorecard's own derived pass/fail and 0-100 score;
// the booleans are surfaced individually so Console can render a per-criterion checklist.
// FailureModes and Evidence let Console explain a "fail" without re-running diagnosis.
internal sealed record DiagnosisScorecardBridge(
    string ScenarioId,
    string ScenarioName,
    string OverallResult,
    double CompositeScore,
    string Confidence,
    bool DiagnosisCorrect,
    bool RemediationSafe,
    bool PolicyCompliant,
    bool RollbackGuidanceCorrect,
    bool RecoveryVerified,
    bool ServiceHealthRestored,
    double EvidenceQuality,
    IReadOnlyList<string> FailureModes,
    IReadOnlyList<EvidenceRef> Evidence);

// Why a ticket was handed off (escalated) to an operator-scoped session. Trigger is a
// stable machine code (e.g. matched-fault-write-remediation, severity-escalation,
// access-requested); Signal is the concrete observation that fired it; Justification is
// the human-readable sentence Console can show verbatim. RollbackIntent and
// RequiredApprovalContext make the approval posture explicit. Escalated is false for a
// read-only/guided-fix ticket that never crossed the operator-scoped boundary, so Console
// can render "not escalated" without inspecting the posture string.
internal sealed record EscalationRationale(
    bool Escalated,
    string Trigger,
    string Signal,
    string Justification,
    string AccessScope,
    int TtlMinutes,
    string RollbackIntent,
    IReadOnlyList<string> RequiredApprovalContext);

// Aggregate console view of a support ticket's L2/L3 trust state: the live delegated
// session, the diagnosis scorecard, the escalation rationale, and the audit-journal
// references (JSONL records keyed by the support-triage operation scope) backing them.
// Read-only by construction: the bridge projects already-computed honua-devops state and
// never opens a session, posts a diagnosis, or escalates.
internal sealed record SupportTicketConsoleView(
    string TicketId,
    string Posture,
    string DiagnosisSummary,
    DelegatedSessionState Session,
    DiagnosisScorecardBridge Scorecard,
    EscalationRationale Escalation,
    IReadOnlyList<EvidenceRef> AuditReferences,
    string CreatedAt);

// Carrier for the structured TRUST state relayed back to honua-support alongside a posted
// diagnosis (honua-support#23). It bundles the same already-computed #70 projections the
// SupportTicketConsoleView holds — delegated session, diagnosis scorecard, escalation
// rationale — so SupportGateway can serialize the shared TRUST wire contract honua-support
// consumes verbatim without recomputing any trust state. Every member is optional: omit the
// whole carrier or any sub-projection when it does not apply to the ticket.
internal sealed record SupportTicketTrust(
    DelegatedSessionState? Session = null,
    DiagnosisScorecardBridge? Scorecard = null,
    EscalationRationale? Escalation = null);

// Carried on OperationResponse (JSON-ignored, like GitOpsPlan) so the structured
// projection is available in-process by object reference to callers that hold the
// response (the Console-facing bridge surface), while the LLM-facing wire shape stays
// compact. Like GitOpsPlan, it is not serialized to the model or persisted in the audit
// journal; the journal records the compact status/summary plus evidence and backend steps.
internal sealed record ConsoleBridgeProjection(
    string Kind,
    GitOpsProposalBridge? Proposal = null,
    DevOpsOperationStatus? OperationStatus = null,
    AiDevOpsBrief? Brief = null,
    ReleaseExplanation? ReleaseExplanation = null,
    SupportTicketConsoleView? SupportTicket = null);
