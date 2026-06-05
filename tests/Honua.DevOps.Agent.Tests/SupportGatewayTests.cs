using System.Text.Json;
using Honua.DevOps.Agent.Operations;
using Honua.DevOps.Agent.Operations.GuidedFix;
using Honua.DevOps.Agent.Operations.OperatorPolicy;
using Honua.DevOps.Agent.Operations.Troubleshooting;
using OperatorPolicyModel = Honua.DevOps.Agent.Operations.OperatorPolicy.OperatorPolicy;

namespace Honua.DevOps.Agent.Tests;

public class SupportGatewayTests
{
    [Fact]
    public async Task ListPendingTicketsAsync_ReturnsTicketsFromSupportApi()
    {
        object[] tickets =
        [
            new { id = "T-001", phase = "intake", severity = "high", environment = "prod", service = "roads-api", symptoms = "slow queries" },
            new { id = "T-002", phase = "triaging", severity = "medium", environment = "dev", service = "maps-api", symptoms = "timeout" },
            new { id = "T-003", phase = "diagnosed", severity = "low", environment = "staging", service = "tiles-api", symptoms = "none" }
        ];

        TestHttpMessageHandler handler = new(_ => TestHttpMessageHandler.JsonOk(tickets));
        using HttpClient httpClient = new(handler) { Timeout = TimeSpan.FromSeconds(5) };
        using SupportGateway gateway = new(CreateBackendConfiguration(), httpClient);

        BackendCallResult result = await gateway.ListPendingTicketsAsync();

        Assert.True(result.IsSuccess);
        Assert.Contains("200", result.Detail, StringComparison.Ordinal);
        CapturedRequest captured = Assert.Single(handler.CapturedRequests);
        Assert.Equal("GET", captured.Method);
        Assert.Contains("/api/v1/tickets", captured.Uri, StringComparison.Ordinal);
        Assert.Contains("T-001", result.PayloadPreview, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListPendingTicketsAsync_IncludesOperatorRoleHeader()
    {
        HttpRequestMessage? capturedRequest = null;
        TestHttpMessageHandler handler = new(request =>
        {
            capturedRequest = request;
            return TestHttpMessageHandler.JsonOk(Array.Empty<object>());
        });
        using HttpClient httpClient = new(handler) { Timeout = TimeSpan.FromSeconds(5) };
        using SupportGateway gateway = new(CreateBackendConfiguration(), httpClient);

        await gateway.ListPendingTicketsAsync();

        Assert.NotNull(capturedRequest);
        Assert.True(capturedRequest!.Headers.Contains("X-Operator-Role"));
        Assert.Equal("true", capturedRequest.Headers.GetValues("X-Operator-Role").First());
    }

    [Fact]
    public async Task ListPendingTicketsAsync_UsesBearerTokenWhenConfigured()
    {
        HttpRequestMessage? capturedRequest = null;
        TestHttpMessageHandler handler = new(request =>
        {
            capturedRequest = request;
            return TestHttpMessageHandler.JsonOk(Array.Empty<object>());
        });
        using HttpClient httpClient = new(handler) { Timeout = TimeSpan.FromSeconds(5) };
        using SupportGateway gateway = new(
            CreateBackendConfiguration(supportApiBearerToken: "support-operator-token"),
            httpClient);

        await gateway.ListPendingTicketsAsync();

        Assert.NotNull(capturedRequest);
        Assert.Equal("Bearer", capturedRequest!.Headers.Authorization?.Scheme);
        Assert.Equal("support-operator-token", capturedRequest.Headers.Authorization?.Parameter);
        Assert.False(capturedRequest.Headers.Contains("X-Operator-Role"));
    }

    [Fact]
    public async Task PostDiagnosisAsync_SendsCorrectJsonPayload()
    {
        TestHttpMessageHandler handler = new(_ => TestHttpMessageHandler.JsonOk(new { status = "ok" }));
        using HttpClient httpClient = new(handler) { Timeout = TimeSpan.FromSeconds(5) };
        using SupportGateway gateway = new(CreateBackendConfiguration(), httpClient);

        GuidedFixResult diagnosis = new(
            Mode: GuidedFixMode.ReadOnlyTriage,
            DiagnosisSummary: "Test diagnosis summary",
            Confidence: "high",
            MissingEvidence: [],
            RecommendedNextAction: "diagnosis-ready",
            GuidedCommands: ["curl -sf ${HONUA_SMOKE_BASE_URL}/healthz/ready"],
            ValidationSteps: ["Verify service health."],
            Escalation: null);

        BackendCallResult result = await gateway.PostDiagnosisAsync("T-001", diagnosis);

        Assert.True(result.IsSuccess);
        CapturedRequest captured = Assert.Single(handler.CapturedRequests);
        Assert.Equal("POST", captured.Method);
        Assert.Contains("/api/v1/tickets/T-001/diagnosis", captured.Uri, StringComparison.Ordinal);
        Assert.NotNull(captured.Body);

        using JsonDocument doc = JsonDocument.Parse(captured.Body!);
        Assert.Equal("Test diagnosis summary", doc.RootElement.GetProperty("summary").GetString());
        Assert.Equal("high", doc.RootElement.GetProperty("confidence").GetString());
        Assert.Equal("read-only-triage", doc.RootElement.GetProperty("mode").GetString());
        Assert.True(doc.RootElement.GetProperty("guidedCommands").GetArrayLength() > 0);
        Assert.True(doc.RootElement.GetProperty("validationSteps").GetArrayLength() > 0);
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("escalation").ValueKind);
    }

    [Fact]
    public async Task PostDiagnosisAsync_EncodesTicketIdAsSinglePathSegment()
    {
        TestHttpMessageHandler handler = new(_ => TestHttpMessageHandler.JsonOk(new { status = "ok" }));
        using HttpClient httpClient = new(handler) { Timeout = TimeSpan.FromSeconds(5) };
        using SupportGateway gateway = new(CreateBackendConfiguration(), httpClient);

        GuidedFixResult diagnosis = new(
            Mode: GuidedFixMode.ReadOnlyTriage,
            DiagnosisSummary: "Test diagnosis summary",
            Confidence: "high",
            MissingEvidence: [],
            RecommendedNextAction: "diagnosis-ready",
            GuidedCommands: [],
            ValidationSteps: [],
            Escalation: null);

        BackendCallResult result = await gateway.PostDiagnosisAsync("T-001/../close?x=1#fragment", diagnosis);

        Assert.True(result.IsSuccess);
        CapturedRequest captured = Assert.Single(handler.CapturedRequests);
        Assert.Contains("T-001%2F..%2Fclose%3Fx%3D1%23fragment/diagnosis", captured.Uri, StringComparison.Ordinal);
        Assert.DoesNotContain("/close?x=1", captured.Uri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PostDiagnosisAsync_IncludesEscalationWhenPresent()
    {
        TestHttpMessageHandler handler = new(_ => TestHttpMessageHandler.JsonOk(new { status = "ok" }));
        using HttpClient httpClient = new(handler) { Timeout = TimeSpan.FromSeconds(5) };
        using SupportGateway gateway = new(CreateBackendConfiguration(), httpClient);

        GuidedFixResult diagnosis = new(
            Mode: GuidedFixMode.OperatorScoped,
            DiagnosisSummary: "Escalation needed",
            Confidence: "medium",
            MissingEvidence: [],
            RecommendedNextAction: "approval-required-escalation",
            GuidedCommands: [],
            ValidationSteps: [],
            Escalation: new GuidedFixEscalation(
                Justification: "Critical issue",
                AccessScope: "operator-scoped",
                TtlMinutes: 30,
                RollbackIntent: "rollback-prepared",
                RequiredApprovalContext: ["approver-identity"],
                Trigger: GuidedFixEscalation.SeverityEscalationTrigger,
                Signal: "critical-severity ticket requested operator-scoped access"));

        BackendCallResult result = await gateway.PostDiagnosisAsync("T-002", diagnosis);

        Assert.True(result.IsSuccess);
        CapturedRequest captured = Assert.Single(handler.CapturedRequests);
        using JsonDocument doc = JsonDocument.Parse(captured.Body!);
        JsonElement escalation = doc.RootElement.GetProperty("escalation");
        Assert.Equal("Critical issue", escalation.GetProperty("justification").GetString());
        Assert.Equal("operator-scoped", escalation.GetProperty("accessScope").GetString());
        Assert.Equal(30, escalation.GetProperty("ttlMinutes").GetInt32());
        Assert.Equal("rollback-prepared", escalation.GetProperty("rollbackIntent").GetString());
        // Escalation rationale: "why escalated" trigger + signal travel with the diagnosis.
        Assert.Equal("severity-escalation", escalation.GetProperty("trigger").GetString());
        Assert.Equal(
            "critical-severity ticket requested operator-scoped access",
            escalation.GetProperty("signal").GetString());
    }

    [Fact]
    public async Task PostDiagnosisAsync_SerializesEvidenceAndScorecard()
    {
        TestHttpMessageHandler handler = new(_ => TestHttpMessageHandler.JsonOk(new { status = "ok" }));
        using HttpClient httpClient = new(handler) { Timeout = TimeSpan.FromSeconds(5) };
        using SupportGateway gateway = new(CreateBackendConfiguration(), httpClient);

        GuidedFixResult diagnosis = new(
            Mode: GuidedFixMode.GuidedFix,
            DiagnosisSummary: "Connection pool exhaustion matched the fault catalog.",
            Confidence: "high",
            MissingEvidence: [],
            RecommendedNextAction: "guided-customer-action",
            GuidedCommands: ["kubectl rollout restart deploy/roads-api"],
            ValidationSteps: ["Verify /healthz/ready returns 200."],
            Escalation: null);
        OperationEvidence evidence = new(
            Scope: "support-triage:T-777",
            RequestedAction: "diagnose",
            EffectiveAction: "guided-fix",
            DryRun: true,
            ExecutionMode: "plan",
            ExecutionTier: "plan",
            TargetEnvironments: ["prod"],
            CurrentRevision: null,
            DesiredRevision: null,
            GitOpsTool: "honua-gitops",
            TerraformRepository: "https://github.com/honua-io/honua-terraform",
            TerraformRef: "main",
            DeploymentTargets: ["aks"],
            PolicyGate: "guided-fix",
            ApprovalMode: "pr-first",
            AuditHookTarget: "stdout-evidence",
            SupportSessionAccess: "read-only",
            SupportSessionTtlMinutes: 60,
            SupportSessionCustomerVisible: true,
            BreakGlassPostActionReviewRequired: true,
            RequiredChecks: ["ticket-context", "diagnosis-evidence"],
            DiffSummary: null,
            GateStatus: "guided-customer-action",
            BackendEndpoint: "http://localhost:8080/api/v1/admin/observability/errors",
            BackendDetail: "200 OK");
        DiagnosisScorecard scorecard = new(
            ScenarioId: "FAULT-014",
            ScenarioName: "connection-pool exhaustion under synthetic load",
            DiagnosisCorrect: true,
            DiagnosisLatency: "single-pass",
            EvidenceQuality: 95,
            RemediationSafe: true,
            PolicyCompliant: true,
            RollbackGuidanceCorrect: true,
            RecoveryVerified: false,
            ServiceHealthRestored: false,
            FailureModes: []);

        BackendCallResult result = await gateway.PostDiagnosisAsync("T-777", diagnosis, evidence, scorecard);

        Assert.True(result.IsSuccess);
        CapturedRequest captured = Assert.Single(handler.CapturedRequests);
        using JsonDocument doc = JsonDocument.Parse(captured.Body!);
        Assert.Equal("support-triage:T-777", doc.RootElement.GetProperty("evidence").GetProperty("scope").GetString());
        Assert.Equal("FAULT-014", doc.RootElement.GetProperty("diagnosisScorecard").GetProperty("scenarioId").GetString());
        Assert.Equal("pass", doc.RootElement.GetProperty("diagnosisScorecard").GetProperty("overallResult").GetString());
    }

    [Fact]
    public async Task TriggerAutoBundleAsync_CallsCorrectEndpointWhenOptedIn()
    {
        TestHttpMessageHandler handler = new(_ => TestHttpMessageHandler.JsonOk(new { status = "ok" }));
        using HttpClient httpClient = new(handler) { Timeout = TimeSpan.FromSeconds(5) };
        BackendConfiguration config = CreateBackendConfiguration(
            autoBundleEnabled: true,
            autoBundleAllowedHosts: ["localhost"]);
        using SupportGateway gateway = new(config, httpClient);

        BackendCallResult result = await gateway.TriggerAutoBundleAsync(
            "T-005",
            "http://localhost:8080",
            "admin-key");

        Assert.True(result.IsSuccess);
        CapturedRequest captured = Assert.Single(handler.CapturedRequests);
        Assert.Equal("POST", captured.Method);
        Assert.Contains("/api/v1/tickets/T-005/auto-bundle", captured.Uri, StringComparison.Ordinal);
        using JsonDocument requestJson = JsonDocument.Parse(captured.Body!);
        Assert.Equal("http://localhost:8080", requestJson.RootElement.GetProperty("instanceUrl").GetString());
        Assert.Equal("admin-key", requestJson.RootElement.GetProperty("apiKey").GetString());
    }

    [Fact]
    public async Task TriggerAutoBundleAsync_RefusesWhenDisabledByDefault()
    {
        TestHttpMessageHandler handler = new(_ => TestHttpMessageHandler.JsonOk(new { status = "ok" }));
        using HttpClient httpClient = new(handler) { Timeout = TimeSpan.FromSeconds(5) };
        using SupportGateway gateway = new(CreateBackendConfiguration(), httpClient);

        BackendCallResult result = await gateway.TriggerAutoBundleAsync(
            "T-005",
            "http://localhost:8080",
            "admin-key");

        Assert.False(result.IsSuccess);
        Assert.Equal("auto-bundle-disabled", result.Detail);
        Assert.Empty(handler.CapturedRequests);
    }

    [Fact]
    public async Task TriggerAutoBundleAsync_RefusesWhenInstanceHostNotAllowed()
    {
        TestHttpMessageHandler handler = new(_ => TestHttpMessageHandler.JsonOk(new { status = "ok" }));
        using HttpClient httpClient = new(handler) { Timeout = TimeSpan.FromSeconds(5) };
        BackendConfiguration config = CreateBackendConfiguration(
            autoBundleEnabled: true,
            autoBundleAllowedHosts: ["customer-a.example.com"]);
        using SupportGateway gateway = new(config, httpClient);

        BackendCallResult result = await gateway.TriggerAutoBundleAsync(
            "T-005",
            "http://attacker.example.com:8080",
            "admin-key");

        Assert.False(result.IsSuccess);
        Assert.Equal("auto-bundle-host-rejected", result.Detail);
        Assert.Empty(handler.CapturedRequests);
    }

    [Fact]
    public async Task TriggerAutoBundleAsync_RefusesWhenAllowedHostsEmpty()
    {
        TestHttpMessageHandler handler = new(_ => TestHttpMessageHandler.JsonOk(new { status = "ok" }));
        using HttpClient httpClient = new(handler) { Timeout = TimeSpan.FromSeconds(5) };
        BackendConfiguration config = CreateBackendConfiguration(autoBundleEnabled: true);
        using SupportGateway gateway = new(config, httpClient);

        BackendCallResult result = await gateway.TriggerAutoBundleAsync(
            "T-005",
            "http://localhost:8080",
            "admin-key");

        Assert.False(result.IsSuccess);
        Assert.Equal("auto-bundle-host-rejected", result.Detail);
    }

    [Fact]
    public async Task CloseTicketAsync_CallsCorrectEndpointWithPayload()
    {
        TestHttpMessageHandler handler = new(_ => TestHttpMessageHandler.JsonOk(new { status = "ok" }));
        using HttpClient httpClient = new(handler) { Timeout = TimeSpan.FromSeconds(5) };
        using SupportGateway gateway = new(CreateBackendConfiguration(), httpClient);

        BackendCallResult result = await gateway.CloseTicketAsync("T-010", "Issue resolved after cache flush.");

        Assert.True(result.IsSuccess);
        CapturedRequest captured = Assert.Single(handler.CapturedRequests);
        Assert.Equal("POST", captured.Method);
        Assert.Contains("/api/v1/tickets/T-010/close", captured.Uri, StringComparison.Ordinal);
        Assert.NotNull(captured.Body);
        using JsonDocument doc = JsonDocument.Parse(captured.Body!);
        Assert.Equal("Issue resolved after cache flush.", doc.RootElement.GetProperty("resolutionSummary").GetString());
    }

    [Fact]
    public async Task ListPendingTicketsAsync_ReturnsDisabledWhenSupportApiBaseUriIsNull()
    {
        BackendConfiguration config = CreateBackendConfiguration(disableSupport: true);
        using SupportGateway gateway = new(config);

        BackendCallResult result = await gateway.ListPendingTicketsAsync();

        Assert.False(result.IsSuccess);
        Assert.Equal("support-api-disabled", result.Detail);
        Assert.Contains("HONUA_DEVOPS_SUPPORT_API_BASE_URL", result.PayloadPreview, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PostDiagnosisAsync_ReturnsDisabledWhenSupportApiBaseUriIsNull()
    {
        BackendConfiguration config = CreateBackendConfiguration(disableSupport: true);
        using SupportGateway gateway = new(config);

        GuidedFixResult diagnosis = new(
            Mode: GuidedFixMode.ReadOnlyTriage,
            DiagnosisSummary: "test",
            Confidence: "low",
            MissingEvidence: [],
            RecommendedNextAction: "observe-only",
            GuidedCommands: [],
            ValidationSteps: [],
            Escalation: null);

        BackendCallResult result = await gateway.PostDiagnosisAsync("T-001", diagnosis);

        Assert.False(result.IsSuccess);
        Assert.Equal("support-api-disabled", result.Detail);
    }

    [Fact]
    public async Task GetTicketAsync_ReturnsDisabledWhenSupportApiBaseUriIsNull()
    {
        BackendConfiguration config = CreateBackendConfiguration(disableSupport: true);
        using SupportGateway gateway = new(config);

        BackendCallResult result = await gateway.GetTicketAsync("T-001");

        Assert.False(result.IsSuccess);
        Assert.Equal("support-api-disabled", result.Detail);
    }

    [Fact]
    public async Task TriggerAutoBundleAsync_ReturnsDisabledWhenSupportApiBaseUriIsNull()
    {
        BackendConfiguration config = CreateBackendConfiguration(disableSupport: true);
        using SupportGateway gateway = new(config);

        BackendCallResult result = await gateway.TriggerAutoBundleAsync("T-001");

        Assert.False(result.IsSuccess);
        Assert.Equal("support-api-disabled", result.Detail);
    }

    [Fact]
    public async Task CloseTicketAsync_ReturnsDisabledWhenSupportApiBaseUriIsNull()
    {
        BackendConfiguration config = CreateBackendConfiguration(disableSupport: true);
        using SupportGateway gateway = new(config);

        BackendCallResult result = await gateway.CloseTicketAsync("T-001", "done");

        Assert.False(result.IsSuccess);
        Assert.Equal("support-api-disabled", result.Detail);
    }

    [Fact]
    public async Task ProcessPendingTicketsAsync_TriagesAndPostsDiagnosisForEachTicket()
    {
        object[] ticketList =
        [
            new
            {
                id = "T-100",
                phase = "intake",
                severity = "high",
                environment = "prod",
                service = "roads-api",
                symptoms = "connection pool exhaustion",
                requestedAction = "diagnose",
                allowedAccessMode = "read-only",
                ttlMinutes = 60,
                rollbackExpected = false,
                instanceUrl = "http://localhost:8080"
            },
            new
            {
                id = "T-101",
                phase = "triaging",
                severity = "medium",
                environment = "dev",
                service = "maps-api",
                symptoms = "slow response times",
                requestedAction = "diagnose",
                allowedAccessMode = "guided-fix",
                ttlMinutes = 30,
                rollbackExpected = false
            },
            new
            {
                id = "T-102",
                phase = "diagnosed",
                severity = "low",
                environment = "staging",
                service = "tiles-api",
                symptoms = "none",
                requestedAction = "diagnose",
                allowedAccessMode = "read-only",
                ttlMinutes = 60,
                rollbackExpected = false
            }
        ];

        // Support gateway handler: list returns all tickets, diagnosis posts return ok
        TestHttpMessageHandler supportHandler = new(request =>
        {
            if (request.Method == HttpMethod.Get)
                return TestHttpMessageHandler.JsonOk(ticketList);
            return TestHttpMessageHandler.JsonOk(new { status = "ok" });
        });
        using HttpClient supportHttpClient = new(supportHandler) { Timeout = TimeSpan.FromSeconds(5) };
        BackendConfiguration supportConfig = CreateBackendConfiguration(
            autoBundleEnabled: true,
            autoBundleAllowedHosts: ["localhost"],
            autoBundleApiKey: "dedicated-bundle-key");
        using SupportGateway supportGw = new(supportConfig, supportHttpClient);

        // Backend gateway handler: troubleshoot returns ok
        TestHttpMessageHandler backendHandler = new(_ => TestHttpMessageHandler.JsonOk(new { status = "ok" }));
        using HttpClient backendHttpClient = new(backendHandler) { Timeout = TimeSpan.FromSeconds(5) };
        using BackendGateway backendGw = new(CreateBackendConfiguration(), backendHttpClient);

        OperationRuntime runtime = CreateRuntime();
        HonuaOperationsToolkit toolkit = new(runtime, backendGw, supportGateway: supportGw);

        OperationResponse response = await toolkit.ProcessPendingTicketsAsync();

        // T-102 is "diagnosed" so should be filtered out. Only T-100 and T-101 are pending.
        Assert.Equal("tickets-processed", response.Status);
        Assert.Contains("2", response.Summary, StringComparison.Ordinal);
        Assert.Equal(2, response.Findings.Count);
        Assert.Contains(response.Findings, finding => finding.Contains("T-100", StringComparison.Ordinal));
        Assert.Contains(response.Findings, finding => finding.Contains("T-101", StringComparison.Ordinal));
        Assert.DoesNotContain(response.Findings, finding => finding.Contains("T-102", StringComparison.Ordinal));

        // Verify diagnosis was posted for T-100 and T-101, and auto-bundle was requested for the real Honua URL.
        List<CapturedRequest> diagnosisPosts = supportHandler.CapturedRequests
            .Where(r => r.Method == "POST" && r.Uri.Contains("/diagnosis", StringComparison.Ordinal))
            .ToList();
        Assert.Equal(2, diagnosisPosts.Count);
        Assert.Contains(diagnosisPosts, r => r.Uri.Contains("/T-100/diagnosis", StringComparison.Ordinal));
        Assert.Contains(diagnosisPosts, r => r.Uri.Contains("/T-101/diagnosis", StringComparison.Ordinal));

        CapturedRequest autoBundlePost = Assert.Single(
            supportHandler.CapturedRequests,
            r => r.Method == "POST" && r.Uri.Contains("/T-100/auto-bundle", StringComparison.Ordinal));
        using JsonDocument autoBundleJson = JsonDocument.Parse(autoBundlePost.Body!);
        Assert.Equal("http://localhost:8080", autoBundleJson.RootElement.GetProperty("instanceUrl").GetString());
        Assert.Equal("dedicated-bundle-key", autoBundleJson.RootElement.GetProperty("apiKey").GetString());

        CapturedRequest firstDiagnosisPost = Assert.Single(
            diagnosisPosts,
            r => r.Uri.Contains("/T-100/diagnosis", StringComparison.Ordinal));
        using JsonDocument diagnosisDoc = JsonDocument.Parse(firstDiagnosisPost.Body!);
        Assert.Equal("support-triage:roads-api:T-100", diagnosisDoc.RootElement.GetProperty("evidence").GetProperty("scope").GetString());

        Assert.Contains(backendHandler.CapturedRequests, r =>
            r.Uri.Contains("service=roads-api", StringComparison.Ordinal) &&
            r.Uri.Contains("environment=prod", StringComparison.Ordinal));
        Assert.True(diagnosisDoc.RootElement.GetProperty("diagnosisScorecard").TryGetProperty("scenarioId", out _));
    }

    [Fact]
    public async Task ProcessPendingTicketsAsync_ReturnsDisabledWhenNoSupportGateway()
    {
        TestHttpMessageHandler backendHandler = new(_ => TestHttpMessageHandler.JsonOk(new { status = "ok" }));
        using HttpClient backendHttpClient = new(backendHandler) { Timeout = TimeSpan.FromSeconds(5) };
        using BackendGateway backendGw = new(CreateBackendConfiguration(), backendHttpClient);

        OperationRuntime runtime = CreateRuntime();
        HonuaOperationsToolkit toolkit = new(runtime, backendGw, supportGateway: null);

        OperationResponse response = await toolkit.ProcessPendingTicketsAsync();

        Assert.Equal("support-api-disabled", response.Status);
        Assert.Contains("not configured", response.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProcessPendingTicketsAsync_ReturnsNoPendingWhenAllTicketsAreAlreadyProcessed()
    {
        object[] ticketList =
        [
            new { id = "T-200", phase = "diagnosed", severity = "low", environment = "dev", service = "api", symptoms = "none" },
            new { id = "T-201", phase = "resolved", severity = "low", environment = "dev", service = "api", symptoms = "none" }
        ];

        TestHttpMessageHandler supportHandler = new(_ => TestHttpMessageHandler.JsonOk(ticketList));
        using HttpClient supportHttpClient = new(supportHandler) { Timeout = TimeSpan.FromSeconds(5) };
        using SupportGateway supportGw = new(CreateBackendConfiguration(), supportHttpClient);

        TestHttpMessageHandler backendHandler = new(_ => TestHttpMessageHandler.JsonOk(new { status = "ok" }));
        using HttpClient backendHttpClient = new(backendHandler) { Timeout = TimeSpan.FromSeconds(5) };
        using BackendGateway backendGw = new(CreateBackendConfiguration(), backendHttpClient);

        HonuaOperationsToolkit toolkit = new(CreateRuntime(), backendGw, supportGateway: supportGw);

        OperationResponse response = await toolkit.ProcessPendingTicketsAsync();

        Assert.Equal("no-pending-tickets", response.Status);
    }

    private static OperationRuntime CreateRuntime(
        ExecutionMode mode = ExecutionMode.Plan,
        ExecutionTier executionTier = ExecutionTier.Plan)
    {
        return new OperationRuntime(
            mode,
            executionTier,
            "honua-gitops",
            AllowedEnvironments: ["dev", "staging", "prod"],
            TerraformRepository: "https://github.com/honua-io/honua-terraform",
            TerraformRef: "main",
            TerraformLocalPath: "/tmp/honua-terraform",
            TerraformDeploymentTargets: ["eks", "aks"]);
    }

    private static BackendConfiguration CreateBackendConfiguration(
        bool disableSupport = false,
        string? supportApiBearerToken = null,
        bool autoBundleEnabled = false,
        IReadOnlyList<string>? autoBundleAllowedHosts = null,
        string? autoBundleApiKey = null)
    {
        return new BackendConfiguration(
            HonuaApiBaseUri: new Uri("http://localhost:8080"),
            OTelBaseUri: new Uri("http://localhost:4318"),
            HonuaApiKey: null,
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
            RequestTimeout: TimeSpan.FromSeconds(5),
            SupportApiBaseUri: disableSupport ? null : new Uri("http://localhost:5100"),
            SupportApiTicketsPath: "api/v1/tickets",
            SupportApiBearerToken: supportApiBearerToken,
            SupportAutoBundleEnabled: autoBundleEnabled,
            SupportAutoBundleAllowedHosts: autoBundleAllowedHosts,
            SupportAutoBundleApiKey: autoBundleApiKey);
    }
}
