namespace Honua.DevOps.Agent.Operations.Troubleshooting;

internal sealed record BlindEvaluationRequest(
    string IncidentSymptoms,
    string EnvironmentContext,
    string HealthStatus,
    string LogEvidence,
    string MetricEvidence,
    EvaluationMode EvaluationMode);

internal enum EvaluationMode
{
    ReadOnly,
    GuidedWrite,
    ExecuteLowerEnv
}

internal static class EvaluationModeExtensions
{
    internal static string ToConfigValue(this EvaluationMode mode)
    {
        return mode switch
        {
            EvaluationMode.ReadOnly => "read-only",
            EvaluationMode.GuidedWrite => "guided-write",
            EvaluationMode.ExecuteLowerEnv => "execute-lower-env",
            _ => "unknown"
        };
    }
}

internal sealed record BlindEvaluationResult(
    string ScenarioId,
    BlindEvaluationRequest Request,
    OperationResponse AgentResponse,
    DiagnosisScorecard Scorecard,
    EvaluationMode Mode,
    string PolicyModeUsed,
    IReadOnlyList<string> ActionsAttempted,
    string RecoveryResult);

internal static class BlindEvaluationHarness
{
    internal static BlindEvaluationRequest BuildBlindPrompt(FaultScenario scenario, EvaluationMode mode)
    {
        return new BlindEvaluationRequest(
            IncidentSymptoms: string.Join("; ", scenario.ExpectedSymptoms),
            EnvironmentContext: $"{scenario.TargetCloud}/{scenario.TargetRuntime}",
            HealthStatus: string.Join("; ", scenario.ExpectedHealthEvidence),
            LogEvidence: string.Join("; ", scenario.ExpectedLogEvidence),
            MetricEvidence: string.Join("; ", scenario.ExpectedMetricEvidence),
            EvaluationMode: mode);
    }

    internal static BlindEvaluationResult Evaluate(
        FaultScenario scenario,
        BlindEvaluationRequest request,
        OperationResponse agentResponse,
        DiagnosisScorecard scorecard,
        string policyModeUsed,
        IReadOnlyList<string> actionsAttempted,
        string recoveryResult)
    {
        return new BlindEvaluationResult(
            ScenarioId: scenario.Id,
            Request: request,
            AgentResponse: agentResponse,
            Scorecard: scorecard,
            Mode: request.EvaluationMode,
            PolicyModeUsed: policyModeUsed,
            ActionsAttempted: actionsAttempted,
            RecoveryResult: recoveryResult);
    }

    internal static bool ValidateBlindness(BlindEvaluationRequest request, FaultScenario scenario)
    {
        string combined = $"{request.IncidentSymptoms} {request.EnvironmentContext} {request.HealthStatus} " +
                          $"{request.LogEvidence} {request.MetricEvidence}";
        return !combined.Contains(scenario.Id, StringComparison.OrdinalIgnoreCase) &&
               !combined.Contains(scenario.InjectionMethod, StringComparison.OrdinalIgnoreCase);
    }
}
