using System.Globalization;
using Honua.DevOps.Agent.Operations.GuidedFix;
using Honua.DevOps.Agent.Operations.OperatorPolicy;
using Honua.DevOps.Agent.Operations.Troubleshooting;
using OperatorPolicyModel = Honua.DevOps.Agent.Operations.OperatorPolicy.OperatorPolicy;

namespace Honua.DevOps.Agent.Operations.ConsoleBridge;

// Projection layer that packages a support ticket's already-computed L2/L3 trust state
// (delegated session, diagnosis scorecard, escalation rationale) into stable, evidence-
// linked Console contracts. Read-only by construction: it maps state honua-devops already
// derived and never opens a session, posts a diagnosis, or escalates. Mirrors the
// ConsoleOperationBridge pattern — DTOs in ConsoleBridgeContracts, mapping here.
internal static class SupportSessionBridge
{
    // Build the live delegated-session state for a ticket. effectiveTtl is the TTL the
    // operator is actually bound to (already min-clamped against policy by the caller);
    // establishedAt anchors the expiry countdown Console renders. A session is only
    // "active" when access is enabled and the ticket resolved to an operator-scoped
    // posture — read-only/guided-fix tickets carry the policy ceiling but no live session.
    internal static DelegatedSessionState BuildSessionState(
        SupportTicket ticket,
        GuidedFixResult guidedFix,
        OperatorPolicyModel policy,
        int effectiveTtlMinutes,
        DateTimeOffset establishedAt)
    {
        string accessMode = policy.SupportSession.Access.ToConfigValue();
        string posture = guidedFix.Mode.ToConfigValue();
        bool active = policy.SupportSession.Access != SupportSessionAccess.Disabled
            && guidedFix.Mode == GuidedFixMode.OperatorScoped;

        string? establishedAtText = active ? Timestamp(establishedAt) : null;
        string? expiresAt = active
            ? Timestamp(establishedAt.AddMinutes(effectiveTtlMinutes))
            : null;

        IReadOnlyList<string> approvalContext =
            guidedFix.Escalation?.RequiredApprovalContext ?? [];

        return new DelegatedSessionState(
            TicketId: ticket.TicketId,
            AccessMode: accessMode,
            Posture: posture,
            TtlMinutes: effectiveTtlMinutes,
            EstablishedAt: establishedAtText,
            ExpiresAt: expiresAt,
            CustomerVisible: policy.SupportSession.CustomerVisible,
            Active: active,
            RequiredApprovalContext: approvalContext);
    }

    // Project the DiagnosisScorecard posted back to honua-support into a console-consumable
    // shape: the scorecard's own derived pass/fail and composite score, the per-criterion
    // booleans for a checklist, failure modes, and evidence references for "why it failed".
    internal static DiagnosisScorecardBridge BuildScorecard(
        DiagnosisScorecard scorecard,
        string confidence,
        IReadOnlyList<EvidenceRef> evidence)
    {
        return new DiagnosisScorecardBridge(
            ScenarioId: scorecard.ScenarioId,
            ScenarioName: scorecard.ScenarioName,
            OverallResult: scorecard.OverallResult,
            CompositeScore: Math.Round(scorecard.CompositeScore, 2),
            Confidence: confidence,
            DiagnosisCorrect: scorecard.DiagnosisCorrect,
            RemediationSafe: scorecard.RemediationSafe,
            PolicyCompliant: scorecard.PolicyCompliant,
            RollbackGuidanceCorrect: scorecard.RollbackGuidanceCorrect,
            RecoveryVerified: scorecard.RecoveryVerified,
            ServiceHealthRestored: scorecard.ServiceHealthRestored,
            EvidenceQuality: scorecard.EvidenceQuality,
            FailureModes: scorecard.FailureModes,
            Evidence: evidence);
    }

    // Project the escalation rationale ("why escalated") for a ticket. A read-only or
    // guided-fix ticket never crossed the operator-scoped boundary, so Escalated is false
    // and the rationale records the not-escalated posture without inventing a trigger.
    internal static EscalationRationale BuildEscalationRationale(GuidedFixResult guidedFix)
    {
        if (guidedFix.Escalation is not { } escalation)
        {
            return new EscalationRationale(
                Escalated: false,
                Trigger: "not-escalated",
                Signal: $"Ticket resolved to `{guidedFix.Mode.ToConfigValue()}`; no operator-scoped hand-off occurred.",
                Justification: guidedFix.DiagnosisSummary,
                AccessScope: SupportPosture.ReadOnlyTriage,
                TtlMinutes: 0,
                RollbackIntent: "not-applicable",
                RequiredApprovalContext: []);
        }

        return new EscalationRationale(
            Escalated: true,
            Trigger: escalation.Trigger,
            Signal: escalation.Signal,
            Justification: escalation.Justification,
            AccessScope: escalation.AccessScope,
            TtlMinutes: escalation.TtlMinutes,
            RollbackIntent: escalation.RollbackIntent,
            RequiredApprovalContext: escalation.RequiredApprovalContext);
    }

    // An audit-journal reference for the support-triage operation backing this ticket.
    // RawRef is the stable JSONL scope (matching OperationEvidence.Scope); Url points at
    // the file-backed journal when one is configured, so Console can deep-link to the
    // append-only record rather than the bridge embedding raw audit lines.
    internal static EvidenceRef AuditReference(string scope, string auditHookTarget, string capturedAt)
    {
        string? url = OperationJournalUrl(auditHookTarget);
        bool fileBacked = url is not null;
        return new EvidenceRef(
            Type: "audit-journal",
            Source: "honua-devops",
            RawRef: $"audit-scope:{scope}",
            Url: url,
            Summary: fileBacked
                ? $"Append-only JSONL audit record for `{scope}`."
                : $"Audit sink `{auditHookTarget}` is not file-backed; records stream to the configured sink for `{scope}`.",
            CapturedAt: capturedAt,
            Sensitivity: EvidenceSensitivity.Internal);
    }

    private static string? OperationJournalUrl(string auditHookTarget)
    {
        if (!Audit.OperationJournal.TryResolveJournalPath(auditHookTarget, out string path, out _))
        {
            return null;
        }

        return new Uri(path).ToString();
    }

    private static string Timestamp(DateTimeOffset value)
        => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}
