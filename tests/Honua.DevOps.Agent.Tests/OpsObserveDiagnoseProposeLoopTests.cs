using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Honua.DevOps.Agent.Operations;
using Honua.DevOps.Agent.Operations.Observability;

namespace Honua.DevOps.Agent.Tests;

public sealed class OpsObserveDiagnoseProposeLoopTests
{
    [Fact]
    public async Task RunAsync_ActionableFinding_UsesBoundedMcpEvidenceAndFindingIdProposal()
    {
        McpOpsServerEmulator emulator = new();
        TestHttpMessageHandler handler = new(emulator.Respond);
        using HttpClient httpClient = new(handler) { Timeout = TimeSpan.FromSeconds(5) };
        using BackendGateway gateway = new(CreateBackendConfiguration(), httpClient);
        OpsObserveDiagnoseProposeLoop loop = new(
            CreateRuntime(ExecutionTier.Propose),
            gateway);

        OpsLoopReport report = await loop.RunAsync(
            findingId: string.Empty,
            severity: "Critical",
            rule: string.Empty,
            lookbackHours: 500,
            pageSize: 500,
            proposeRecommendedAction: true,
            CancellationToken.None);

        Assert.Equal("proposal-created", report.Status);
        Assert.Equal("honua-server-mcp", report.ObservabilitySource);
        Assert.Equal("Degraded", report.OverallHealth);
        Assert.Equal("2026.07.0", report.PlatformReleaseVersion);
        Assert.False(report.PlatformReleaseCoVersioned);
        Assert.True(report.SupportedKindsVerified);
        Assert.Equal(["AdminConfigChange", "Deploy", "MetadataRelease"], report.SupportedKinds);
        Assert.Equal(50, report.Bounds.PageSize);
        Assert.Equal(168, report.Bounds.LookbackHours);

        OpsLoopFindingReport finding = Assert.Single(report.Findings);
        Assert.Equal("deploy-stuck-abc", finding.FindingId);
        Assert.Equal("Deploy", finding.RecommendedAction!.Kind);
        Assert.True(finding.RecommendedAction.Supported);
        Assert.Equal(["alert:42"], finding.RelatedAlertIds);
        Assert.Equal(["release:77"], finding.RelatedEventIds);
        Assert.Equal(["op-7"], finding.RelatedDeployOperationIds);
        Assert.NotNull(finding.Proposal);
        Assert.Equal("ProposalCreated", finding.Proposal!.GatewayStatus);
        Assert.Equal("proposal-9", finding.Proposal.ProposalId);

        CapturedRequest[] mcpCalls = handler.CapturedRequests
            .Where(request => request.Method == "POST" && request.Uri.EndsWith("/mcp", StringComparison.Ordinal))
            .ToArray();
        Assert.All(mcpCalls, request => Assert.Equal("test-admin-key", request.ApiKey));
        Assert.All(
            mcpCalls.Where(request => !IsMcpMethod(request, "initialize")),
            request => Assert.Equal("session-135", request.McpSessionId));

        JsonElement alertArguments = FindToolArguments(handler.CapturedRequests, "honua_alert_events");
        Assert.Equal(50, alertArguments.GetProperty("pageSize").GetInt32());
        Assert.True(alertArguments.TryGetProperty("from", out _));
        Assert.True(alertArguments.TryGetProperty("to", out _));
        Assert.False(alertArguments.TryGetProperty("severity", out _));

        JsonElement timelineArguments = FindToolArguments(handler.CapturedRequests, "honua_operate_events");
        Assert.Equal(50, timelineArguments.GetProperty("pageSize").GetInt32());
        Assert.True(timelineArguments.TryGetProperty("from", out _));
        Assert.True(timelineArguments.TryGetProperty("to", out _));

        JsonElement deployArguments = FindToolArguments(handler.CapturedRequests, "honua_deploy_operations");
        Assert.Equal(1, deployArguments.GetProperty("page").GetInt32());
        Assert.Equal(50, deployArguments.GetProperty("pageSize").GetInt32());

        JsonElement discoveryArguments = FindToolArguments(handler.CapturedRequests, "honua_propose_operation");
        Assert.Equal(JsonValueKind.Object, discoveryArguments.ValueKind);
        Assert.Empty(discoveryArguments.EnumerateObject());

        CapturedRequest proposal = Assert.Single(
            handler.CapturedRequests,
            request => request.Method == "POST" &&
                request.Uri.EndsWith("/api/v1/admin/observability/findings/deploy-stuck-abc/propose", StringComparison.Ordinal));
        Assert.Equal("test-admin-key", proposal.ApiKey);

        // The duplicate fixture entry represents the same live condition. The client de-duplicates
        // by deterministic finding id, and the server uses that same id as its gateway idempotency key.
        Assert.Equal(1, emulator.ProposalCallCount);
        Assert.Contains(handler.CapturedRequests, request => request.Method == "DELETE" && request.Uri.EndsWith("/mcp", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunAsync_PlanTier_ReportsCandidateWithoutProposing()
    {
        McpOpsServerEmulator emulator = new();
        TestHttpMessageHandler handler = new(emulator.Respond);
        using HttpClient httpClient = new(handler) { Timeout = TimeSpan.FromSeconds(5) };
        using BackendGateway gateway = new(CreateBackendConfiguration(), httpClient);
        OpsObserveDiagnoseProposeLoop loop = new(CreateRuntime(ExecutionTier.Plan), gateway);

        OpsLoopReport report = await loop.RunAsync(
            findingId: "deploy-stuck-abc",
            severity: string.Empty,
            rule: string.Empty,
            lookbackHours: 4,
            pageSize: 10,
            proposeRecommendedAction: true,
            CancellationToken.None);

        Assert.Equal("proposal-not-authorized", report.Status);
        OpsLoopFindingReport finding = Assert.Single(report.Findings);
        Assert.Null(finding.Proposal);
        Assert.Contains(report.Limitations, value => value.Contains("execution tier `propose`", StringComparison.Ordinal));
        Assert.Equal(0, emulator.ProposalCallCount);
        Assert.DoesNotContain(
            handler.CapturedRequests,
            request => request.Body is not null && IsToolCall(request, "honua_propose_operation"));
    }

    [Fact]
    public async Task RunAsync_ProposalNotRequested_UsesReadOnlyMcpToolsOnly()
    {
        McpOpsServerEmulator emulator = new();
        TestHttpMessageHandler handler = new(emulator.Respond);
        using HttpClient httpClient = new(handler) { Timeout = TimeSpan.FromSeconds(5) };
        using BackendGateway gateway = new(CreateBackendConfiguration(), httpClient);
        OpsObserveDiagnoseProposeLoop loop = new(CreateRuntime(ExecutionTier.Propose), gateway);

        OpsLoopReport report = await loop.RunAsync(
            findingId: string.Empty,
            severity: string.Empty,
            rule: string.Empty,
            lookbackHours: 4,
            pageSize: 10,
            proposeRecommendedAction: false,
            CancellationToken.None);

        Assert.Equal("diagnosed", report.Status);
        Assert.False(report.SupportedKindsVerified);
        Assert.Equal(0, emulator.ProposalCallCount);
        Assert.DoesNotContain(
            handler.CapturedRequests,
            request => request.Body is not null && IsToolCall(request, "honua_propose_operation"));
    }

    [Fact]
    public async Task RunAsync_McpReadFails_ReturnsUnavailableWithoutProposal()
    {
        McpOpsServerEmulator emulator = new(failHealthRead: true);
        TestHttpMessageHandler handler = new(emulator.Respond);
        using HttpClient httpClient = new(handler) { Timeout = TimeSpan.FromSeconds(5) };
        using BackendGateway gateway = new(CreateBackendConfiguration(), httpClient);
        OpsObserveDiagnoseProposeLoop loop = new(CreateRuntime(ExecutionTier.Propose), gateway);

        OpsLoopReport report = await loop.RunAsync(
            findingId: string.Empty,
            severity: string.Empty,
            rule: string.Empty,
            lookbackHours: 24,
            pageSize: 25,
            proposeRecommendedAction: true,
            CancellationToken.None);

        Assert.Equal("observability-unavailable", report.Status);
        Assert.Empty(report.Findings);
        Assert.Contains(report.Limitations, value => value.Contains("honua_ops_health", StringComparison.Ordinal));
        Assert.Equal(0, emulator.ProposalCallCount);
    }

    [Fact]
    public async Task RunAsync_McpTransportFails_ReturnsUnavailableWithoutLeakingTransportDetail()
    {
        TestHttpMessageHandler handler = new(_ => throw new HttpRequestException("secret upstream host detail"));
        using HttpClient httpClient = new(handler) { Timeout = TimeSpan.FromSeconds(5) };
        using BackendGateway gateway = new(CreateBackendConfiguration(), httpClient);
        OpsObserveDiagnoseProposeLoop loop = new(CreateRuntime(ExecutionTier.Propose), gateway);

        OpsLoopReport report = await loop.RunAsync(
            findingId: string.Empty,
            severity: string.Empty,
            rule: string.Empty,
            lookbackHours: 24,
            pageSize: 25,
            proposeRecommendedAction: true,
            CancellationToken.None);

        Assert.Equal("observability-unavailable", report.Status);
        string limitation = Assert.Single(report.Limitations);
        Assert.Contains("could not be reached", limitation, StringComparison.Ordinal);
        Assert.DoesNotContain("secret upstream host detail", limitation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_SessionCleanupTimesOut_PreservesCompletedReadResult()
    {
        McpOpsServerEmulator emulator = new(cancelDelete: true);
        TestHttpMessageHandler handler = new(emulator.Respond);
        using HttpClient httpClient = new(handler) { Timeout = TimeSpan.FromSeconds(5) };
        using BackendGateway gateway = new(CreateBackendConfiguration(), httpClient);
        OpsObserveDiagnoseProposeLoop loop = new(CreateRuntime(ExecutionTier.Plan), gateway);

        OpsLoopReport report = await loop.RunAsync(
            findingId: string.Empty,
            severity: string.Empty,
            rule: string.Empty,
            lookbackHours: 1,
            pageSize: 10,
            proposeRecommendedAction: false,
            CancellationToken.None);

        Assert.Equal("diagnosed", report.Status);
        Assert.Contains(handler.CapturedRequests, request => request.Method == "DELETE");
    }

    [Theory]
    [InlineData("Failed", "execution-failed")]
    [InlineData("RolledBack", "execution-rolled-back")]
    [InlineData("Indeterminate", "execution-indeterminate")]
    [InlineData("Canceled", "execution-canceled")]
    public async Task RunAsync_TerminalGatewayOutcome_IsReportedExplicitly(
        string gatewayStatus,
        string expectedStatus)
    {
        McpOpsServerEmulator emulator = new(proposalStatus: gatewayStatus);
        TestHttpMessageHandler handler = new(emulator.Respond);
        using HttpClient httpClient = new(handler) { Timeout = TimeSpan.FromSeconds(5) };
        using BackendGateway gateway = new(CreateBackendConfiguration(), httpClient);
        OpsObserveDiagnoseProposeLoop loop = new(CreateRuntime(ExecutionTier.Propose), gateway);

        OpsLoopReport report = await loop.RunAsync(
            findingId: "deploy-stuck-abc",
            severity: string.Empty,
            rule: string.Empty,
            lookbackHours: 1,
            pageSize: 10,
            proposeRecommendedAction: true,
            CancellationToken.None);

        Assert.Equal(expectedStatus, report.Status);
        Assert.Equal(gatewayStatus, Assert.Single(report.Findings).Proposal!.GatewayStatus);
    }

    private static JsonElement FindToolArguments(IEnumerable<CapturedRequest> requests, string toolName)
    {
        CapturedRequest request = Assert.Single(
            requests,
            candidate => candidate.Body is not null && IsToolCall(candidate, toolName));
        using JsonDocument document = JsonDocument.Parse(request.Body!);
        return document.RootElement
            .GetProperty("params")
            .GetProperty("arguments")
            .Clone();
    }

    private static bool IsMcpMethod(CapturedRequest request, string method)
    {
        if (request.Body is null)
        {
            return false;
        }

        using JsonDocument document = JsonDocument.Parse(request.Body);
        return document.RootElement.TryGetProperty("method", out JsonElement methodElement) &&
            string.Equals(methodElement.GetString(), method, StringComparison.Ordinal);
    }

    private static bool IsToolCall(CapturedRequest request, string toolName)
    {
        using JsonDocument document = JsonDocument.Parse(request.Body!);
        JsonElement root = document.RootElement;
        return root.TryGetProperty("method", out JsonElement method) &&
            method.GetString() == "tools/call" &&
            root.GetProperty("params").GetProperty("name").GetString() == toolName;
    }

    private static OperationRuntime CreateRuntime(ExecutionTier tier) => new(
        ExecutionMode.Plan,
        tier,
        "honua-gitops",
        ["dev", "staging", "prod"],
        "https://github.com/honua-io/honua-iac",
        "trunk",
        "/tmp/honua-iac",
        ["eks", "aks"]);

    private static BackendConfiguration CreateBackendConfiguration() => new(
        HonuaApiBaseUri: new Uri("http://localhost:8080"),
        OTelBaseUri: new Uri("http://localhost:4318"),
        HonuaApiKey: "test-admin-key",
        OTelApiKey: null,
        HonuaReadinessPath: "healthz/ready",
        OTelHealthPath: "health",
        OTelLogsPath: "v1/logs/search",
        OTelMetricsPath: "v1/metrics/search",
        HonuaAdminErrorsPath: "api/v1/admin/observability/errors",
        HonuaAdminTelemetryPath: "api/v1/admin/observability/telemetry",
        HonuaMetricsHealthPath: "api/v1/metrics/health",
        HonuaMetricsPerformancePath: "api/v1/metrics/performance",
        HonuaMetricsDatabasePath: "api/v1/metrics/database",
        HonuaMetricsCachePath: "api/v1/metrics/cache",
        HonuaMetricsMemoryPath: "api/v1/metrics/memory",
        HonuaQueryCacheStatisticsPath: "api/v1/admin/performance/database/query-cache/statistics",
        HonuaAdminVersionPath: "api/v1/admin/version",
        HonuaAdminCapabilitiesPath: "api/v1/admin/capabilities",
        HonuaManifestExportPath: "api/v1/admin/manifest",
        HonuaManifestApplyPath: "api/v1/admin/manifest/apply",
        RequestTimeout: TimeSpan.FromSeconds(5));

    private sealed class McpOpsServerEmulator(
        bool failHealthRead = false,
        bool cancelDelete = false,
        string proposalStatus = "ProposalCreated")
    {
        internal int ProposalCallCount { get; private set; }

        internal HttpResponseMessage Respond(HttpRequestMessage request)
        {
            string path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Delete && path == "/mcp")
            {
                if (cancelDelete)
                {
                    throw new TaskCanceledException("cleanup timed out");
                }

                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            if (request.Method == HttpMethod.Post && path.EndsWith("/findings/deploy-stuck-abc/propose", StringComparison.Ordinal))
            {
                ProposalCallCount++;
                return TestHttpMessageHandler.JsonOk(new
                {
                    findingId = "deploy-stuck-abc",
                    status = proposalStatus,
                    proposalId = proposalStatus == "ProposalCreated" ? "proposal-9" : null,
                    executionOperationId = proposalStatus == "ProposalCreated" ? null : "action-9",
                    message = proposalStatus == "ProposalCreated"
                        ? "Awaiting Console approval."
                        : "Server action reached a terminal outcome."
                });
            }

            Assert.Equal("/mcp", path);
            string body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            using JsonDocument document = JsonDocument.Parse(body);
            JsonElement root = document.RootElement;
            string method = root.GetProperty("method").GetString()!;

            if (method == "initialize")
            {
                HttpResponseMessage response = JsonRpcResult(root, new
                {
                    protocolVersion = "2025-06-18",
                    capabilities = new { tools = new { listChanged = true } }
                });
                response.Headers.TryAddWithoutValidation("Mcp-Session-Id", "session-135");
                return response;
            }

            if (method == "notifications/initialized")
            {
                return new HttpResponseMessage(HttpStatusCode.Accepted);
            }

            Assert.Equal("tools/call", method);
            string tool = root.GetProperty("params").GetProperty("name").GetString()!;
            object structured = tool switch
            {
                "honua_ops_health" when failHealthRead => new
                {
                    code = "unavailable",
                    message = "ops health store unavailable"
                },
                "honua_ops_health" => new
                {
                    generatedAt = "2026-07-10T00:00:00Z",
                    overallStatus = "Degraded",
                    health = new { status = "Degraded" },
                    servingLatency = new { protocols = Array.Empty<object>() },
                    geoprocessing = new { totalActive = 0 },
                    alertDispatch = new { deadLetteredCount = 2 },
                    deploy = new { status = "blocked" },
                    database = new { errorRate = 0.01 }
                },
                "honua_ops_findings" => new
                {
                    generatedAt = "2026-07-10T00:00:00Z",
                    findings = new object[]
                    {
                        Finding(),
                        Finding()
                    }
                },
                "honua_alert_events" => new
                {
                    items = new[]
                    {
                        new
                        {
                            eventId = 42,
                            ruleId = (long?)null,
                            serviceId = "",
                            layerId = 0,
                            objectId = 0,
                            triggerType = "threshold",
                            severity = "critical",
                            occurredAt = "2026-07-09T23:50:00Z",
                            incidentStatus = "ongoing",
                            incidentDurationMs = 1000,
                            lifecycleStatus = "open",
                            resourceRef = "alert/42"
                        }
                    }
                },
                "honua_operate_events" => new
                {
                    items = new[]
                    {
                        new
                        {
                            eventId = "release:77",
                            kind = "release",
                            severity = "error",
                            occurredAt = "2026-07-09T23:51:00Z",
                            title = "Deploy requires manual intervention",
                            operationId = "op-7",
                            releaseId = "2026.07.0",
                            resourceRef = "deploy-operation:op-7"
                        }
                    },
                    partialResult = false
                },
                "honua_platform_release_status" => new
                {
                    releaseVersion = "2026.07.0",
                    releaseDeclared = true,
                    isCoVersioned = false,
                    serving = Array.Empty<object>(),
                    execution = Array.Empty<object>(),
                    skewedIds = new[] { "serving-us-west" }
                },
                "honua_deploy_operations" => new
                {
                    items = new[]
                    {
                        new
                        {
                            operationId = "op-7",
                            kind = "Deploy",
                            status = "ManualIntervention",
                            priority = "High",
                            warnings = Array.Empty<string>(),
                            blockingReasons = new[] { "SLO gate failed" },
                            createdAt = "2026-07-09T23:40:00Z",
                            updatedAt = "2026-07-09T23:51:00Z",
                            target = new
                            {
                                targetId = "prod-api",
                                targetKind = "Service",
                                backend = "honua-kubernetes-argo-rollouts",
                                environment = "prod",
                                targetName = "roads-api",
                                desiredRevision = "sha256:new",
                                parameters = new { }
                            }
                        }
                    },
                    page = 1,
                    pageSize = 50,
                    totalCount = 1,
                    hasMore = false
                },
                "honua_propose_operation" => new
                {
                    outcome = "rejected",
                    requiresApproval = false,
                    supportedKinds = new[] { "AdminConfigChange", "Deploy", "MetadataRelease" },
                    message = "Unknown or missing operation kind."
                },
                _ => throw new Xunit.Sdk.XunitException($"Unexpected MCP tool `{tool}`.")
            };

            return JsonRpcToolResult(root, structured, failHealthRead && tool == "honua_ops_health");
        }

        private static object Finding() => new
        {
            id = "deploy-stuck-abc",
            rule = "deploy-manual-intervention",
            severity = "Critical",
            title = "Deploy requires manual intervention",
            explanation = "The deployment stopped after an SLO breach.",
            detectedAt = "2026-07-09T23:52:00Z",
            subject = new
            {
                targetId = "prod-api",
                operationId = "op-7",
                releaseVersion = "2026.07.0"
            },
            evidenceRefs = new[] { "alert:42", "release:77", "deploy-operation:op-7" },
            recommendedAction = new
            {
                kind = "Deploy",
                summary = "Deploy the prior known-good revision.",
                reason = "Recover the failed deployment through the governed gateway.",
                autoSafe = false,
                blastRadius = 1
            }
        };

        private static HttpResponseMessage JsonRpcResult(JsonElement request, object result) =>
            TestHttpMessageHandler.JsonOk(new
            {
                jsonrpc = "2.0",
                id = request.GetProperty("id").Clone(),
                result
            });

        private static HttpResponseMessage JsonRpcToolResult(JsonElement request, object structuredContent, bool isError) =>
            TestHttpMessageHandler.JsonOk(new
            {
                jsonrpc = "2.0",
                id = request.GetProperty("id").Clone(),
                result = new
                {
                    content = new[] { new { type = "text", text = isError ? "tool unavailable" : "ok" } },
                    structuredContent,
                    isError
                }
            });
    }
}
