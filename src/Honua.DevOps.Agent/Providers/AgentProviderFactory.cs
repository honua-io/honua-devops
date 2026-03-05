using Microsoft.Agents.AI;
using OpenAI;
using OpenAI.Chat;
using System.ClientModel;

namespace Honua.DevOps.Agent.Providers;

internal static class AgentProviderFactory
{
    internal static ChatClientAgent Create(ProviderKind provider, string systemPrompt)
    {
        ProviderConfiguration configuration = ProviderConfiguration.Load(provider);
        ChatClient client = CreateChatClient(configuration);

        return client.AsAIAgent(
            instructions: systemPrompt,
            name: $"honua-devops-{configuration.Name}",
            description: "Honua operations and solution engineering agent");
    }

    private static ChatClient CreateChatClient(ProviderConfiguration configuration)
    {
        ApiKeyCredential credential = new(configuration.ApiKey);
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
