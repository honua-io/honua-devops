using System.Net.Http;
using System.Text.Json;
using Honua.DevOps.Agent.Configuration;
using Honua.DevOps.Agent.Operations;
using Honua.DevOps.Agent.Operations.Audit;
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

IAuditSink? auditSink = null;

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

    if (options.ListOperations)
    {
        OperatorPolicyModel journalPolicy = OperatorPolicyModel.Load();
        Environment.ExitCode = OperationJournal.ListRecent(journalPolicy.AuditHookTarget, options.OperationLimit, Console.Out);
        return;
    }

    if (options.ShowOperation is not null)
    {
        OperatorPolicyModel journalPolicy = OperatorPolicyModel.Load();
        Environment.ExitCode = OperationJournal.ShowOperation(journalPolicy.AuditHookTarget, options.ShowOperation, Console.Out);
        return;
    }

    OperationRuntime runtime = OperationRuntime.Load();
    OperatorPolicyModel policy = OperatorPolicyModel.Load();
    BackendConfiguration backendConfiguration = BackendConfiguration.Load();
    using HttpClient sharedHttpClient = HttpClientFactory.Create(backendConfiguration.RequestTimeout);
    using BackendGateway backendGateway = new(backendConfiguration, sharedHttpClient);
    using SupportGateway supportGateway = new(backendConfiguration, sharedHttpClient);

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

    if (options.Listen)
    {
        WebhookListenerConfiguration listenerConfiguration = WebhookListenerConfiguration.Load();
        HonuaOperationsToolkit listenerToolkit = new(runtime, backendGateway, policy, supportGateway);
        EscalationConsoleReporter reporter = new(listenerConfiguration.AutoTriage ? listenerToolkit : null);
        EscalationWebhookHandler webhookHandler = new(
            listenerConfiguration.Secret,
            (payload, token) => reporter.ReportAsync(payload, token));
        await using EscalationWebhookListener listener = new(listenerConfiguration, webhookHandler);
        await listener.RunAsync(cancellationTokenSource.Token);
        return;
    }

    auditSink = AuditSinkFactory.Create(policy.AuditHookTarget);

    string detectedEdition = await DetectEditionAsync(backendGateway, cancellationTokenSource.Token);

    IList<AITool> tools = CapabilityToolset.Create(runtime, backendGateway, policy, supportGateway, detectedEdition);
    ChatClientAgent agent = AgentProviderFactory.Create(options.Provider, HonuaDevOpsPrompt.SystemPrompt, tools);
    AgentSession session = await agent.CreateSessionAsync(cancellationTokenSource.Token);

    string sessionId = Guid.NewGuid().ToString("n");
    AuditContext auditContext = new(
        sessionId,
        runtime.ExecutionMode.ToString().ToLowerInvariant(),
        runtime.ExecutionTier.ToConfigValue(),
        policy.ApprovalMode.ToConfigValue(),
        options.Provider.ToConfigValue(),
        auditSink);

    if (!string.IsNullOrWhiteSpace(options.Prompt))
    {
        await StreamResponseAsync(agent, session, options.Prompt, auditContext, cancellationTokenSource.Token);
        return;
    }

    Console.WriteLine(
        $"honua-devops is ready ({options.Provider.ToConfigValue()} provider, mode={runtime.ExecutionMode.ToString().ToLowerInvariant()}, tier={runtime.ExecutionTier.ToConfigValue()}, gitops={runtime.GitOpsTool}, edition={detectedEdition}).");
    Console.WriteLine($"approval={policy.ApprovalMode.ToConfigValue()} audit={auditSink.Target} support-session={policy.SupportSession.Access.ToConfigValue()} ttl={policy.SupportSession.TtlMinutes}m session={sessionId}");
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

        await StreamResponseAsync(agent, session, input, auditContext, cancellationTokenSource.Token);
    }
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Operation canceled.");
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception);
    Environment.ExitCode = 1;
}
finally
{
    if (auditSink is not null)
    {
        await auditSink.DisposeAsync();
    }
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
    catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
    {
        Console.Error.WriteLine($"warn: capability probe failed ({exception.GetType().Name}): {exception.Message}");
    }

    return "community";
}

