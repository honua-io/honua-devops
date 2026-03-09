namespace Honua.DevOps.Agent.Operations.OperatorPolicy;

internal sealed record OperatorPolicy(
    ApprovalMode ApprovalMode,
    string AuditHookTarget,
    SupportSessionPolicy SupportSession,
    bool BreakGlassPostActionReviewRequired)
{
    private const string ApprovalModeVariable = "HONUA_DEVOPS_APPROVAL_MODE";
    private const string AuditHookTargetVariable = "HONUA_DEVOPS_AUDIT_HOOK_TARGET";
    private const string SupportSessionAccessVariable = "HONUA_DEVOPS_SUPPORT_SESSION_ACCESS";
    private const string SupportSessionTtlMinutesVariable = "HONUA_DEVOPS_SUPPORT_SESSION_TTL_MINUTES";
    private const string SupportSessionCustomerVisibleVariable = "HONUA_DEVOPS_SUPPORT_SESSION_CUSTOMER_VISIBLE";
    private const string BreakGlassPostReviewVariable = "HONUA_DEVOPS_BREAK_GLASS_POST_REVIEW_REQUIRED";

    internal static OperatorPolicy Default =>
        new(
            ApprovalMode.PrFirst,
            "stdout-evidence",
            new SupportSessionPolicy(SupportSessionAccess.Disabled, 60, true),
            true);

    internal static OperatorPolicy Load()
    {
        ApprovalMode approvalMode = ParseApprovalMode(Environment.GetEnvironmentVariable(ApprovalModeVariable));

        string auditHookTarget = Normalize(Environment.GetEnvironmentVariable(AuditHookTargetVariable), "stdout-evidence");
        if (string.IsNullOrWhiteSpace(auditHookTarget))
        {
            throw new InvalidOperationException($"Environment variable `{AuditHookTargetVariable}` must not be empty.");
        }

        SupportSessionAccess supportSessionAccess = ParseSupportSessionAccess(
            Environment.GetEnvironmentVariable(SupportSessionAccessVariable));
        int supportSessionTtlMinutes = ParseTtlMinutes(
            Environment.GetEnvironmentVariable(SupportSessionTtlMinutesVariable));
        bool supportSessionCustomerVisible = ParseBoolean(
            Environment.GetEnvironmentVariable(SupportSessionCustomerVisibleVariable),
            true,
            SupportSessionCustomerVisibleVariable);
        bool breakGlassPostReviewRequired = ParseBoolean(
            Environment.GetEnvironmentVariable(BreakGlassPostReviewVariable),
            true,
            BreakGlassPostReviewVariable);

        return new OperatorPolicy(
            approvalMode,
            auditHookTarget,
            new SupportSessionPolicy(
                supportSessionAccess,
                supportSessionTtlMinutes,
                supportSessionCustomerVisible),
            breakGlassPostReviewRequired);
    }

    private static ApprovalMode ParseApprovalMode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return ApprovalMode.PrFirst;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "pr-first" or "pr_first" => ApprovalMode.PrFirst,
            "direct-allowed" or "direct_allowed" => ApprovalMode.DirectAllowed,
            "break-glass-only" or "break_glass_only" => ApprovalMode.BreakGlassOnly,
            _ => throw new InvalidOperationException(
                $"Invalid `{ApprovalModeVariable}` value `{value}`. Allowed values: pr-first, direct-allowed, break-glass-only.")
        };
    }

    private static SupportSessionAccess ParseSupportSessionAccess(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return SupportSessionAccess.Disabled;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "disabled" => SupportSessionAccess.Disabled,
            "read-only" or "read_only" => SupportSessionAccess.ReadOnly,
            "operator-scoped" or "operator_scoped" => SupportSessionAccess.OperatorScoped,
            _ => throw new InvalidOperationException(
                $"Invalid `{SupportSessionAccessVariable}` value `{value}`. Allowed values: disabled, read-only, operator-scoped.")
        };
    }

    private static int ParseTtlMinutes(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return 60;
        }

        if (!int.TryParse(value.Trim(), out int parsed) || parsed is < 1 or > 1440)
        {
            throw new InvalidOperationException(
                $"Environment variable `{SupportSessionTtlMinutesVariable}` must be an integer between 1 and 1440.");
        }

        return parsed;
    }

    private static bool ParseBoolean(string? value, bool fallback, string variableName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        if (bool.TryParse(value.Trim(), out bool parsed))
        {
            return parsed;
        }

        throw new InvalidOperationException(
            $"Environment variable `{variableName}` must be `true` or `false`.");
    }

    private static string Normalize(string? value, string fallback)
    {
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }
}
