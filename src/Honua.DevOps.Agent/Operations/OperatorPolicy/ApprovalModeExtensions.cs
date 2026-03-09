namespace Honua.DevOps.Agent.Operations.OperatorPolicy;

internal static class ApprovalModeExtensions
{
    internal static string ToConfigValue(this ApprovalMode mode)
    {
        return mode switch
        {
            ApprovalMode.PrFirst => "pr-first",
            ApprovalMode.DirectAllowed => "direct-allowed",
            ApprovalMode.BreakGlassOnly => "break-glass-only",
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unsupported approval mode.")
        };
    }
}
