using Honua.DevOps.Agent.Configuration;
using Honua.DevOps.Agent.Operations;
using Honua.DevOps.Agent.Operations.OperatorPolicy;
using Honua.DevOps.Agent.Prompts;
using Honua.DevOps.Agent.Providers;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OperatorPolicyModel = Honua.DevOps.Agent.Operations.OperatorPolicy.OperatorPolicy;

using CancellationTokenSource cancellationTokenSource = new();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellationTokenSource.Cancel();
};

try
{
    _ = DotEnvLoader.LoadDefaultFiles();
    CliOptions options = CliOptions.Parse(args);
    OperationRuntime runtime = OperationRuntime.Load();
    OperatorPolicyModel policy = OperatorPolicyModel.Load();
    BackendConfiguration backendConfiguration = BackendConfiguration.Load();
    using BackendGateway backendGateway = new(backendConfiguration);
    using SupportGateway supportGateway = new(backendConfiguration);

    if (options.Preflight)
    {
        Environment.ExitCode = await PreflightRunner.RunAsync(
            runtime,
            policy,
            backendConfiguration,
            backendGateway,
            cancellationTokenSource.Token);
        return;
    }

    IList<AITool> tools = CapabilityToolset.Create(runtime, backendGateway, policy, supportGateway);
    ChatClientAgent agent = AgentProviderFactory.Create(options.Provider, HonuaDevOpsPrompt.SystemPrompt, tools);
    AgentSession session = await agent.CreateSessionAsync(cancellationTokenSource.Token);

    if (!string.IsNullOrWhiteSpace(options.Prompt))
    {
        await StreamResponseAsync(agent, session, options.Prompt, cancellationTokenSource.Token);
        return;
    }

    Console.WriteLine(
        $"honua-devops is ready ({options.Provider.ToString().ToLowerInvariant()} provider, mode={runtime.ExecutionMode.ToString().ToLowerInvariant()}, tier={runtime.ExecutionTier.ToConfigValue()}, gitops={runtime.GitOpsTool}).");
    Console.WriteLine($"approval={policy.ApprovalMode.ToConfigValue()} audit={policy.AuditHookTarget} support-session={policy.SupportSession.Access.ToConfigValue()} ttl={policy.SupportSession.TtlMinutes}m");
    Console.WriteLine($"honua-api={backendConfiguration.HonuaApiBaseUri} otel={backendConfiguration.OTelBaseUri}");
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
