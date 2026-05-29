using Honua.DevOps.Agent.Operations.GuidedFix;
using Honua.DevOps.Agent.Operations.OperatorPolicy;
using OperatorPolicyModel = Honua.DevOps.Agent.Operations.OperatorPolicy.OperatorPolicy;

namespace Honua.DevOps.Agent.Operations.Triage;

// Planning-only support-ticket triage. Classifies and prioritizes pending
// tickets by reusing the existing diagnostic machinery (GuidedFixPlanner +
// FaultCatalog) and emits a typed plan. It deliberately runs the diagnosis in a
// read-only posture (ExecutionMode.Plan / ExecutionTier.Plan) so it never
// resolves an operator-scoped escalation or write-capable remediation: the
// suggested action is the recommended NEXT step, not an executed one. Any actual
// fix remains behind the operator approval/execution gates handled elsewhere.
internal static class SupportTriagePlanner
{
    internal static SupportTicketTriage BuildTriage(SupportTicket ticket, OperatorPolicyModel policy)
    {
        // Force read-only triage: planning only, never escalate or execute here.
        GuidedFixResult guidedFix = GuidedFixPlanner.Build(
            ticket,
            policy,
            ExecutionMode.Plan,
            ExecutionTier.Plan,
            ReadOnlyBackendResult());

        FaultMatch? matchedFault = guidedFix.MatchedFault;
        string category = matchedFault?.FaultCategory ?? "unclassified";
        int priorityScore = ComputePriorityScore(ticket.Severity, guidedFix.Confidence, matchedFault);

        return new SupportTicketTriage(
            TicketId: ticket.TicketId,
            Service: ticket.Service,
            Environment: ticket.Environment,
            Severity: ticket.Severity,
            Category: category,
            SuggestedAction: guidedFix.RecommendedNextAction,
            Confidence: guidedFix.Confidence,
            PriorityScore: priorityScore,
            DiagnosisSummary: guidedFix.DiagnosisSummary,
            MatchedScenarioId: matchedFault?.ScenarioId,
            MatchedScenarioName: matchedFault?.ScenarioName,
            MatchScore: matchedFault?.MatchScore ?? 0,
            SuggestedRunbookSteps: matchedFault?.RemediationSteps ?? [],
            MissingEvidence: guidedFix.MissingEvidence,
            RollbackPath: matchedFault?.RollbackPath ?? "capture current revision before any change");
    }

    internal static SupportTriagePlan Build(
        IReadOnlyList<SupportTicket> pendingTickets,
        int totalTickets,
        OperatorPolicyModel policy)
    {
        List<SupportTicketTriage> triages = pendingTickets
            .Select(ticket => BuildTriage(ticket, policy))
            .OrderByDescending(triage => triage.PriorityScore)
            .ThenBy(triage => triage.TicketId, StringComparer.Ordinal)
            .ToList();

        int classified = triages.Count(triage => triage.MatchedScenarioId is not null);

        return new SupportTriagePlan(
            TotalTickets: totalTickets,
            PendingTickets: pendingTickets.Count,
            ClassifiedTickets: classified,
            Triages: triages);
    }

    // Priority blends severity (dominant) with diagnosis confidence so the
    // operator gets a stable, explainable ordering. Higher is more urgent.
    private static int ComputePriorityScore(
        SupportSeverity severity,
        string confidence,
        FaultMatch? matchedFault)
    {
        int severityWeight = severity switch
        {
            SupportSeverity.Critical => 1000,
            SupportSeverity.High => 750,
            SupportSeverity.Medium => 500,
            SupportSeverity.Low => 250,
            _ => 250
        };

        int confidenceWeight = confidence switch
        {
            "high" => 150,
            "medium" => 75,
            _ => 0
        };

        int matchWeight = matchedFault is null ? 0 : (int)Math.Round(matchedFault.MatchScore);

        return severityWeight + confidenceWeight + matchWeight;
    }

    private static BackendCallResult ReadOnlyBackendResult()
    {
        return new BackendCallResult(
            IsSuccess: true,
            Endpoint: "local://honua-devops/triage-planner",
            Detail: "read-only-triage",
            PayloadPreview: "diagnosis derived from ticket symptoms and attached evidence only");
    }
}
