using Honua.DevOps.Agent.Operations.OperatorPolicy;
using Honua.DevOps.Agent.Operations.Troubleshooting;

namespace Honua.DevOps.Agent.Operations.GuidedFix;

internal static class GuidedFixPlanner
{
    internal static GuidedFixResult Build(
        SupportTicket ticket,
        OperatorPolicy.OperatorPolicy policy,
        ExecutionMode executionMode,
        ExecutionTier executionTier,
        BackendCallResult backendResult)
    {
        GuidedFixMode mode = ResolveMode(ticket, policy, executionMode, executionTier);
        FaultMatch? matchedFault = DiagnoseFromEvidence(ticket);
        string confidence = ComputeConfidence(ticket, backendResult, matchedFault);
        List<string> missingEvidence = BuildMissingEvidence(ticket, backendResult, matchedFault);
        string recommendedNextAction = ResolveNextAction(mode, confidence, missingEvidence, matchedFault, executionMode);
        List<string> guidedCommands = BuildGuidedCommands(ticket, mode, matchedFault, executionMode);
        List<string> validationSteps = BuildValidationSteps(ticket, mode, matchedFault);
        GuidedFixEscalation? escalation = mode == GuidedFixMode.OperatorScoped
            ? BuildEscalation(ticket, policy)
            : null;

        return new GuidedFixResult(
            Mode: mode,
            DiagnosisSummary: BuildDiagnosisSummary(ticket, backendResult, matchedFault),
            Confidence: confidence,
            MissingEvidence: missingEvidence,
            RecommendedNextAction: recommendedNextAction,
            GuidedCommands: guidedCommands,
            ValidationSteps: validationSteps,
            Escalation: escalation,
            MatchedFault: matchedFault);
    }

    internal static FaultMatch? DiagnoseFromEvidence(SupportTicket ticket)
    {
        string combined = $"{ticket.Symptoms} {ticket.AttachedEvidence}".ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(combined))
        {
            return null;
        }

        FaultMatch? bestMatch = null;

        foreach (FaultScenario scenario in FaultCatalog.All)
        {
            List<string> matchedIndicators = [];

            foreach (string symptom in scenario.ExpectedSymptoms)
            {
                if (ContainsAny(combined, ExtractKeyTerms(symptom)))
                {
                    matchedIndicators.Add($"symptom: {symptom}");
                }
            }

            foreach (string logPattern in scenario.ExpectedLogEvidence)
            {
                if (combined.Contains(logPattern.ToLowerInvariant(), StringComparison.Ordinal))
                {
                    matchedIndicators.Add($"log: {logPattern}");
                }
            }

            foreach (string metricPattern in scenario.ExpectedMetricEvidence)
            {
                if (ContainsAny(combined, ExtractKeyTerms(metricPattern)))
                {
                    matchedIndicators.Add($"metric: {metricPattern}");
                }
            }

            foreach (string healthPattern in scenario.ExpectedHealthEvidence)
            {
                if (ContainsAny(combined, ExtractKeyTerms(healthPattern)))
                {
                    matchedIndicators.Add($"health: {healthPattern}");
                }
            }

            if (matchedIndicators.Count == 0)
            {
                continue;
            }

            int totalIndicators = scenario.ExpectedSymptoms.Count +
                                  scenario.ExpectedLogEvidence.Count +
                                  scenario.ExpectedMetricEvidence.Count +
                                  scenario.ExpectedHealthEvidence.Count;
            double score = (double)matchedIndicators.Count / totalIndicators * 100.0;

            if (bestMatch is null || score > bestMatch.MatchScore)
            {
                bestMatch = new FaultMatch(
                    ScenarioId: scenario.Id,
                    ScenarioName: scenario.Name,
                    FaultCategory: scenario.Category.ToConfigValue(),
                    MatchScore: score,
                    MatchedIndicators: matchedIndicators,
                    RemediationSteps: scenario.SafeRemediationOptions,
                    RollbackPath: scenario.RollbackPath,
                    CleanupPath: scenario.CleanupPath,
                    RemediationScope: scenario.RemediationScope);
            }
        }

