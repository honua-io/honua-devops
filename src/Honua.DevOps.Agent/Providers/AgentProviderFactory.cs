using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;
using System.ClientModel;

namespace Honua.DevOps.Agent.Providers;

internal static class AgentProviderFactory
{
    internal static ChatClientAgent Create(
        ProviderKind provider,
        string systemPrompt,
        IList<AITool>? tools = null)
    {
        return Create(provider, systemPrompt, tools, clientOptionsOverride: null);
    }

    internal static ChatClientAgent Create(
        ProviderKind provider,
        string systemPrompt,
        IList<AITool>? tools,
        OpenAIClientOptions? clientOptionsOverride)
    {
        ProviderConfiguration configuration = ProviderConfiguration.Load(provider);

        // Bedrock speaks the Converse API (not OpenAI-compatible chat completions), so it is
        // wrapped as an IChatClient adapter and flows through the IChatClient AsAIAgent
        // overload. The OpenAI-compatible providers keep their existing ChatClient path.
        if (provider == ProviderKind.Bedrock)
        {
            IChatClient bedrockClient = BedrockChatClientAdapter.FromConfiguration(configuration);
            return bedrockClient.AsAIAgent(
                instructions: systemPrompt,
                name: $"honua-devops-{configuration.Name}",
                description: "Honua operations and solution engineering agent",
                tools: tools);
        }

        ChatClient client = CreateChatClient(configuration, clientOptionsOverride);

        return client.AsAIAgent(
            instructions: systemPrompt,
            name: $"honua-devops-{configuration.Name}",
            description: "Honua operations and solution engineering agent",
            tools: tools);
    }

    private static ChatClient CreateChatClient(
        ProviderConfiguration configuration,
        OpenAIClientOptions? clientOptionsOverride)
    {
        ApiKeyCredential credential = new(configuration.ApiKey);

        if (clientOptionsOverride is not null)
        {
            if (clientOptionsOverride.Endpoint is null && configuration.Endpoint is not null)
            {
                clientOptionsOverride.Endpoint = configuration.Endpoint;
            }
            return new ChatClient(configuration.Model, credential, clientOptionsOverride);
        }

        if (configuration.Endpoint is null)
        {
            return new ChatClient(configuration.Model, credential);
        }

        OpenAIClientOptions options = new()
        {
            Endpoint = configuration.Endpoint
        };

        return new ChatClient(configuration.Model, credential, options);
    }
}
