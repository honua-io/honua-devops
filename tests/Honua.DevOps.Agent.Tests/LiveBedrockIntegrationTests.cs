using Honua.DevOps.Agent.Providers;
using Microsoft.Agents.AI;

namespace Honua.DevOps.Agent.Tests;

/// <summary>
/// Opt-in live integration against a real Amazon Bedrock endpoint. Disabled by default; it
/// runs only when <c>HONUA_DEVOPS_LIVE_BEDROCK</c> is set to <c>1</c>/<c>true</c> and the
/// Bedrock model env block is present. Authentication uses the standard AWS credential chain
/// (IAM) unless <c>HONUA_DEVOPS_BEDROCK_API_KEY</c> supplies a Bedrock API key.
/// </summary>
public class LiveBedrockIntegrationTests
{
    private const string LiveGateVariable = "HONUA_DEVOPS_LIVE_BEDROCK";

    [Fact]
    public async Task LiveBedrock_ReturnsAssistantTurnWhenEnabled()
    {
        if (!LiveBedrockEnabled())
        {
            return;
        }

        ProviderConfiguration configuration = ProviderConfiguration.Load(ProviderKind.Bedrock);
        Assert.Equal("bedrock", configuration.Name);

        ChatClientAgent agent = AgentProviderFactory.Create(
            ProviderKind.Bedrock,
            systemPrompt: "you are a Honua operator live integration probe",
            tools: null);

        using CancellationTokenSource cancellation = new(ResolveBackendTimeout());

        AgentResponse response = await agent.RunAsync(
            "Return the literal string OK.",
            session: null,
            options: null,
            cancellationToken: cancellation.Token);

        Assert.NotNull(response);
        Assert.False(string.IsNullOrWhiteSpace(response.Text), "Live Bedrock endpoint returned an empty assistant turn.");
    }

    private static bool LiveBedrockEnabled()
    {
        string? gate = Environment.GetEnvironmentVariable(LiveGateVariable);
        bool enabled = string.Equals(gate, "1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(gate, "true", StringComparison.OrdinalIgnoreCase);

        // The model id is required; region defaults to us-west-2 and the API key is optional
        // (the AWS credential chain is used when it is absent).
        return enabled && HasValue("HONUA_DEVOPS_BEDROCK_MODEL");
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
