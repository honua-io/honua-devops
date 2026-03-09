namespace Honua.DevOps.Agent.Operations.OperatorPolicy;

internal static class SupportSessionAccessExtensions
{
    internal static string ToConfigValue(this SupportSessionAccess access)
    {
        return access switch
        {
            SupportSessionAccess.Disabled => "disabled",
            SupportSessionAccess.ReadOnly => "read-only",
            SupportSessionAccess.OperatorScoped => "operator-scoped",
            _ => throw new ArgumentOutOfRangeException(nameof(access), access, "Unsupported support session access.")
        };
    }
}
