using Honua.DevOps.Agent.Prompts;

namespace Honua.DevOps.Agent.Tests;

public sealed class HonuaDevOpsPromptTests
{
    [Fact]
    public void SystemPrompt_MakesServerMcpLoopPrimaryAndProposalExplicit()
    {
        string prompt = HonuaDevOpsPrompt.SystemPrompt;

        Assert.Contains(
            "call `honua_observe_diagnose_propose` first with `proposeRecommendedAction=false`",
            prompt,
            StringComparison.Ordinal);
        Assert.Contains("primary operational truth", prompt, StringComparison.Ordinal);
        Assert.Contains(
            "Set it true only when the operator asked for a proposal",
            prompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "server gateway and Console approval/autonomy policy remain authoritative",
            prompt,
            StringComparison.Ordinal);
    }
}
