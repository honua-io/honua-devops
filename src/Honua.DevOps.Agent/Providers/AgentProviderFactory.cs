using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;
using System.ClientModel;
using System.ClientModel.Primitives;

namespace Honua.DevOps.Agent.Providers;

internal static class AgentProviderFactory
{
    // Reuse the production connection pool; SDK clients do not own this transport.
    private static readonly HttpClient ProviderHttpClient = new(new HttpClientHandler
    {
        AllowAutoRedirect = false
    });

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

        OpenAIClientOptions options = CreateClientOptions(configuration, clientOptionsOverride);
        return new ChatClient(configuration.Model, credential, options);
    }

    internal static OpenAIClientOptions CreateClientOptions(
        ProviderConfiguration configuration,
        OpenAIClientOptions? clientOptionsOverride = null)
    {
        // The override is an internal synthetic-test seam. Production always supplies an
        // explicit nonredirecting transport, including when the SDK selects its default URL.
        OpenAIClientOptions options = clientOptionsOverride ?? new OpenAIClientOptions
        {
            Transport = new HttpClientPipelineTransport(ProviderHttpClient)
        };
        options.Endpoint ??= configuration.Endpoint;
        options.RetryPolicy = new ClientRetryPolicy(maxRetries: 0);
        return options;
    }
}
