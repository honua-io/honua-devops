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

// Canonical proposal lifecycle Console aggregates across honua-server and honua-devops
// proposals (issue #78). These values map 1:1 onto the honua-server WorkflowOperationStatus
// enum (Honua.Core.Features.ControlPlane.Domain.WorkflowOperationStatus) so a console can
// render and resolve proposals from both systems on one timeline without a per-source fork.
// The lifecycle the issue states — Planned -> AwaitingApproval -> Submitted ->
// Succeeded/Failed/Rejected — is the spine; the additional Reconciling/RollbackRequested/
// RolledBack/ManualInterventionRequired values are carried verbatim so the projection never
// loses fidelity against the server enum. Rejected is the devops-side decision terminal a
// rejected approval resolves to (no execution); it has no distinct server enum member, so
// the mapping documents it as a decision-audit state layered on AwaitingApproval.
internal static class ProposalLifecycle
{
    internal const string Planned = "Planned";
    internal const string AwaitingApproval = "AwaitingApproval";
    internal const string Submitted = "Submitted";
    internal const string Reconciling = "Reconciling";
    internal const string Succeeded = "Succeeded";
    internal const string Failed = "Failed";
    internal const string Rejected = "Rejected";
    internal const string RollbackRequested = "RollbackRequested";
    internal const string RolledBack = "RolledBack";
    internal const string ManualInterventionRequired = "ManualInterventionRequired";

    // Used when the deploy-control status cannot be read; never a server enum value, so
    // Console can render the gap instead of a fabricated lifecycle state.
    internal const string Unknown = "unknown";
}

// The honua-server WorkflowOperationStatus members, as the single source of truth for the
// raw status strings the agent reads back from honua-server. The server emits these in either
// lowercased-PascalCase ("awaitingapproval") or hyphenated ("awaiting-approval") form; both
// fold to the same member here. Recognizing the vocabulary in ONE place keeps the GitOps/
// rollback executors, the ApprovalWaiter, and the Console bridge mappings from drifting apart
// when honua-server adds or renames a status — previously each site parsed the strings with its
// own hand-written switch/array and they could silently disagree.
internal enum ServerOperationStatus
{
    // The status string was absent or not a recognized server WorkflowOperationStatus member.
    Unrecognized = 0,
    Planned,
    AwaitingApproval,
    Submitted,
    Reconciling,
    Succeeded,
    Failed,
    RollbackRequested,
    RolledBack,
    ManualInterventionRequired
}

// Canonical recognizer for the honua-server WorkflowOperationStatus vocabulary. All status
// parsing on the devops side funnels through Recognize so the cross-repo contract is read in
// exactly one place.
internal static class ServerOperationStatusParser
{
    // Folds any casing/hyphenation of a server status onto its canonical member, or
    // ServerOperationStatus.Unrecognized for null/empty/unknown input. Hyphens are stripped so
    // "awaiting-approval", "AwaitingApproval", and "awaitingapproval" all map identically.
    internal static ServerOperationStatus Recognize(string? status)
        => Normalize(status) switch
        {
            "planned" => ServerOperationStatus.Planned,
            "awaitingapproval" => ServerOperationStatus.AwaitingApproval,
            "submitted" => ServerOperationStatus.Submitted,
            "reconciling" => ServerOperationStatus.Reconciling,
            "succeeded" => ServerOperationStatus.Succeeded,
            "failed" => ServerOperationStatus.Failed,
            "rollbackrequested" => ServerOperationStatus.RollbackRequested,
            "rolledback" => ServerOperationStatus.RolledBack,
            "manualinterventionrequired" => ServerOperationStatus.ManualInterventionRequired,
            _ => ServerOperationStatus.Unrecognized
        };

