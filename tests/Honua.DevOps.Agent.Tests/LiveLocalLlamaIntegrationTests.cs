using Honua.DevOps.Agent.Providers;
using Microsoft.Agents.AI;

namespace Honua.DevOps.Agent.Tests;

public class LiveLocalLlamaIntegrationTests
{
    private const string LiveGateVariable = "HONUA_DEVOPS_LIVE_LOCAL_LLAMA";

    [Fact]
    public async Task LiveLocalLlama_ReturnsAssistantTurnWhenEnabled()
    {
        if (!LiveLocalLlamaEnabled())
        {
            return;
        }

        ProviderConfiguration configuration = ProviderConfiguration.Load(ProviderKind.LocalLlama);
        Assert.Equal("local-llama", configuration.Name);

        ChatClientAgent agent = AgentProviderFactory.Create(
            ProviderKind.LocalLlama,
            systemPrompt: "you are a Honua operator live integration probe",
            tools: null);

        using CancellationTokenSource cancellation = new(ResolveBackendTimeout());

        AgentResponse response = await agent.RunAsync(
            "Return the literal string OK.",
            session: null,
            options: null,
            cancellationToken: cancellation.Token);

        Assert.NotNull(response);
        Assert.False(string.IsNullOrWhiteSpace(response.Text), "Live NIM endpoint returned an empty assistant turn.");
    }

    private static bool LiveLocalLlamaEnabled()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(LiveGateVariable),
                "true",
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return HasValue("HONUA_DEVOPS_LOCAL_LLAMA_MODEL")
            && HasValue("HONUA_DEVOPS_LOCAL_LLAMA_API_KEY")
            && HasValue("HONUA_DEVOPS_LOCAL_LLAMA_ENDPOINT");
    }

    private static bool HasValue(string variableName)
    {
        return !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(variableName));
    }

    private static TimeSpan ResolveBackendTimeout()
    {
        string? raw = Environment.GetEnvironmentVariable("HONUA_DEVOPS_BACKEND_TIMEOUT_SECONDS");
        if (!string.IsNullOrWhiteSpace(raw)
            && int.TryParse(raw, out int seconds)
            && seconds > 0)
        {
            return TimeSpan.FromSeconds(seconds);
        }

        return TimeSpan.FromSeconds(60);
    }
}
