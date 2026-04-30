namespace Honua.DevOps.Agent.Operations.GuidedFix;

internal enum SupportSeverity
{
    Critical,
    High,
    Medium,
    Low
}

internal static class SupportSeverityExtensions
{
    internal static string ToConfigValue(this SupportSeverity severity)
    {
        return severity switch
        {
            SupportSeverity.Critical => "critical",
            SupportSeverity.High => "high",
            SupportSeverity.Medium => "medium",
            SupportSeverity.Low => "low",
            _ => "unknown"
        };
    }

    internal static SupportSeverity Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return SupportSeverity.Medium;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "critical" or "p1" or "sev1" => SupportSeverity.Critical,
            "high" or "p2" or "sev2" => SupportSeverity.High,
            "medium" or "p3" or "sev3" => SupportSeverity.Medium,
            "low" or "p4" or "sev4" => SupportSeverity.Low,
            _ => throw new InvalidOperationException(
                $"Invalid severity `{value}`. Allowed values: critical, high, medium, low.")
        };
    }
}
