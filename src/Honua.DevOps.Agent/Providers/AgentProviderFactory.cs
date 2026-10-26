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

    internal static ConfiguredProviderAgent Create(
        ProviderKind provider,
        string systemPrompt,
        ProviderDisclosureSnapshot approval,
        ProviderModelUseCase useCase,
        IList<AITool>? tools = null,
        OpenAIClientOptions? clientOptionsOverride = null,
        ProviderConstructionHooks? constructionHooks = null)
    {
        ArgumentNullException.ThrowIfNull(approval);
        ProviderRoute route = ProviderConfiguration.LoadRoute(provider);
        approval.RequireApproval(route, useCase);
        if (clientOptionsOverride?.Endpoint is { } endpoint && endpoint != route.Endpoint)
            throw new InvalidOperationException(ProviderDisclosureSnapshot.DenialMessage);
        // No provider key or AWS credential-chain client is read/constructed before approval.
        ProviderConfiguration configuration = constructionHooks is null ? ProviderConfiguration.LoadCredentials(route) : constructionHooks.LoadCredentials(route);
        if (configuration.Name != route.Name || configuration.Model != route.Model
            || configuration.Endpoint != route.Endpoint || configuration.Region != route.Region)
            throw new InvalidOperationException(ProviderDisclosureSnapshot.DenialMessage);

        // Bedrock speaks the Converse API (not OpenAI-compatible chat completions), so it is
        // wrapped as an IChatClient adapter and flows through the IChatClient AsAIAgent
        // overload. The OpenAI-compatible providers keep their existing ChatClient path.
        if (provider == ProviderKind.Bedrock)
        {
            IChatClient bedrockClient = constructionHooks is null
                ? BedrockChatClientAdapter.FromConfiguration(configuration) : constructionHooks.CreateBedrock(configuration);
            return new(bedrockClient.AsAIAgent(
                instructions: systemPrompt,
                name: $"honua-devops-{configuration.Name}",
                description: "Honua operations and solution engineering agent",
                tools: tools), route.Model);
        }

        ChatClient client = CreateChatClient(configuration, clientOptionsOverride);

        return new(client.AsAIAgent(
            instructions: systemPrompt,
            name: $"honua-devops-{configuration.Name}",
            description: "Honua operations and solution engineering agent",
            tools: tools), route.Model);
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

internal sealed record ConfiguredProviderAgent(ChatClientAgent Agent, string Model);

// Per-call synthetic dependencies keep denial-order tests offline, including AWS credential-chain construction.
internal sealed record ProviderConstructionHooks(Func<ProviderRoute, ProviderConfiguration> LoadCredentials,
    Func<ProviderConfiguration, IChatClient> CreateBedrock);
