using System.Text;

namespace Honua.DevOps.Agent.Operations.Troubleshooting;

internal sealed record TroubleshootingReport(
    string RunId,
    DateTimeOffset Timestamp,
    IReadOnlyList<BlindEvaluationResult> Results)
{
    internal int TotalScenarios => Results.Count;
    internal int PassedScenarios => Results.Count(result => result.Scorecard.OverallResult == "pass");
    internal int FailedScenarios => Results.Count(result => result.Scorecard.OverallResult == "fail");
    internal double AverageCompositeScore => Results.Count > 0
        ? Results.Average(result => result.Scorecard.CompositeScore)
        : 0;

    internal string ToMarkdown()
    {
        StringBuilder sb = new();
        sb.AppendLine($"# Troubleshooting Evaluation Report");
        sb.AppendLine();
        sb.AppendLine($"- **Run ID**: {RunId}");
        sb.AppendLine($"- **Timestamp**: {Timestamp:O}");
        sb.AppendLine($"- **Total scenarios**: {TotalScenarios}");
        sb.AppendLine($"- **Passed**: {PassedScenarios}");
        sb.AppendLine($"- **Failed**: {FailedScenarios}");
        sb.AppendLine($"- **Average composite score**: {AverageCompositeScore:F1}%");
        sb.AppendLine();
        sb.AppendLine("## Scenario Results");
        sb.AppendLine();
        sb.AppendLine("| Scenario | Result | Diagnosis | Remediation Safe | Policy | Score |");
        sb.AppendLine("|----------|--------|-----------|-----------------|--------|-------|");

        foreach (BlindEvaluationResult result in Results)
        {
            sb.AppendLine(
                $"| {result.Scorecard.ScenarioName} " +
                $"| {result.Scorecard.OverallResult} " +
                $"| {(result.Scorecard.DiagnosisCorrect ? "correct" : "incorrect")} " +
                $"| {(result.Scorecard.RemediationSafe ? "safe" : "unsafe")} " +
                $"| {(result.Scorecard.PolicyCompliant ? "compliant" : "violation")} " +
                $"| {result.Scorecard.CompositeScore:F1} |");
        }

        IEnumerable<BlindEvaluationResult> failures = Results.Where(result => result.Scorecard.OverallResult == "fail");
        if (failures.Any())
        {
            sb.AppendLine();
            sb.AppendLine("## Failure Details");
            sb.AppendLine();

            foreach (BlindEvaluationResult failure in failures)
            {
                sb.AppendLine($"### {failure.Scorecard.ScenarioName}");
                sb.AppendLine();
                sb.AppendLine($"- **Failure modes**: {string.Join(", ", failure.Scorecard.FailureModes)}");
                sb.AppendLine($"- **Evaluation mode**: {failure.Mode.ToConfigValue()}");
                sb.AppendLine($"- **Policy mode**: {failure.PolicyModeUsed}");
                sb.AppendLine($"- **Recovery result**: {failure.RecoveryResult}");
                sb.AppendLine();
            }
        }

        return sb.ToString();
    }

    internal string ToScorecardJson()
    {
        StringBuilder sb = new();
        sb.AppendLine("[");

        for (int i = 0; i < Results.Count; i++)
        {
            DiagnosisScorecard card = Results[i].Scorecard;
            sb.AppendLine("  {");
            sb.AppendLine($"    \"scenarioId\": \"{card.ScenarioId}\",");
            sb.AppendLine($"    \"scenarioName\": \"{card.ScenarioName}\",");
            sb.AppendLine($"    \"overallResult\": \"{card.OverallResult}\",");
            sb.AppendLine($"    \"compositeScore\": {card.CompositeScore:F1},");
            sb.AppendLine($"    \"diagnosisCorrect\": {card.DiagnosisCorrect.ToString().ToLowerInvariant()},");
            sb.AppendLine($"    \"remediationSafe\": {card.RemediationSafe.ToString().ToLowerInvariant()},");
            sb.AppendLine($"    \"policyCompliant\": {card.PolicyCompliant.ToString().ToLowerInvariant()},");
            sb.AppendLine($"    \"rollbackGuidanceCorrect\": {card.RollbackGuidanceCorrect.ToString().ToLowerInvariant()},");
            sb.AppendLine($"    \"recoveryVerified\": {card.RecoveryVerified.ToString().ToLowerInvariant()},");
            sb.AppendLine($"    \"serviceHealthRestored\": {card.ServiceHealthRestored.ToString().ToLowerInvariant()},");
            sb.AppendLine($"    \"evidenceQuality\": {card.EvidenceQuality:F1},");
            sb.AppendLine($"    \"diagnosisLatency\": \"{card.DiagnosisLatency}\",");
            sb.AppendLine($"    \"failureModes\": [{string.Join(", ", card.FailureModes.Select(mode => $"\"{mode}\""))}]");
            sb.Append("  }");
            if (i < Results.Count - 1) sb.Append(',');
            sb.AppendLine();
        }

        sb.AppendLine("]");
        return sb.ToString();
    }
}
