using System.Net.Http;
using System.Text.Json;
using Honua.DevOps.Agent.Configuration;
using Honua.DevOps.Agent.Mcp;
using Honua.DevOps.Agent.Operations;
using Honua.DevOps.Agent.Operations.Audit;
using Honua.DevOps.Agent.Operations.BugReport;
using Honua.DevOps.Agent.Operations.ConsoleBridge;
using Honua.DevOps.Agent.Operations.Eval;
using Honua.DevOps.Agent.Operations.OperatorPolicy;
using Honua.DevOps.Agent.Operations.WorkIntake;
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

    // The blind evaluation lane runs before the backend gateways are constructed: it
    // measures model diagnosis from the fault catalog and needs no Honua backend, no
    // audit sink, and no operator toolset.
    if (options.EvalBlind is not null)
    {
        Environment.ExitCode = await BlindEvalMode.RunAsync(
            options.Provider,
            options.EvalBlind,
            Console.Out,
            cancellationTokenSource.Token);
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

    if (options.AwaitApproval is not null)
    {
        ApprovalWaiter waiter = new(backendGateway, policy, runtime);
        Console.WriteLine(
            $"Waiting for deploy-control operation `{options.AwaitApproval}` to leave AwaitingApproval "
            + $"(approval={policy.ApprovalMode.ToConfigValue()}, timeout={waiter.Timeout.TotalSeconds:0}s).");

        ApprovalWaitResult waitResult = await waiter.WaitAsync(options.AwaitApproval, cancellationTokenSource.Token);

        Console.WriteLine(waitResult.Summary);
        if (waitResult.FinalStatus is not null)
        {
            Console.WriteLine($"Final status: {waitResult.FinalStatus} (polls={waitResult.Polls}).");
        }

        // Resolved == the operation left AwaitingApproval (approved or, under direct-allowed,
        // submitted by the waiter). Timeout/not-found/error are non-zero so callers/CI can gate on it.
        Environment.ExitCode = waitResult.Outcome == ApprovalWaitOutcome.Resolved ? 0 : 1;
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

    if (options.IntakeListen)
    {
        WorkIntakeConfiguration intakeConfiguration = WorkIntakeConfiguration.Load();
        if (!intakeConfiguration.IsEnabled)
        {
            Console.Error.WriteLine(
                "intake-disabled: set HONUA_DEVOPS_INTAKE_PROVIDER=jira (and HONUA_DEVOPS_INTAKE_WEBHOOK_SECRET) to enable the work-intake listener.");
            Environment.ExitCode = 1;
            return;
        }

        // Founder decision: the intake capability is Enterprise. Detect the edition
        // from the connected server and refuse to bind below Enterprise.
        string intakeEdition = await EditionDetector.DetectAsync(backendGateway, cancellationTokenSource.Token);
        if (!WorkIntakeEditionGate.IsAllowed(intakeEdition))
        {
            OperationResponse refusal = WorkIntakeEditionGate.BuildRefusal(intakeEdition);
            Console.Error.WriteLine($"{refusal.Status}: {refusal.Summary}");
            Environment.ExitCode = 1;
            return;
        }

        IIntakeSignatureVerifier intakeVerifier = new JiraCloudSignatureVerifier(intakeConfiguration.WebhookSecret);
        using JiraConnector jiraConnector = new(intakeConfiguration, sharedHttpClient);
        WorkIntakeReporter intakeReporter = new(jiraConnector);
        WorkIntakeWebhookHandler intakeHandler = new(
            intakeVerifier,
            provider: WorkItem.JiraProvider,
            projectFilter: intakeConfiguration.ProjectFilter,
            onAccepted: (workItem, token) => intakeReporter.ReportAsync(workItem, token));
        await using WorkIntakeWebhookListener intakeListener = new(intakeConfiguration, intakeHandler);
        await intakeListener.RunAsync(cancellationTokenSource.Token);
        return;
    }

    if (options.BugReportListen)
    {
        BugReportConfiguration bugReportConfiguration = BugReportConfiguration.Load();
        if (!bugReportConfiguration.IsEnabled)
        {
            Console.Error.WriteLine(
                "bugreport-disabled: set HONUA_DEVOPS_BUGREPORT_WEBHOOK_SECRET to enable the ticket.bug_report.v1 issue adapter.");
            Environment.ExitCode = 1;
            return;
        }

        // honua-devops — not the support service — may hold a GitHub token. When one
        // is not configured the connector reports graceful-disabled and the reporter
        // stays report-only (sanitized issue prepared, not filed).
        using GitHubIssueConnector issueTracker = new(bugReportConfiguration, sharedHttpClient);
        BugReportReporter bugReportReporter = new(issueTracker, bugReportConfiguration.Labels);
        IEventIdempotencyStore eventIdempotencyStore = EventIdempotencyStoreFactory.Create(
            bugReportConfiguration,
            Console.Error);

        // Durably audit every security-relevant outcome (invalid-signature,
        // stale/replay, unmapped-component, duplicate-skip, filing-failure) through
        // the shared JSONL audit hook, not just stderr.
        IAuditSink bugReportAuditSink = AuditSinkFactory.Create(policy.AuditHookTarget);
        await using (bugReportAuditSink)
        {
            AuditContext bugReportAuditContext = new(
                Guid.NewGuid().ToString("n"),
                runtime.ExecutionMode.ToString().ToLowerInvariant(),
                runtime.ExecutionTier.ToConfigValue(),
                policy.ApprovalMode.ToConfigValue(),
                options.Provider.ToConfigValue(),
                bugReportAuditSink);

            BugReportWebhookHandler bugReportHandler = new(
                bugReportConfiguration.WebhookSecret,
                bugReportConfiguration.Allowlist,
                bugReportConfiguration.ReplayWindow,
                onAccepted: (report, repo, token) => bugReportReporter.ReportAsync(report, repo, token),
                idempotencyStore: eventIdempotencyStore,
                auditContext: bugReportAuditContext);
            await using BugReportWebhookListener bugReportListener = new(bugReportConfiguration, bugReportHandler);
            await bugReportListener.RunAsync(cancellationTokenSource.Token);
        }

        return;
    }

    if (options.Mcp)
    {
        Environment.ExitCode = await McpStdioServerHost.RunAsync(
            runtime,
            policy,
            backendConfiguration,
            backendGateway,
            supportGateway,
            cancellationTokenSource.Token);
        return;
    }

    auditSink = AuditSinkFactory.Create(policy.AuditHookTarget);

    string detectedEdition = await EditionDetector.DetectAsync(backendGateway, cancellationTokenSource.Token);

    IList<AITool> tools = CapabilityToolset.Create(runtime, backendGateway, policy, supportGateway, detectedEdition, auditSink);
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
    // Scrub before writing: an exception message/stack can carry secrets or
    // endpoints (e.g. a connection string or token echoed from a failed call),
    // and this top-level boundary is otherwise the one logging path that bypasses
    // the transport/MCP redaction.
    Console.Error.WriteLine(Redaction.Scrub(exception.ToString()));
    Environment.ExitCode = 1;
}
finally
{
    if (auditSink is not null)
    {
        await auditSink.DisposeAsync();
    }
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
                        await ToolCallAuditor.EmitAsync(auditContext, record, result.Result, cancellationToken);
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
        // Redact by key and content before echoing to stdout: this preview is a
        // logging boundary the transport/MCP redaction does not cover, so a secret
        // passed as an argument would otherwise leak here.
        string value = Redaction.ScrubValue(kvp.Key, kvp.Value?.ToString() ?? "null");
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
