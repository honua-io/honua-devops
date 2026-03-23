namespace Honua.DevOps.Agent.Operations.GuidedFix;

internal enum GuidedFixMode
{
    ReadOnlyTriage,
    GuidedFix,
    OperatorScoped
}

internal static class GuidedFixModeExtensions
{
    internal static string ToConfigValue(this GuidedFixMode mode)
    {
        return mode switch
        {
            GuidedFixMode.ReadOnlyTriage => "read-only-triage",
            GuidedFixMode.GuidedFix => "guided-fix",
            GuidedFixMode.OperatorScoped => "operator-scoped",
            _ => "unknown"
        };
    }
}
