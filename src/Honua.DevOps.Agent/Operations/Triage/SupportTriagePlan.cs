using Honua.DevOps.Agent.Operations.GuidedFix;

namespace Honua.DevOps.Agent.Operations.Triage;

// Typed, planning-only output of the support-ticket triage tool. This is carried
// on OperationResponse as a [JsonIgnore] field (see OperationResponse) so the
// large structured plan lands in the audit journal via the C# object reference
// without bloating the LLM-facing wire shape. It records classification and a
// suggested NEXT action per ticket but never represents an executed action:
// every remediation stays behind the existing approval/execution gates.
internal sealed record SupportTriagePlan(
    int TotalTickets,
    int PendingTickets,
    int ClassifiedTickets,
    IReadOnlyList<SupportTicketTriage> Triages);

internal sealed record SupportTicketTriage(
    string TicketId,
    string Service,
    string Environment,
    SupportSeverity Severity,
    string Category,
    string SuggestedAction,
    string Confidence,
    int PriorityScore,
    string DiagnosisSummary,
    string? MatchedScenarioId,
    string? MatchedScenarioName,
    double MatchScore,
    IReadOnlyList<string> SuggestedRunbookSteps,
    IReadOnlyList<string> MissingEvidence,
    string RollbackPath);
