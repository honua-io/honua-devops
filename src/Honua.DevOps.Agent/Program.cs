using System.Text.Json;
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

    if (options.Help)
    {
        Console.WriteLine(CliOptions.HelpText);
        return;
    }

    if (options.ListTools)
    {
        OperationRuntime listRuntime = OperationRuntime.Load();
        OperatorPolicyModel listPolicy = OperatorPolicyModel.Load();
        BackendConfiguration listBackendConfig = BackendConfiguration.Load();
        using BackendGateway listGateway = new(listBackendConfig);
        using SupportGateway listSupport = new(listBackendConfig);
        IList<AITool> toolList = CapabilityToolset.Create(listRuntime, listGateway, listPolicy, listSupport);
        Console.WriteLine($"honua-devops exposes {toolList.Count} operator tools:");
        foreach (AITool tool in toolList)
        {
            Console.WriteLine($"  - {tool.Name}: {tool.Description}");
        }
        return;
    }

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

    string detectedEdition = await DetectEditionAsync(backendGateway, cancellationTokenSource.Token);

    IList<AITool> tools = CapabilityToolset.Create(runtime, backendGateway, policy, supportGateway, detectedEdition);
    ChatClientAgent agent = AgentProviderFactory.Create(options.Provider, HonuaDevOpsPrompt.SystemPrompt, tools);
    AgentSession session = await agent.CreateSessionAsync(cancellationTokenSource.Token);

    if (!string.IsNullOrWhiteSpace(options.Prompt))
    {
        await StreamResponseAsync(agent, session, options.Prompt, cancellationTokenSource.Token);
        return;
    }

    Console.WriteLine(
        $"honua-devops is ready ({options.Provider.ToString().ToLowerInvariant()} provider, mode={runtime.ExecutionMode.ToString().ToLowerInvariant()}, tier={runtime.ExecutionTier.ToConfigValue()}, gitops={runtime.GitOpsTool}, edition={detectedEdition}).");
    Console.WriteLine($"approval={policy.ApprovalMode.ToConfigValue()} audit={policy.AuditHookTarget} support-session={policy.SupportSession.Access.ToConfigValue()} ttl={policy.SupportSession.TtlMinutes}m");
    Console.WriteLine($"honua-api={backendConfiguration.HonuaApiBaseUri} otel={backendConfiguration.OTelBaseUri}");
    Console.WriteLine("Type a request, `exit` to quit, or `/tools` to list available operator tools.");

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

        if (input.Equals("/tools", StringComparison.OrdinalIgnoreCase))
        {
            foreach (AITool tool in tools)
            {
                Console.WriteLine($"  - {tool.Name}: {tool.Description}");
            }
            continue;
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

static async Task<string> DetectEditionAsync(BackendGateway gateway, CancellationToken cancellationToken)
{
    try
    {
        using BackendJsonResult capabilities = await gateway.GetCapabilitySnapshotAsync(cancellationToken);
        if (capabilities.CallResult.IsSuccess && capabilities.Payload is not null)
        {
            string? detected = BackendGateway.ExtractEditionFromCapabilities(capabilities.Payload);
            if (!string.IsNullOrWhiteSpace(detected))
            {
                return detected!.Trim().ToLowerInvariant();
            }
        }
    }
    catch (OperationCanceledException)
    {
        throw;
    }
    catch
    {
        // best-effort; fall through to default
    }

    return "community";
}

static async Task StreamResponseAsync(
    AIAgent agent,
    AgentSession session,
    string prompt,
    CancellationToken cancellationToken)
{
    HashSet<string> announcedCalls = new(StringComparer.Ordinal);
    HashSet<string> announcedResults = new(StringComparer.Ordinal);

    await foreach (AgentResponseUpdate update in agent.RunStreamingAsync(prompt, session, cancellationToken: cancellationToken))
    {
        if (update.Contents is not null)
        {
            foreach (AIContent content in update.Contents)
            {
                if (content is FunctionCallContent call && announcedCalls.Add(call.CallId ?? Guid.NewGuid().ToString()))
                {
                    Console.WriteLine();
                    Console.WriteLine($"[tool] {call.Name}({SummarizeArguments(call.Arguments)})");
                    continue;
                }

                if (content is FunctionResultContent result && announcedResults.Add(result.CallId ?? Guid.NewGuid().ToString()))
                {
                    string status = ExtractStatus(result.Result);
                    Console.WriteLine($"[tool] → {status}");
                }
            }
        }

        if (!string.IsNullOrEmpty(update.Text))
        {
            Console.Write(update.Text);
        }
    }

    Console.WriteLine();
}

static string SummarizeArguments(IDictionary<string, object?>? arguments)
{
    if (arguments is null || arguments.Count == 0)
    {
        return string.Empty;
    }

    return string.Join(", ", arguments.Take(4).Select(kvp =>
    {
        string value = kvp.Value?.ToString() ?? "null";
        if (value.Length > 32)
        {
            value = value[..32] + "...";
        }
        return $"{kvp.Key}={value}";
    }));
}

static string ExtractStatus(object? toolResult)
{
    if (toolResult is null)
    {
        return "(no result)";
    }

    try
    {
        string json = toolResult is string s ? s : JsonSerializer.Serialize(toolResult);
        using JsonDocument document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind == JsonValueKind.Object
            && document.RootElement.TryGetProperty("Status", out JsonElement statusElement)
            && statusElement.ValueKind == JsonValueKind.String)
        {
            string? status = statusElement.GetString();
            if (!string.IsNullOrWhiteSpace(status))
            {
                if (document.RootElement.TryGetProperty("Summary", out JsonElement summaryElement)
                    && summaryElement.ValueKind == JsonValueKind.String)
                {
                    string? summary = summaryElement.GetString();
                    return string.IsNullOrWhiteSpace(summary) ? status! : $"{status} — {summary}";
                }
                return status!;
            }
        }
    }
    catch (JsonException)
    {
        // fall through
    }

    string raw = toolResult.ToString() ?? string.Empty;
    return raw.Length > 120 ? raw[..120] + "..." : raw;
}