static async Task StreamResponseAsync(
    AIAgent agent,
    AgentSession session,
    string prompt,
    AuditContext auditContext,
    CancellationToken cancellationToken)
{
    HashSet<string> announcedCalls = new(StringComparer.Ordinal);
    HashSet<string> announcedResults = new(StringComparer.Ordinal);
    Dictionary<string, ToolCallRecord> pendingCalls = new(StringComparer.Ordinal);

    await foreach (AgentResponseUpdate update in agent.RunStreamingAsync(prompt, session, cancellationToken: cancellationToken))
    {
        if (update.Contents is not null)
        {
            foreach (AIContent content in update.Contents)
            {
                if (content is FunctionCallContent call)
                {
                    string callId = call.CallId ?? Guid.NewGuid().ToString();
                    pendingCalls[callId] = new ToolCallRecord(call.Name ?? "(unknown)", call.Arguments);

                    if (announcedCalls.Add(callId))
                    {
                        Console.WriteLine();
                        Console.WriteLine($"[tool] {call.Name}({SummarizeArguments(call.Arguments)})");
                    }
                    continue;
                }

                if (content is FunctionResultContent result)
                {
                    string callId = result.CallId ?? Guid.NewGuid().ToString();
                    if (announcedResults.Add(callId))
                    {
                        string status = ExtractStatus(result.Result);
                        Console.WriteLine($"[tool] → {status}");
                    }

                    if (pendingCalls.Remove(callId, out ToolCallRecord? record))
                    {
                        await EmitAuditAsync(auditContext, record, result.Result, cancellationToken);
                    }
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

static async Task EmitAuditAsync(
    AuditContext context,
    ToolCallRecord call,
    object? toolResult,
    CancellationToken cancellationToken)
{
    if (context.Sink is NullAuditSink)
    {
        return;
    }

    string operationId = Guid.NewGuid().ToString("n");
    Dictionary<string, string> arguments = new(StringComparer.Ordinal);
    if (call.Arguments is not null)
    {
        foreach (KeyValuePair<string, object?> kvp in call.Arguments)
        {
            string raw = kvp.Value?.ToString() ?? "null";
            arguments[kvp.Key] = Redaction.Scrub(raw);
        }
    }

    string status = "unknown";
    string summary = string.Empty;
    bool mutated = false;
    IReadOnlyList<OperationBackendStep>? backendSteps = null;
    OperationEvidence? evidence = null;

    if (toolResult is OperationResponse response)
    {
        status = response.Status;
        summary = Redaction.Scrub(response.Summary);
        evidence = response.Evidence;
        if (response.BackendSteps is { } steps)
        {
            List<OperationBackendStep> scrubbedSteps = new(steps.Count);
            foreach (OperationBackendStep step in steps)
            {
                scrubbedSteps.Add(step with
                {
                    Detail = Redaction.Scrub(step.Detail),
                    PayloadPreview = Redaction.Scrub(step.PayloadPreview)
                });
                if (step.MutatesState)
                {
                    mutated = true;
                }
            }
            backendSteps = scrubbedSteps;
        }
    }
    else if (toolResult is not null)
    {
        try
        {
            string json = toolResult is string s ? s : JsonSerializer.Serialize(toolResult);
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("Status", out JsonElement statusElement) && statusElement.ValueKind == JsonValueKind.String)
                {
                    status = statusElement.GetString() ?? status;
                }

                if (root.TryGetProperty("Summary", out JsonElement summaryElement) && summaryElement.ValueKind == JsonValueKind.String)
                {
                    summary = Redaction.Scrub(summaryElement.GetString() ?? string.Empty);
                }
            }
        }
        catch (JsonException)
        {
            // leave defaults
        }
    }

    AuditRecord record = new(
        Timestamp: DateTimeOffset.UtcNow,
        SessionId: context.SessionId,
        OperationId: operationId,
        ToolName: call.ToolName,
        Arguments: arguments,
        Status: status,
        Summary: summary,
        Mutated: mutated,
        ExecutionMode: context.ExecutionMode,
        ExecutionTier: context.ExecutionTier,
        ApprovalMode: context.ApprovalMode,
        Provider: context.Provider,
        BackendSteps: backendSteps,
        Evidence: evidence);

    try
    {
        await context.Sink.WriteAsync(record, cancellationToken);
    }
    catch (OperationCanceledException)
    {
        throw;
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
    {
        Console.Error.WriteLine($"warn: audit sink write failed: {exception.Message}");
    }
}

internal sealed record AuditContext(
    string SessionId,
    string ExecutionMode,
    string ExecutionTier,
    string ApprovalMode,
    string Provider,
    IAuditSink Sink);

internal sealed record ToolCallRecord(string ToolName, IDictionary<string, object?>? Arguments);
