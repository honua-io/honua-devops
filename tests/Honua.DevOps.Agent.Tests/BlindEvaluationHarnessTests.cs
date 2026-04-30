using Honua.DevOps.Agent.Operations;
using Honua.DevOps.Agent.Operations.Troubleshooting;

namespace Honua.DevOps.Agent.Tests;

public class BlindEvaluationHarnessTests
{
    [Fact]
    public void BuildBlindPrompt_DoesNotExposeScenarioIdOrInjectionMethod()
    {
        FaultScenario scenario = FaultCatalog.Resolve("FAULT-001")!;

        BlindEvaluationRequest request = BlindEvaluationHarness.BuildBlindPrompt(
            scenario,
            EvaluationMode.ReadOnly);

        Assert.True(BlindEvaluationHarness.ValidateBlindness(request, scenario),
            "Blind prompt should not contain scenario ID or injection method.");
        Assert.DoesNotContain("FAULT-001", request.IncidentSymptoms, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(scenario.InjectionMethod, request.IncidentSymptoms, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildBlindPrompt_IncludesSymptomsAndEvidence()
    {
        FaultScenario scenario = FaultCatalog.Resolve("FAULT-001")!;

        BlindEvaluationRequest request = BlindEvaluationHarness.BuildBlindPrompt(
            scenario,
            EvaluationMode.ReadOnly);

        Assert.False(string.IsNullOrWhiteSpace(request.IncidentSymptoms));
        Assert.False(string.IsNullOrWhiteSpace(request.EnvironmentContext));
        Assert.False(string.IsNullOrWhiteSpace(request.LogEvidence));
        Assert.False(string.IsNullOrWhiteSpace(request.MetricEvidence));
    }

    [Fact]
    public void BuildBlindPrompt_SetsReadOnlyEvaluationMode()
    {
        FaultScenario scenario = FaultCatalog.Resolve("FAULT-005")!;
        BlindEvaluationRequest request = BlindEvaluationHarness.BuildBlindPrompt(scenario, EvaluationMode.ReadOnly);
        Assert.Equal(EvaluationMode.ReadOnly, request.EvaluationMode);
        Assert.Equal("read-only", request.EvaluationMode.ToConfigValue());
    }

    [Fact]
    public void BuildBlindPrompt_SetsGuidedWriteEvaluationMode()
    {
        FaultScenario scenario = FaultCatalog.Resolve("FAULT-005")!;
        BlindEvaluationRequest request = BlindEvaluationHarness.BuildBlindPrompt(scenario, EvaluationMode.GuidedWrite);
        Assert.Equal(EvaluationMode.GuidedWrite, request.EvaluationMode);
        Assert.Equal("guided-write", request.EvaluationMode.ToConfigValue());
    }

    [Fact]
    public void BuildBlindPrompt_SetsExecuteLowerEnvEvaluationMode()
    {
        FaultScenario scenario = FaultCatalog.Resolve("FAULT-005")!;
        BlindEvaluationRequest request = BlindEvaluationHarness.BuildBlindPrompt(scenario, EvaluationMode.ExecuteLowerEnv);
        Assert.Equal(EvaluationMode.ExecuteLowerEnv, request.EvaluationMode);
        Assert.Equal("execute-lower-env", request.EvaluationMode.ToConfigValue());
    }

    [Fact]
    public void ValidateBlindness_FailsWhenScenarioIdLeaks()
    {
        FaultScenario scenario = FaultCatalog.Resolve("FAULT-003")!;

        BlindEvaluationRequest leakyRequest = new(
            IncidentSymptoms: "FAULT-003 is happening",
            EnvironmentContext: "aws/ecs",
            HealthStatus: "degraded",
            LogEvidence: "auth error",
            MetricEvidence: "cache errors",
            EvaluationMode: EvaluationMode.ReadOnly);

        Assert.False(BlindEvaluationHarness.ValidateBlindness(leakyRequest, scenario));
    }

    [Fact]
    public void ValidateBlindness_FailsWhenInjectionMethodLeaks()
    {
        FaultScenario scenario = FaultCatalog.Resolve("FAULT-002")!;

        BlindEvaluationRequest leakyRequest = new(
            IncidentSymptoms: "DB unreachable after remove-app-sg-ingress-rule",
            EnvironmentContext: "aws/ecs",
            HealthStatus: "failing",
            LogEvidence: "timeout",
            MetricEvidence: "errors",
            EvaluationMode: EvaluationMode.ReadOnly);

        Assert.False(BlindEvaluationHarness.ValidateBlindness(leakyRequest, scenario));
    }

    [Fact]
    public void ValidateBlindness_PassesForAllCatalogScenarios()
    {
        foreach (FaultScenario scenario in FaultCatalog.All)
        {
            BlindEvaluationRequest request = BlindEvaluationHarness.BuildBlindPrompt(
                scenario, EvaluationMode.ReadOnly);

            Assert.True(
                BlindEvaluationHarness.ValidateBlindness(request, scenario),
                $"Blind prompt for {scenario.Id} ({scenario.Name}) leaks scenario identity.");
        }
    }

    [Fact]
    public void Evaluate_ProducesCompleteResult()
    {
        FaultScenario scenario = FaultCatalog.Resolve("FAULT-010")!;
        BlindEvaluationRequest request = BlindEvaluationHarness.BuildBlindPrompt(
            scenario, EvaluationMode.GuidedWrite);

        OperationResponse agentResponse = new(
            Status: "triage-ready",
            Summary: "CrashLoopBackOff detected",
            Findings: ["Pod restart loop", "Image pull failure"],
            Actions: ["kubectl rollout undo", "Verify image exists"],
            ValidationChecks: ["Pod ready", "Rollout complete"],
            Risks: ["Rolling back may lose in-flight state"]);

        DiagnosisScorecard scorecard = DiagnosisScorecardBuilder.Build(
            scenario,
            agentResponse,
            diagnosisCorrect: true,
            diagnosisLatency: "2.1s",
            evidenceQuality: 85.0,
            remediationSafe: true,
            policyCompliant: true,
            rollbackGuidanceCorrect: true,
            recoveryVerified: true,
            serviceHealthRestored: true);

        BlindEvaluationResult result = BlindEvaluationHarness.Evaluate(
            scenario,
            request,
            agentResponse,
            scorecard,
            policyModeUsed: "plan",
            actionsAttempted: ["kubectl rollout undo"],
            recoveryResult: "recovered");

        Assert.Equal(scenario.Id, result.ScenarioId);
        Assert.Equal(EvaluationMode.GuidedWrite, result.Mode);
        Assert.Equal("plan", result.PolicyModeUsed);
        Assert.Equal("recovered", result.RecoveryResult);
        Assert.Equal("pass", result.Scorecard.OverallResult);
        Assert.Empty(result.Scorecard.FailureModes);
    }

    [Fact]
    public void DiagnosisScorecardBuilder_DetectsWrongRootCause()
    {
        FaultScenario scenario = FaultCatalog.Resolve("FAULT-001")!;
        OperationResponse response = CreateMinimalResponse();

        DiagnosisScorecard scorecard = DiagnosisScorecardBuilder.Build(
            scenario, response,
            diagnosisCorrect: false,
            diagnosisLatency: "5.0s",
            evidenceQuality: 50.0,
            remediationSafe: true,
            policyCompliant: true,
            rollbackGuidanceCorrect: true,
            recoveryVerified: false,
            serviceHealthRestored: false);

        Assert.Equal("fail", scorecard.OverallResult);
        Assert.Contains("wrong-root-cause", scorecard.FailureModes);
    }

    [Fact]
    public void DiagnosisScorecardBuilder_DetectsUnsafeRemediation()
    {
        FaultScenario scenario = FaultCatalog.Resolve("FAULT-001")!;
        OperationResponse response = CreateMinimalResponse();

        DiagnosisScorecard scorecard = DiagnosisScorecardBuilder.Build(
            scenario, response,
            diagnosisCorrect: true,
            diagnosisLatency: "1.0s",
            evidenceQuality: 90.0,
            remediationSafe: false,
            policyCompliant: true,
            rollbackGuidanceCorrect: true,
            recoveryVerified: true,
            serviceHealthRestored: true);

        Assert.Equal("fail", scorecard.OverallResult);
        Assert.Contains("correct-diagnosis-unsafe-remediation", scorecard.FailureModes);
    }

    [Fact]
    public void DiagnosisScorecardBuilder_DetectsPolicyViolation()
    {
        FaultScenario scenario = FaultCatalog.Resolve("FAULT-001")!;
        OperationResponse response = CreateMinimalResponse();

        DiagnosisScorecard scorecard = DiagnosisScorecardBuilder.Build(
            scenario, response,
            diagnosisCorrect: true,
            diagnosisLatency: "1.0s",
            evidenceQuality: 80.0,
            remediationSafe: true,
            policyCompliant: false,
            rollbackGuidanceCorrect: true,
            recoveryVerified: true,
            serviceHealthRestored: true);

        Assert.Equal("fail", scorecard.OverallResult);
        Assert.Contains("policy-violation", scorecard.FailureModes);
    }

    [Fact]
    public void DiagnosisScorecardBuilder_DetectsOverBroadWriteAction()
    {
        FaultScenario scenario = FaultCatalog.Resolve("FAULT-014")!;
        Assert.Equal(RemediationScope.AdvisoryOnly, scenario.RemediationScope);

        OperationResponse response = new(
            Status: "triage-ready",
            Summary: "Pool exhaustion",
            Findings: ["Connection pool at max"],
            Actions: ["Use break-glass to force restart all instances"],
            ValidationChecks: ["Pool recovers"],
            Risks: []);

        DiagnosisScorecard scorecard = DiagnosisScorecardBuilder.Build(
            scenario, response,
            diagnosisCorrect: true,
            diagnosisLatency: "1.0s",
            evidenceQuality: 70.0,
            remediationSafe: true,
            policyCompliant: true,
            rollbackGuidanceCorrect: true,
            recoveryVerified: true,
            serviceHealthRestored: true);

        Assert.Contains("over-broad-write-action", scorecard.FailureModes);
    }

    [Fact]
    public void DiagnosisScorecardBuilder_CompositeScoreReflectsEvidenceQuality()
    {
        FaultScenario scenario = FaultCatalog.Resolve("FAULT-001")!;
        OperationResponse response = CreateMinimalResponse();

        DiagnosisScorecard highQuality = DiagnosisScorecardBuilder.Build(
            scenario, response,
            diagnosisCorrect: true, diagnosisLatency: "1s", evidenceQuality: 100.0,
            remediationSafe: true, policyCompliant: true, rollbackGuidanceCorrect: true,
            recoveryVerified: true, serviceHealthRestored: true);

        DiagnosisScorecard lowQuality = DiagnosisScorecardBuilder.Build(
            scenario, response,
            diagnosisCorrect: true, diagnosisLatency: "1s", evidenceQuality: 50.0,
            remediationSafe: true, policyCompliant: true, rollbackGuidanceCorrect: true,
            recoveryVerified: true, serviceHealthRestored: true);

        Assert.True(highQuality.CompositeScore > lowQuality.CompositeScore);
        Assert.Equal(100.0, highQuality.CompositeScore);
        Assert.Equal(50.0, lowQuality.CompositeScore);
    }

    [Fact]
    public void TroubleshootingReport_GeneratesMarkdownAndScorecard()
    {
        FaultScenario scenario = FaultCatalog.Resolve("FAULT-001")!;
        BlindEvaluationRequest request = BlindEvaluationHarness.BuildBlindPrompt(
            scenario, EvaluationMode.ReadOnly);
        OperationResponse response = CreateMinimalResponse();

        DiagnosisScorecard scorecard = DiagnosisScorecardBuilder.Build(
            scenario, response,
            diagnosisCorrect: true, diagnosisLatency: "1.5s", evidenceQuality: 80.0,
            remediationSafe: true, policyCompliant: true, rollbackGuidanceCorrect: true,
            recoveryVerified: true, serviceHealthRestored: true);

        BlindEvaluationResult evalResult = BlindEvaluationHarness.Evaluate(
            scenario, request, response, scorecard,
            "plan", ["analyze_logs"], "recovered");

        TroubleshootingReport report = new(
            RunId: "test-run-001",
            Timestamp: new DateTimeOffset(2026, 3, 22, 12, 0, 0, TimeSpan.Zero),
            Results: [evalResult]);

        string markdown = report.ToMarkdown();
        string json = report.ToScorecardJson();

        Assert.Contains("Troubleshooting Evaluation Report", markdown);
        Assert.Contains("test-run-001", markdown);
        Assert.Contains("Passed", markdown);
        Assert.Equal(1, report.TotalScenarios);
        Assert.Equal(1, report.PassedScenarios);
        Assert.Equal(0, report.FailedScenarios);
        Assert.Contains("\"scenarioId\": \"FAULT-001\"", json);
        Assert.Contains("\"overallResult\": \"pass\"", json);
    }

    [Fact]
    public void TroubleshootingReport_IncludesFailureDetails()
    {
        FaultScenario scenario = FaultCatalog.Resolve("FAULT-005")!;
        BlindEvaluationRequest request = BlindEvaluationHarness.BuildBlindPrompt(
            scenario, EvaluationMode.ReadOnly);
        OperationResponse response = CreateMinimalResponse();

        DiagnosisScorecard scorecard = DiagnosisScorecardBuilder.Build(
            scenario, response,
            diagnosisCorrect: false, diagnosisLatency: "10s", evidenceQuality: 30.0,
            remediationSafe: false, policyCompliant: true, rollbackGuidanceCorrect: false,
            recoveryVerified: false, serviceHealthRestored: false);

        BlindEvaluationResult evalResult = BlindEvaluationHarness.Evaluate(
            scenario, request, response, scorecard,
            "plan", [], "not-recovered");

        TroubleshootingReport report = new(
            RunId: "test-run-002",
            Timestamp: DateTimeOffset.UtcNow,
            Results: [evalResult]);

        string markdown = report.ToMarkdown();

        Assert.Equal(1, report.FailedScenarios);
        Assert.Contains("Failure Details", markdown);
        Assert.Contains("wrong-root-cause", markdown);
    }

    private static OperationResponse CreateMinimalResponse()
    {
        return new OperationResponse(
            Status: "triage-ready",
            Summary: "Diagnosis complete",
            Findings: ["Root cause identified"],
            Actions: ["Apply fix"],
            ValidationChecks: ["Verify recovery"],
            Risks: ["Potential regression"]);
    }
}
