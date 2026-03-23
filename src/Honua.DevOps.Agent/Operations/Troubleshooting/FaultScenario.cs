namespace Honua.DevOps.Agent.Operations.Troubleshooting;

internal sealed record FaultScenario(
    string Id,
    string Name,
    FaultCategory Category,
    string TargetCloud,
    string TargetRuntime,
    string InjectionMethod,
    IReadOnlyList<string> ExpectedSymptoms,
    IReadOnlyList<string> ExpectedLogEvidence,
    IReadOnlyList<string> ExpectedMetricEvidence,
    IReadOnlyList<string> ExpectedHealthEvidence,
    IReadOnlyList<string> SafeRemediationOptions,
    string RollbackPath,
    string CleanupPath,
    RemediationScope RemediationScope);

internal enum RemediationScope
{
    AdvisoryOnly,
    ReadOnlyDiagnosis,
    WriteCapable
}

internal static class RemediationScopeExtensions
{
    internal static string ToConfigValue(this RemediationScope scope)
    {
        return scope switch
        {
            RemediationScope.AdvisoryOnly => "advisory-only",
            RemediationScope.ReadOnlyDiagnosis => "read-only-diagnosis",
            RemediationScope.WriteCapable => "write-capable",
            _ => "unknown"
        };
    }
}
