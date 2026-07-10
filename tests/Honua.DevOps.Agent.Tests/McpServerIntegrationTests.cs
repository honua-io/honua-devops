using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Honua.DevOps.Agent.Tests;

/// <summary>
/// Boots the real `honua-devops --mcp` stdio server as a child process (the same entrypoint
/// `claude mcp add honua-devops -- dotnet ... --mcp` registers), pointed at a fake Honua
/// backend, and shares one MCP client across the tests in this class.
/// </summary>
public sealed class McpServerFixture : IAsyncLifetime
{
    private HttpListener? _backend;
    private CancellationTokenSource? _backendCts;
    private Task? _backendLoop;
    private string? _workingDirectory;

    public McpClient Client { get; private set; } = null!;
    public ConcurrentQueue<string> CapturedBackendRequests { get; } = new();
    public string AuditFilePath { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        int port = ReserveLoopbackPort();
        string baseUrl = $"http://127.0.0.1:{port}/";

        _backend = new HttpListener();
        _backend.Prefixes.Add(baseUrl);
        _backend.Start();
        _backendCts = new CancellationTokenSource();
        _backendLoop = Task.Run(() => ServeFakeHonuaBackendAsync(_backend, _backendCts.Token));

        _workingDirectory = Directory.CreateTempSubdirectory("honua-devops-mcp-test").FullName;
        AuditFilePath = Path.Combine(_workingDirectory, "audit.jsonl");

        string agentDllPath = Path.Combine(AppContext.BaseDirectory, "Honua.DevOps.Agent.dll");
        Assert.True(File.Exists(agentDllPath), $"Expected agent assembly at {agentDllPath}.");

        StdioClientTransport transport = new(new StdioClientTransportOptions
        {
            Name = "honua-devops",
            Command = "dotnet",
            Arguments = [agentDllPath, "--mcp"],
            WorkingDirectory = _workingDirectory,
            EnvironmentVariables = new Dictionary<string, string?>
            {
                ["HONUA_DEVOPS_HONUA_API_BASE_URL"] = baseUrl,
                ["HONUA_DEVOPS_OTEL_BASE_URL"] = baseUrl,
                ["HONUA_DEVOPS_HONUA_API_KEY"] = null,
                ["HONUA_DEVOPS_EXECUTION_MODE"] = "plan",
                ["HONUA_DEVOPS_EXECUTION_TIER"] = "plan",
                ["HONUA_DEVOPS_APPROVAL_MODE"] = "pr-first",
                ["HONUA_DEVOPS_AUDIT_HOOK_TARGET"] = $"file://{AuditFilePath}",
                ["HONUA_DEVOPS_BACKEND_TIMEOUT_SECONDS"] = "10",
                ["HONUA_DEVOPS_ALLOWED_ENVIRONMENTS"] = "dev,staging,prod",
                ["HONUA_DEVOPS_TERRAFORM_TARGETS"] = "lambda,eks",
                ["HONUA_DEVOPS_TERRAFORM_LOCAL_PATH"] = _workingDirectory,
                ["HONUA_DEVOPS_DEPLOY_TARGET_ID"] = "",
                ["HONUA_DEVOPS_SUPPORT_API_BASE_URL"] = ""
            }
        });

        using CancellationTokenSource startupCts = new(TimeSpan.FromMinutes(2));
        Client = await McpClient.CreateAsync(transport, cancellationToken: startupCts.Token);
    }

    public async Task DisposeAsync()
    {
        if (Client is not null)
        {
            await Client.DisposeAsync();
        }

        _backendCts?.Cancel();
        try
        {
            _backend?.Stop();
            _backend?.Close();
        }
        catch (ObjectDisposedException)
        {
            // already torn down
        }

        if (_backendLoop is not null)
        {
            try
            {
                await _backendLoop;
            }
            catch (Exception)
            {
                // listener teardown races are irrelevant to test outcomes
            }
        }

        _backendCts?.Dispose();

        if (_workingDirectory is not null && Directory.Exists(_workingDirectory))
        {
            try
            {
                Directory.Delete(_workingDirectory, recursive: true);
            }
            catch (IOException)
            {
                // best effort cleanup
            }
        }
    }