        return bestMatch;
    }

    private static GuidedFixMode ResolveMode(
        SupportTicket ticket,
        OperatorPolicy.OperatorPolicy policy,
        ExecutionMode executionMode,
        ExecutionTier executionTier)
    {
        if (executionMode == ExecutionMode.Plan ||
            executionTier is ExecutionTier.Observe or ExecutionTier.Plan)
        {
            return GuidedFixMode.ReadOnlyTriage;
        }

        if (policy.SupportSession.Access == SupportSessionAccess.Disabled)
        {
            return GuidedFixMode.ReadOnlyTriage;
        }

        string allowedAccess = ticket.AllowedAccessMode.Trim().ToLowerInvariant();

        if (allowedAccess is "read-only" or "read_only" or "triage")
        {
            return GuidedFixMode.ReadOnlyTriage;
        }

        if (policy.SupportSession.Access == SupportSessionAccess.OperatorScoped &&
            allowedAccess is "operator-scoped" or "operator_scoped" or "execute")
        {
            return GuidedFixMode.OperatorScoped;
        }

        return GuidedFixMode.GuidedFix;
    }

    private static string ComputeConfidence(
        SupportTicket ticket,
        BackendCallResult backendResult,
        FaultMatch? matchedFault)
    {
        if (matchedFault is not null && matchedFault.MatchScore >= 40.0)
        {
            return backendResult.IsSuccess ? "high" : "medium";
        }

        if (matchedFault is not null && matchedFault.MatchScore >= 20.0)
        {
            return "medium";
        }

        if (!backendResult.IsSuccess)
        {
            return "low";
        }

        bool hasSymptoms = !string.IsNullOrWhiteSpace(ticket.Symptoms);
        bool hasEvidence = !string.IsNullOrWhiteSpace(ticket.AttachedEvidence);

        return (hasSymptoms, hasEvidence) switch
        {
            (true, true) => "medium",
            (true, false) => "low",
            _ => "low"
        };
    }

    private static List<string> BuildMissingEvidence(
        SupportTicket ticket,
        BackendCallResult backendResult,
        FaultMatch? matchedFault)
    {
        List<string> missing = [];

        if (!backendResult.IsSuccess)
        {
            missing.Add("Live backend telemetry unavailable; diagnosis based on reported symptoms only.");
        }

        if (string.IsNullOrWhiteSpace(ticket.AttachedEvidence))
        {
            missing.Add("No attached evidence (logs, screenshots, traces). Request from customer to improve diagnosis accuracy.");
        }

        if (string.IsNullOrWhiteSpace(ticket.Symptoms))
        {
            missing.Add("Symptom description is empty. Request detailed reproduction steps.");
        }

        if (matchedFault is null && !string.IsNullOrWhiteSpace(ticket.Symptoms))
        {
            missing.Add("No known fault pattern matched. Attach log samples with error messages for better diagnosis.");
        }

        return missing;
    }

    private static string ResolveNextAction(
        GuidedFixMode mode,
        string confidence,
        IReadOnlyList<string> missingEvidence,
        FaultMatch? matchedFault,
        ExecutionMode executionMode)
    {
        if (matchedFault is null && confidence == "low")
        {
            return "request-more-evidence";
        }

        if (matchedFault is not null && executionMode == ExecutionMode.Execute &&
            mode == GuidedFixMode.OperatorScoped &&
            matchedFault.RemediationScope == RemediationScope.WriteCapable)
        {
            return "execute-remediation";
        }

        return mode switch
        {
            GuidedFixMode.ReadOnlyTriage => matchedFault is not null ? "diagnosis-ready" : "observe-only",
            GuidedFixMode.GuidedFix => "guided-customer-action",
            GuidedFixMode.OperatorScoped => "approval-required-escalation",
            _ => "observe-only"
        };
    }

    private static List<string> BuildGuidedCommands(
        SupportTicket ticket,
        GuidedFixMode mode,
        FaultMatch? matchedFault,
        ExecutionMode executionMode)
    {
        if (matchedFault is not null)
        {
            return BuildDiagnosisCommands(ticket, mode, matchedFault, executionMode);
        }

        if (mode == GuidedFixMode.ReadOnlyTriage)
        {
            return
            [
                $"# No known fault pattern matched for ticket {ticket.TicketId}",
                $"# Collect diagnostic evidence from {ticket.Environment}",
                $"curl -sf ${{HONUA_SMOKE_BASE_URL}}/healthz/ready",
                $"curl -sf ${{HONUA_SMOKE_BASE_URL}}/healthz/live",
                "# Attach recent error logs: kubectl logs <pod> --tail=200 | grep -i error",
                "# Attach recent events: kubectl get events --sort-by='.lastTimestamp' | tail -30",
                "# Check deployment state: honua-gitops status --service <service> --env " + ticket.Environment
            ];
        }

        return
        [
            $"# Generic triage for ticket {ticket.TicketId} in {ticket.Environment}",
            $"curl -sf ${{HONUA_SMOKE_BASE_URL}}/healthz/ready",
            $"curl -sf ${{HONUA_SMOKE_BASE_URL}}/healthz/live",
            "# Review deployment state and revision",
            "# Check connection pool, cache, and external dependency health",
            "# Attach logs and metrics to ticket for pattern matching"
        ];
    }

    private static List<string> BuildDiagnosisCommands(
        SupportTicket ticket,
        GuidedFixMode mode,
        FaultMatch matchedFault,
        ExecutionMode executionMode)
    {
        List<string> commands =
        [
            $"# Diagnosed: {matchedFault.ScenarioName} ({matchedFault.FaultCategory})",
            $"# Match confidence: {matchedFault.MatchScore:F0}% ({matchedFault.MatchedIndicators.Count} indicators matched)",
            $"# Matched signals: {string.Join("; ", matchedFault.MatchedIndicators)}",
            ""
        ];

        if (mode == GuidedFixMode.OperatorScoped && executionMode == ExecutionMode.Execute &&
            matchedFault.RemediationScope == RemediationScope.WriteCapable)
        {
            commands.Add("# EXECUTION MODE: operator will execute the following remediation steps");
            commands.Add("");

            for (int i = 0; i < matchedFault.RemediationSteps.Count; i++)
            {
                commands.Add($"# Step {i + 1}: {matchedFault.RemediationSteps[i]}");
            }

            commands.Add("");
            commands.Add($"# Rollback: {matchedFault.RollbackPath}");
            commands.Add($"# Cleanup: {matchedFault.CleanupPath}");
        }
        else if (mode is GuidedFixMode.GuidedFix or GuidedFixMode.OperatorScoped)
        {
            commands.Add("# GUIDED FIX: customer should execute the following steps");
            commands.Add("");

            for (int i = 0; i < matchedFault.RemediationSteps.Count; i++)
            {
                commands.Add($"# Step {i + 1}: {matchedFault.RemediationSteps[i]}");
            }

            commands.Add("");
            commands.Add($"# If fix fails, rollback: {matchedFault.RollbackPath}");
        }
        else
        {
            commands.Add("# READ-ONLY DIAGNOSIS: the following remediation is recommended");
            commands.Add("");

            for (int i = 0; i < matchedFault.RemediationSteps.Count; i++)
            {
                commands.Add($"# Recommended step {i + 1}: {matchedFault.RemediationSteps[i]}");
            }

            commands.Add("");
            commands.Add($"# Rollback path: {matchedFault.RollbackPath}");
        }

        commands.Add("");
        commands.Add("# Post-fix validation:");
        commands.Add($"curl -sf ${{HONUA_SMOKE_BASE_URL}}/healthz/ready");
        commands.Add($"curl -sf ${{HONUA_SMOKE_BASE_URL}}/healthz/live");

        return commands;
    }

    private static List<string> BuildValidationSteps(
        SupportTicket ticket,
        GuidedFixMode mode,
        FaultMatch? matchedFault)
    {
        List<string> steps =
        [
            $"Verify service health endpoints return 2xx in `{ticket.Environment}`.",
            "Confirm error rate returns to baseline after remediation.",
            "Validate no new alerts fired during the observation window."
        ];

        if (matchedFault is not null)
        {
            steps.Add($"Verify {matchedFault.FaultCategory} fault is fully resolved (cleanup: {matchedFault.CleanupPath}).");
        }

        if (ticket.RollbackExpected || matchedFault is not null)
        {
            steps.Add($"Verify rollback path is available: {matchedFault?.RollbackPath ?? "capture current revision before changes"}.");
        }

        if (mode == GuidedFixMode.OperatorScoped)
        {
            steps.Add("Confirm operator-scoped changes are captured in audit evidence.");
            steps.Add("Validate that support session TTL has not expired.");
        }

        return steps;
    }

    private static GuidedFixEscalation BuildEscalation(
        SupportTicket ticket,
        OperatorPolicy.OperatorPolicy policy)
    {
        return new GuidedFixEscalation(
            Justification: $"Escalation requested for ticket {ticket.TicketId} ({ticket.Severity.ToConfigValue()}) in {ticket.Environment}.",
            AccessScope: policy.SupportSession.Access.ToConfigValue(),
            TtlMinutes: Math.Min(ticket.TtlMinutes, policy.SupportSession.TtlMinutes),
            RollbackIntent: ticket.RollbackExpected ? "rollback-prepared" : "forward-fix-only",
            RequiredApprovalContext:
            [
                "approver-identity",
                $"ticket-reference:{ticket.TicketId}",
                $"access-scope:{policy.SupportSession.Access.ToConfigValue()}",
                $"ttl-minutes:{Math.Min(ticket.TtlMinutes, policy.SupportSession.TtlMinutes)}",
                "operator-justification",
                ticket.RollbackExpected ? "rollback-intent:prepared" : "rollback-intent:forward-fix"
            ]);
    }

    private static string BuildDiagnosisSummary(
        SupportTicket ticket,
        BackendCallResult backendResult,
        FaultMatch? matchedFault)
    {
        string backendStatus = backendResult.IsSuccess ? "backend reachable" : "backend unreachable";

        if (matchedFault is not null)
        {
            return $"Diagnosed {matchedFault.ScenarioName} ({matchedFault.FaultCategory}) " +
                   $"for ticket {ticket.TicketId} in `{ticket.Environment}` " +
                   $"with {matchedFault.MatchScore:F0}% confidence ({matchedFault.MatchedIndicators.Count} indicators). " +
                   $"Remediation: {matchedFault.RemediationSteps.FirstOrDefault() ?? "see guided commands"}. " +
                   $"Backend: {backendStatus}.";
        }

        return $"Triage for ticket {ticket.TicketId} ({ticket.Severity.ToConfigValue()}) " +
               $"in `{ticket.Environment}`: {backendStatus}. No known fault pattern matched. " +
               $"Symptoms: {(string.IsNullOrWhiteSpace(ticket.Symptoms) ? "not provided" : ticket.Symptoms.Trim())}.";
    }

    private static bool ContainsAny(string text, IEnumerable<string> terms)
    {
        return terms.Any(term => text.Contains(term, StringComparison.Ordinal));
    }

    private static IEnumerable<string> ExtractKeyTerms(string phrase)
    {
        string[] stopWords = ["the", "a", "an", "in", "on", "is", "or", "and", "to", "for", "from", "may", "can", "due", "still",
            "not", "all", "new", "but", "has", "with", "that", "this", "also", "after", "before", "been", "only", "pass"];
        return phrase.ToLowerInvariant()
            .Split([' ', ',', '/', '(', ')', '.'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(word => word.Length >= 4 && !stopWords.Contains(word))
            .Distinct();
    }
}
