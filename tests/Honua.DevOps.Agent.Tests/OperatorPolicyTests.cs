using Honua.DevOps.Agent.Operations.OperatorPolicy;
using OperatorPolicyModel = Honua.DevOps.Agent.Operations.OperatorPolicy.OperatorPolicy;

namespace Honua.DevOps.Agent.Tests;

public sealed class OperatorPolicyTests
{
    [Fact]
    public void Load_UsesDefaultsWhenUnset()
    {
        using TestEnvironmentVariableScope environment = new();
        ResetPolicyVariables(environment);

        OperatorPolicyModel policy = OperatorPolicyModel.Load();

        Assert.Equal(ApprovalMode.PrFirst, policy.ApprovalMode);
        Assert.Equal("stdout-evidence", policy.AuditHookTarget);
        Assert.Equal(SupportSessionAccess.Disabled, policy.SupportSession.Access);
        Assert.Equal(60, policy.SupportSession.TtlMinutes);
        Assert.True(policy.SupportSession.CustomerVisible);
        Assert.True(policy.BreakGlassPostActionReviewRequired);
    }

    [Fact]
    public void Load_ParsesConfiguredValues()
    {
        using TestEnvironmentVariableScope environment = new();
        ResetPolicyVariables(environment);
        environment.Set("HONUA_DEVOPS_APPROVAL_MODE", "direct-allowed");
        environment.Set("HONUA_DEVOPS_AUDIT_HOOK_TARGET", "audit://ops-log");
        environment.Set("HONUA_DEVOPS_SUPPORT_SESSION_ACCESS", "operator-scoped");
        environment.Set("HONUA_DEVOPS_SUPPORT_SESSION_TTL_MINUTES", "15");
        environment.Set("HONUA_DEVOPS_SUPPORT_SESSION_CUSTOMER_VISIBLE", "false");
        environment.Set("HONUA_DEVOPS_BREAK_GLASS_POST_REVIEW_REQUIRED", "false");

        OperatorPolicyModel policy = OperatorPolicyModel.Load();

        Assert.Equal(ApprovalMode.DirectAllowed, policy.ApprovalMode);
        Assert.Equal("audit://ops-log", policy.AuditHookTarget);
        Assert.Equal(SupportSessionAccess.OperatorScoped, policy.SupportSession.Access);
        Assert.Equal(15, policy.SupportSession.TtlMinutes);
        Assert.False(policy.SupportSession.CustomerVisible);
        Assert.False(policy.BreakGlassPostActionReviewRequired);
    }

    [Theory]
    [InlineData("HONUA_DEVOPS_APPROVAL_MODE", "ship-it", "APPROVAL_MODE")]
    [InlineData("HONUA_DEVOPS_SUPPORT_SESSION_ACCESS", "full-access", "SUPPORT_SESSION_ACCESS")]
    [InlineData("HONUA_DEVOPS_SUPPORT_SESSION_TTL_MINUTES", "0", "SUPPORT_SESSION_TTL_MINUTES")]
    [InlineData("HONUA_DEVOPS_SUPPORT_SESSION_CUSTOMER_VISIBLE", "maybe", "SUPPORT_SESSION_CUSTOMER_VISIBLE")]
    [InlineData("HONUA_DEVOPS_BREAK_GLASS_POST_REVIEW_REQUIRED", "later", "BREAK_GLASS_POST_REVIEW_REQUIRED")]
    public void Load_RejectsInvalidConfiguration(string variableName, string value, string expectedToken)
    {
        using TestEnvironmentVariableScope environment = new();
        ResetPolicyVariables(environment);
        environment.Set(variableName, value);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(OperatorPolicyModel.Load);

        Assert.Contains(expectedToken, exception.Message, StringComparison.Ordinal);
    }

    private static void ResetPolicyVariables(TestEnvironmentVariableScope environment)
    {
        string[] variableNames =
        [
            "HONUA_DEVOPS_APPROVAL_MODE",
            "HONUA_DEVOPS_AUDIT_HOOK_TARGET",
            "HONUA_DEVOPS_SUPPORT_SESSION_ACCESS",
            "HONUA_DEVOPS_SUPPORT_SESSION_TTL_MINUTES",
            "HONUA_DEVOPS_SUPPORT_SESSION_CUSTOMER_VISIBLE",
            "HONUA_DEVOPS_BREAK_GLASS_POST_REVIEW_REQUIRED"
        ];

        foreach (string variableName in variableNames)
        {
            environment.Set(variableName, null);
        }
    }
}