    private static int ReserveLoopbackPort()
    {
        TcpListener probe = new(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private async Task ServeFakeHonuaBackendAsync(HttpListener listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync();
            }
            catch (Exception exception) when (exception is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }

            string path = context.Request.Url?.AbsolutePath ?? string.Empty;
            CapturedBackendRequests.Enqueue($"{context.Request.HttpMethod} {path}");

            string payload;
            if (path.Contains("healthz/ready", StringComparison.Ordinal))
            {
                payload = """{"status":"ready"}""";
            }
            else if (path.Contains("/api/v1/admin/capabilities", StringComparison.Ordinal))
            {
                payload = """{"edition":"enterprise","features":["runbooks","auto-remediation"]}""";
            }
            else if (path.Contains("/api/v1/admin/manifest", StringComparison.Ordinal))
            {
                payload = """{"services":["roads-api","geocoder"]}""";
            }
            else
            {
                payload = """{"status":"ok"}""";
            }

            byte[] body = Encoding.UTF8.GetBytes(payload);
            context.Response.StatusCode = (int)HttpStatusCode.OK;
            context.Response.ContentType = "application/json";
            context.Response.ContentLength64 = body.Length;
            await context.Response.OutputStream.WriteAsync(body, cancellationToken);
            context.Response.OutputStream.Close();
        }
    }
}

public sealed class McpServerIntegrationTests(McpServerFixture fixture) : IClassFixture<McpServerFixture>
{
    private static readonly string[] ExpectedToolNames =
    [
        "describe_environment",
        "find_recent_operations",
        "analyze_logs",
        "analyze_metrics",
        "tune_performance",
        "troubleshoot_incident",
        "plan_server_upgrade",
        "plan_gitops_engine",
        "generate_metadata_release_changeset",
        "explain_metadata_release_changeset",
        "plan_metadata_release_gitops",
        "deploy_service_gitops",
        "analyze_customer_requirements",
        "recommend_deployment_topology",
        "record_gitops_proposal_decision",
        "plan_forward_fix",
        "inspect_metadata_release",
        "triage_support_ticket",
        "process_pending_tickets",
        "get_support_ticket_console_view",
        "honua_diagnose",
        "honua_explain_slow_queries",
        "honua_observe_diagnose_propose",
        "honua_runbook_execute",
        "honua_auto_remediation_plan",
        "plan_deliverable_lifecycle",
        "create_gitops_proposal",
        "plan_gp_substrate",
        "plan_gp_job_sizing",
        "plan_azure_gp_substrate",
        "plan_azure_gp_job_sizing",
        "get_gitops_proposal",
        "get_devops_operation_status",
        "build_ai_devops_brief",
        "explain_release_package"
    ];

    [Fact]
    public async Task ListTools_ExposesEveryOperatorToolOneToOne()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        IList<McpClientTool> tools = await fixture.Client.ListToolsAsync(cancellationToken: cts.Token);

