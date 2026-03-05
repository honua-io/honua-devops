using Honua.DevOps.Agent.Configuration;
using Honua.DevOps.Agent.Prompts;
using Honua.DevOps.Agent.Providers;
using Microsoft.Agents.AI;

using CancellationTokenSource cancellationTokenSource = new();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellationTokenSource.Cancel();
};

try
{
    CliOptions options = CliOptions.Parse(args);
    ChatClientAgent agent = AgentProviderFactory.Create(options.Provider, HonuaDevOpsPrompt.SystemPrompt);
    AgentSession session = await agent.CreateSessionAsync(cancellationTokenSource.Token);

    if (!string.IsNullOrWhiteSpace(options.Prompt))
    {
        await StreamResponseAsync(agent, session, options.Prompt, cancellationTokenSource.Token);
        return;
    }

    Console.WriteLine($"honua-devops is ready ({options.Provider.ToString().ToLowerInvariant()} provider).");
    Console.WriteLine("Type a request, or `exit` to quit.");

    while (!cancellationTokenSource.IsCancellationRequested)
    {
        Console.Write("> ");
        string? input = Console.ReadLine();
        if (input is null)
        {
            break;
        }

        input = input.Trim();
        if (string.IsNullOrWhiteSpace(input))
        {
            continue;
        }

        if (input.Equals("exit", StringComparison.OrdinalIgnoreCase) ||
            input.Equals("quit", StringComparison.OrdinalIgnoreCase))
        {
            break;
        }

        await StreamResponseAsync(agent, session, input, cancellationTokenSource.Token);
    }
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Operation canceled.");
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception.Message);
    Environment.ExitCode = 1;
}

static async Task StreamResponseAsync(
    AIAgent agent,
    AgentSession session,
    string prompt,
    CancellationToken cancellationToken)
{
    await foreach (AgentResponseUpdate update in agent.RunStreamingAsync(prompt, session, cancellationToken: cancellationToken))
    {
        if (!string.IsNullOrWhiteSpace(update.Text))
        {
            Console.Write(update.Text);
        }
    }

    Console.WriteLine();
}
