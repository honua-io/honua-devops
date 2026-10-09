using Honua.DevOps.Agent.Prompts;

namespace Honua.DevOps.Agent.Tests;

public sealed class HonuaDevOpsPromptTests
{
    [Fact]
    public void SystemPrompt_PointsDayTwoAtTheServerMcpAndSeparatePrincipalApproval()
    {
        string prompt = HonuaDevOpsPrompt.SystemPrompt;

        Assert.Contains("provisioning executor", prompt, StringComparison.Ordinal);
        Assert.Contains("`honua_ops_findings`", prompt, StringComparison.Ordinal);
        Assert.Contains("`honua_propose_*`", prompt, StringComparison.Ordinal);
        Assert.Contains("POST /api/v1/admin/proposals/{id}/approve", prompt, StringComparison.Ordinal);
        Assert.Contains("separate principal", prompt, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("honua_observe_diagnose_propose")]
    [InlineData("honua_auto_remediation_plan")]
    [InlineData("honua_runbook_execute")]
    [InlineData("plan_server_upgrade")]
    [InlineData("plan_forward_fix")]
    [InlineData("honua_diagnose")]
    public void SystemPrompt_DoesNotAdvertiseRetiredOpsLoopTools(string retiredTool)
    {
        Assert.DoesNotContain($"`{retiredTool}`", HonuaDevOpsPrompt.SystemPrompt, StringComparison.Ordinal);
    }
}