        string[] exposedNames = [.. tools.Select(tool => tool.Name).Order()];
        Assert.Equal(ExpectedToolNames.Order().ToArray(), exposedNames);
        Assert.All(tools, tool => Assert.False(string.IsNullOrWhiteSpace(tool.Description)));
    }

    [Fact]
    public async Task QuickstartDocs_ListEveryExposedToolAndTheCorrectCount()
    {
        // Guards against doc drift: the "Exposed tools" list and the tools=<n>
        // banner in docs/QUICKSTART-MCP.md must stay in lockstep with the tools
        // the MCP server actually exposes (i.e. CapabilityToolset registrations).
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        IList<McpClientTool> tools = await fixture.Client.ListToolsAsync(cancellationToken: cts.Token);
        HashSet<string> exposedNames = [.. tools.Select(tool => tool.Name)];

        string repoRoot = FindRepoRoot();
        string quickstart = File.ReadAllText(Path.Combine(repoRoot, "docs", "QUICKSTART-MCP.md"));

        // Scope to the "## Exposed tools" section so prose elsewhere can't satisfy the check.
        Match section = Regex.Match(
            quickstart,
            @"## Exposed tools.*?(?=\n## |\z)",
            RegexOptions.Singleline);
        Assert.True(section.Success, "docs/QUICKSTART-MCP.md is missing an '## Exposed tools' section.");

        // Tool names are the only backtick tokens in that section containing an underscore
        // (excludes prose tokens like `plan`, `pr-first`, `CapabilityToolset`).
        HashSet<string> documentedNames =
        [
            .. Regex.Matches(section.Value, @"`([a-z][a-z0-9_]*_[a-z0-9_]+)`")
                .Select(match => match.Groups[1].Value)
        ];

        Assert.Equal(exposedNames.OrderBy(name => name), documentedNames.OrderBy(name => name));

        // The documented count/banner must match the live tool count.
        Assert.Contains($"tools={exposedNames.Count}", quickstart);
        Assert.Contains($"{exposedNames.Count} operator tools", quickstart);
    }

    private static string FindRepoRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "docs", "QUICKSTART-MCP.md")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"Could not locate repo root (docs/QUICKSTART-MCP.md) from {AppContext.BaseDirectory}.");
    }

    [Fact]
    public async Task CallTool_DescribeEnvironment_RoundTripsAgainstFakeBackendAndEmitsAudit()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        CallToolResult result = await fixture.Client.CallToolAsync(
            "describe_environment",
            arguments: new Dictionary<string, object?>(),
            cancellationToken: cts.Token);

        Assert.NotEqual(true, result.IsError);
        JsonElement payload = ExtractJsonPayload(result);
        Assert.Equal("environment-described", GetString(payload, "Status"));
        Assert.False(string.IsNullOrWhiteSpace(GetString(payload, "Summary")));

        Assert.Contains(
            fixture.CapturedBackendRequests,
            request => request.Contains("/api/v1/admin/capabilities", StringComparison.Ordinal));

        string auditJson = ReadSharedFile(fixture.AuditFilePath);
        string? auditLine = auditJson
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(line => line.Contains("\"describe_environment\"", StringComparison.Ordinal));
        Assert.NotNull(auditLine);

        using JsonDocument auditRecord = JsonDocument.Parse(auditLine!);
        Assert.Equal("environment-described", auditRecord.RootElement.GetProperty("Status").GetString());
        Assert.False(auditRecord.RootElement.GetProperty("Mutated").GetBoolean());
        Assert.Equal("plan", auditRecord.RootElement.GetProperty("ExecutionMode").GetString());
        Assert.Equal("pr-first", auditRecord.RootElement.GetProperty("ApprovalMode").GetString());
        Assert.Equal("mcp", auditRecord.RootElement.GetProperty("Provider").GetString());
    }

    [Fact]
    public async Task CallTool_RunbookExecute_StaysPlanGatedOverMcp()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        CallToolResult result = await fixture.Client.CallToolAsync(
            "honua_runbook_execute",
            arguments: new Dictionary<string, object?>
            {
                ["runbookName"] = "deploy-submit",
                ["service"] = "roads-api",
                ["environment"] = "dev",
                ["parameters"] = "operationId=op-123",
                ["confirmed"] = true,
                ["edition"] = "enterprise"
            },
            cancellationToken: cts.Token);

        Assert.NotEqual(true, result.IsError);
        JsonElement payload = ExtractJsonPayload(result);

        // plan mode + plan tier: the write-capable runbook must stay a plan, and the
        // backend must never receive the deploy-operation submit call.
        Assert.Equal("runbook-plan-ready", GetString(payload, "Status"));
        Assert.DoesNotContain(
            fixture.CapturedBackendRequests,
            request => request.StartsWith("POST ", StringComparison.Ordinal)
                && request.Contains("/api/v1/admin/deploy/operations", StringComparison.Ordinal));
    }

    private static JsonElement ExtractJsonPayload(CallToolResult result)
    {
        if (result.StructuredContent is { } structured)
        {
            using JsonDocument document = JsonDocument.Parse(structured.GetRawText());
            return document.RootElement.Clone();
        }

        string text = string.Concat(
            result.Content
                .OfType<TextContentBlock>()
                .Select(block => block.Text));
        using JsonDocument parsed = JsonDocument.Parse(text);
        return parsed.RootElement.Clone();
    }

    private static string? GetString(JsonElement element, string pascalCaseName)
    {
        if (element.TryGetProperty(pascalCaseName, out JsonElement exact) && exact.ValueKind == JsonValueKind.String)
        {
            return exact.GetString();
        }

        string camelCaseName = char.ToLowerInvariant(pascalCaseName[0]) + pascalCaseName[1..];
        if (element.TryGetProperty(camelCaseName, out JsonElement camel) && camel.ValueKind == JsonValueKind.String)
        {
            return camel.GetString();
        }

        return null;
    }

    private static string ReadSharedFile(string path)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using StreamReader reader = new(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