    // A status the server will not progress past: succeeded, failed, rolled-back, or parked at
    // manual-intervention-required. Used to decide when polling can stop.
    internal static bool IsTerminal(ServerOperationStatus status)
        => status is ServerOperationStatus.Succeeded
            or ServerOperationStatus.Failed
            or ServerOperationStatus.RolledBack
            or ServerOperationStatus.ManualInterventionRequired;

    internal static bool IsTerminal(string? status) => IsTerminal(Recognize(status));

    internal static bool IsSuccess(string? status) => Recognize(status) == ServerOperationStatus.Succeeded;

    internal static bool IsRolledBack(string? status) => Recognize(status) == ServerOperationStatus.RolledBack;

    internal static bool IsAwaitingApproval(string? status)
        => Recognize(status) == ServerOperationStatus.AwaitingApproval;

    internal static bool IsManualInterventionRequired(string? status)
        => Recognize(status) == ServerOperationStatus.ManualInterventionRequired;

    private static string Normalize(string? value)
        => (value ?? string.Empty).Trim().Replace("-", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
}

// Approve/reject decision kinds recorded against a proposal.
internal static class ProposalDecisionKind
{
    internal const string Approve = "approve";
    internal const string Reject = "reject";
}

// Auditable approve/reject decision recorded against a proposal. Mirrors the honua-server
// decision-audit fields (OperationAuditInfo.RequestedBy + Reason, plus the deploy-control
// submit/rollback request `reason`): every decision carries the deciding actor, a free-form
// reason, and the decision timestamp so Console can aggregate server + devops decisions on
// one audit trail. Decision is "approve" or "reject"; ResultingStatus is the canonical
// ProposalLifecycle value the decision moves the proposal toward (Submitted for approve,
// Rejected for reject). GovernedAction names the governed deploy-control verb the decision
// authorizes (submit for approve, none for reject); the bridge records the decision but never
// invokes that verb itself.
internal sealed record ProposalDecision(
    string Decision,
    string Actor,
    string Reason,
    string DecidedAt,
    string ResultingStatus,
    string GovernedAction);

// Provider-neutral proposal plan Console renders without scraping CI or Git: the human-
// readable diff/change summary, whether the create call was a dry-run (proposals are always
// recorded with submitImmediately=false, so DryRun is true), whether approval is required, a
// coarse risk classification, and the blocking reasons that prevent submission. Aligns with
// the server DeployPlan (RequiresApproval/BlockingReasons/Warnings) plus the change diff the
// console approval surface needs.
internal sealed record ProposalPlan(
    string DiffSummary,
    bool DryRun,
    bool RequiresApproval,
    string Risk,
    IReadOnlyList<string> BlockingReasons,
    IReadOnlyList<string> Warnings);

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

// Console-facing GitOps proposal projection aligned with the honua-server OperationProposal
// contract (issue #78). The leading fields are the bridge-local projection honua-console
// already consumes; the trailing fields (Kind, Requester, Agent, ProposalStatus, Plan,
// Decision) are the canonical OperationProposal fields the issue requires so Console can
// aggregate server + devops proposals on one approval surface:
//   * Kind        — proposal kind, always "gitops-deploy" for this bridge (server: deploy/
//                   rollback/migration/metadata-release).
//   * Requester   — the human/owner that requested the proposal (server OperationAuditInfo
//                   .RequestedBy). Mirrors Owner for this bridge.
//   * Agent       — the agent identity that recorded the proposal ("honua-devops").
//   * ProposalStatus — the canonical ProposalLifecycle value (1:1 with the server
//                   WorkflowOperationStatus enum); Status stays the bridge-local projection
//                   value (proposed/target-unconfigured/contract-unavailable) for back-compat.
//   * Plan        — diff + dry-run + risk + blocking reasons (ProposalPlan).
//   * Decision    — the recorded approve/reject decision audit (actor + reason), null until a
//                   decision is recorded.
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
    string UpdatedAt,
    string Kind,
    string Requester,
    string Agent,
    string ProposalStatus,
    ProposalPlan Plan,
    ProposalDecision? Decision = null);

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
