using Honua.DevOps.Agent.Operations;
using Honua.DevOps.Agent.Operations.GuidedFix;
using Honua.DevOps.Agent.Operations.OperatorPolicy;
using Honua.DevOps.Agent.Operations.Triage;
using OperatorPolicyModel = Honua.DevOps.Agent.Operations.OperatorPolicy.OperatorPolicy;

namespace Honua.DevOps.Agent.Tests;

public class SupportTriagePlanTests
{
    [Fact]
    public async Task TriagePendingTicketsAsync_ClassifiesPrioritizesAndEmitsTypedPlan()
    {
        // Fixture tickets: two pending (intake/triaging) with diagnosable evidence,
        // one already diagnosed (must be filtered out of the plan).
        object[] ticketList =
        [
            new
            {
                id = "T-300",
                phase = "triaging",
                severity = "medium",
                environment = "dev",
                service = "tiles-api",
                symptoms = "Cache misses spiking, elevated latency on tile endpoints",
                attachedEvidence = "StackExchange.Redis.RedisConnectionException: NOAUTH Authentication required"
            },
            new
            {
                id = "T-301",
                phase = "intake",
                severity = "critical",
                environment = "prod",
                service = "roads-api",
                symptoms = "Service returning 500 errors after secret rotation",
                attachedEvidence = "FATAL: password authentication failed for user honua. Npgsql.PostgresException. connection refused"
            },
            new
            {
                id = "T-302",
                phase = "diagnosed",
                severity = "high",
                environment = "staging",
                service = "maps-api",
                symptoms = "already handled",
                attachedEvidence = ""
            }
        ];

        OperationResponse response = await RunTriageAsync(ticketList);

        Assert.Equal("triage-plan-ready", response.Status);

        SupportTriagePlan plan = Assert.IsType<SupportTriagePlan>(response.SupportTriage);
        Assert.Equal(3, plan.TotalTickets);
        Assert.Equal(2, plan.PendingTickets);
        Assert.Equal(2, plan.ClassifiedTickets);
        Assert.Equal(2, plan.Triages.Count);
        Assert.DoesNotContain(plan.Triages, t => t.TicketId == "T-302");

        // Critical/prod ticket must be ranked first by priority score.
        SupportTicketTriage first = plan.Triages[0];
        Assert.Equal("T-301", first.TicketId);
        Assert.Equal(SupportSeverity.Critical, first.Severity);
        Assert.True(first.PriorityScore > plan.Triages[1].PriorityScore);

        // Classification reuses the fault catalog.
        Assert.Equal("secret-credential", first.Category);
        Assert.Equal("FAULT-001", first.MatchedScenarioId);
        Assert.Equal("high", first.Confidence);
        Assert.NotEmpty(first.SuggestedRunbookSteps);
        Assert.Contains(first.SuggestedRunbookSteps, step => step.Contains("secret", StringComparison.OrdinalIgnoreCase));
        Assert.NotEqual("capture current revision before any change", first.RollbackPath);

        SupportTicketTriage second = plan.Triages[1];
        Assert.Equal("T-300", second.TicketId);
        Assert.Equal("redis-connectivity", second.Category);

        Assert.Contains(response.Findings, f => f.Contains("T-301", StringComparison.Ordinal));
        Assert.Contains(response.Findings, f => f.Contains("read-only", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task TriagePendingTicketsAsync_TakesNoWriteAction()
    {
        object[] ticketList =
        [
            new
            {
                id = "T-400",
                phase = "intake",
                severity = "critical",
                environment = "prod",
                service = "roads-api",
                symptoms = "DB unreachable",
                allowedAccessMode = "operator-scoped",
                attachedEvidence = "FATAL: password authentication failed. Npgsql.PostgresException. connection refused"
            }
        ];

        List<CapturedRequest> supportRequests = [];
        TestHttpMessageHandler supportHandler = new(request =>
        {
            return TestHttpMessageHandler.JsonOk(ticketList);
        });
        using HttpClient supportHttpClient = new(supportHandler) { Timeout = TimeSpan.FromSeconds(5) };
        // Approval mode that WOULD allow operator-scoped escalation if this were
        // an execution path -- proves triage stays read-only regardless of policy.
        using SupportGateway supportGw = new(CreateBackendConfiguration(), supportHttpClient);

        TestHttpMessageHandler backendHandler = new(_ => TestHttpMessageHandler.JsonOk(new { status = "ok" }));
        using HttpClient backendHttpClient = new(backendHandler) { Timeout = TimeSpan.FromSeconds(5) };
        using BackendGateway backendGw = new(CreateBackendConfiguration(), backendHttpClient);

        OperatorPolicyModel directAllowed = new(
            ApprovalMode.DirectAllowed,
            "stdout-evidence",
            new SupportSessionPolicy(SupportSessionAccess.OperatorScoped, 60, true),
            BreakGlassPostActionReviewRequired: true);
        HonuaOperationsToolkit toolkit = new(
            CreateRuntime(ExecutionMode.Execute, ExecutionTier.BreakGlass),
            backendGw,
            directAllowed,
            supportGateway: supportGw);

        OperationResponse response = await toolkit.TriagePendingTicketsAsync();

        Assert.Equal("triage-plan-ready", response.Status);

        // No POST to the support API (no diagnosis post, no auto-bundle, no close).
        Assert.DoesNotContain(supportHandler.CapturedRequests, r => r.Method == "POST");
        // The only support call is the GET list. No backend troubleshoot calls either.
        Assert.All(supportHandler.CapturedRequests, r => Assert.Equal("GET", r.Method));
        Assert.Empty(backendHandler.CapturedRequests);

        // Suggested action remains a planning recommendation, never "execute-remediation".
        SupportTriagePlan plan = Assert.IsType<SupportTriagePlan>(response.SupportTriage);
        SupportTicketTriage triage = Assert.Single(plan.Triages);
        Assert.NotEqual("execute-remediation", triage.SuggestedAction);

        // BackendSteps record the single read-only list call.
        Assert.NotNull(response.BackendSteps);
        Assert.All(response.BackendSteps!, step => Assert.False(step.MutatesState));
    }

    [Fact]
    public async Task TriagePendingTicketsAsync_ReturnsNoPendingWhenAllProcessed()
    {
        object[] ticketList =
        [
            new { id = "T-500", phase = "diagnosed", severity = "low", environment = "dev", service = "api", symptoms = "none" },
            new { id = "T-501", phase = "resolved", severity = "low", environment = "dev", service = "api", symptoms = "none" }
        ];

        OperationResponse response = await RunTriageAsync(ticketList);

        Assert.Equal("no-pending-tickets", response.Status);
        SupportTriagePlan plan = Assert.IsType<SupportTriagePlan>(response.SupportTriage);
        Assert.Equal(2, plan.TotalTickets);
        Assert.Equal(0, plan.PendingTickets);
        Assert.Empty(plan.Triages);
    }

    [Fact]
    public async Task TriagePendingTicketsAsync_ReturnsDisabledWhenNoSupportGateway()
    {
        TestHttpMessageHandler backendHandler = new(_ => TestHttpMessageHandler.JsonOk(new { status = "ok" }));
        using HttpClient backendHttpClient = new(backendHandler) { Timeout = TimeSpan.FromSeconds(5) };
        using BackendGateway backendGw = new(CreateBackendConfiguration(), backendHttpClient);

        HonuaOperationsToolkit toolkit = new(CreateRuntime(), backendGw, supportGateway: null);

        OperationResponse response = await toolkit.TriagePendingTicketsAsync();

        Assert.Equal("support-api-disabled", response.Status);
        Assert.Null(response.SupportTriage);
    }

    [Fact]
    public async Task TriagePendingTicketsAsync_LowConfidenceWhenEvidenceMissing()
    {
        object[] ticketList =
        [
            new
            {
                id = "T-600",
                phase = "intake",
                severity = "low",
                environment = "dev",
                service = "billing",
                symptoms = "Billing inquiry"
            }
        ];

        OperationResponse response = await RunTriageAsync(ticketList);

        SupportTriagePlan plan = Assert.IsType<SupportTriagePlan>(response.SupportTriage);
        SupportTicketTriage triage = Assert.Single(plan.Triages);
        Assert.Equal("unclassified", triage.Category);
        Assert.Null(triage.MatchedScenarioId);
        Assert.Equal("low", triage.Confidence);
        Assert.NotEmpty(triage.MissingEvidence);
        Assert.Contains(response.Actions, a => a.Contains("Request missing evidence", StringComparison.Ordinal));
    }

    private static async Task<OperationResponse> RunTriageAsync(object[] ticketList)
    {
        TestHttpMessageHandler supportHandler = new(_ => TestHttpMessageHandler.JsonOk(ticketList));
        using HttpClient supportHttpClient = new(supportHandler) { Timeout = TimeSpan.FromSeconds(5) };
        using SupportGateway supportGw = new(CreateBackendConfiguration(), supportHttpClient);

        TestHttpMessageHandler backendHandler = new(_ => TestHttpMessageHandler.JsonOk(new { status = "ok" }));
        using HttpClient backendHttpClient = new(backendHandler) { Timeout = TimeSpan.FromSeconds(5) };
        using BackendGateway backendGw = new(CreateBackendConfiguration(), backendHttpClient);

        HonuaOperationsToolkit toolkit = new(CreateRuntime(), backendGw, supportGateway: supportGw);
        return await toolkit.TriagePendingTicketsAsync();
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

    private static BackendConfiguration CreateBackendConfiguration()
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
            SupportApiBaseUri: new Uri("http://localhost:5100"),
            SupportApiTicketsPath: "api/v1/tickets");
    }
}
