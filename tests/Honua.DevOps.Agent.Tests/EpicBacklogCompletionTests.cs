using Honua.DevOps.Agent.Operations;
using Honua.DevOps.Agent.Operations.OperatorPolicy;
using OperatorPolicyModel = Honua.DevOps.Agent.Operations.OperatorPolicy.OperatorPolicy;

namespace Honua.DevOps.Agent.Tests;

public class EpicBacklogCompletionTests
{
    [Fact]
    public async Task HonuaDiagnoseAsync_IsCommunityReadOnlyAndScopesBackendRequests()
    {
        TestHttpMessageHandler handler = new(_ => TestHttpMessageHandler.JsonOk(new { status = "ok" }));
        using HttpClient httpClient = new(handler) { Timeout = TimeSpan.FromSeconds(5) };
        using BackendGateway gateway = CreateGateway(httpClient);
        HonuaOperationsToolkit toolkit = new(CreateRuntime(), gateway);

        OperationResponse response = await toolkit.HonuaDiagnoseAsync(
            service: "roads-api",
            environment: "prod",
            timeframe: "last-15m",
            symptoms: "timeouts",
            edition: "community");

        Assert.Equal("diagnosis-ready", response.Status);
        Assert.Contains(response.Findings, finding => finding.Contains("Community edition", StringComparison.Ordinal));
        Assert.Equal(3, handler.CapturedRequests.Count);
        Assert.All(handler.CapturedRequests, request =>
        {
            Assert.Contains("service=roads-api", request.Uri, StringComparison.Ordinal);
            Assert.Contains("environment=prod", request.Uri, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task ExplainSlowQueriesGatesCommunityAndReturnsAnalysisForPro()
    {
        TestHttpMessageHandler handler = new(_ => TestHttpMessageHandler.JsonOk(new { status = "ok" }));
        using HttpClient httpClient = new(handler) { Timeout = TimeSpan.FromSeconds(5) };
        using BackendGateway gateway = CreateGateway(httpClient);
        HonuaOperationsToolkit toolkit = new(CreateRuntime(), gateway);

        OperationResponse gated = await toolkit.ExplainSlowQueriesAsync(
            service: "roads-api",
            environment: "prod",
            timeframe: "last-hour",
            slowQuerySample: "Seq Scan with ST_Intersects and cache miss",
            edition: "community");
        Assert.Equal("edition-gated", gated.Status);

        OperationResponse slowQuery = await toolkit.ExplainSlowQueriesAsync(
            service: "roads-api",
            environment: "prod",
            timeframe: "last-hour",
            slowQuerySample: "Seq Scan with ST_Intersects and cache miss",
            edition: "pro");
        Assert.Equal("slow-query-explained", slowQuery.Status);
        Assert.Contains(slowQuery.Findings, finding => finding.Contains("Spatial predicate", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EnterpriseRunbookAndAutoRemediationRespectGates()
    {
        OperatorPolicyModel directPolicy = new(
            ApprovalMode.DirectAllowed,
            "stdout-evidence",
            new SupportSessionPolicy(SupportSessionAccess.OperatorScoped, 30, true),
            BreakGlassPostActionReviewRequired: true);
        HonuaOperationsToolkit toolkit = new(
            CreateRuntime(ExecutionMode.Execute, ExecutionTier.ExecuteLowerEnv),
            CreateGateway(),
            directPolicy);

        OperationResponse runbook = await toolkit.RunbookExecuteAsync(
            runbookName: "clear-tile-cache",
            service: "roads-api",
            environment: "staging",
            parameters: "layer=roads",
            confirmed: true,
            edition: "enterprise");
        Assert.Equal("runbook-execute-ready", runbook.Status);
        Assert.NotNull(runbook.Evidence);

        OperationResponse remediation = await toolkit.AutoRemediationPlanAsync(
            service: "roads-api",
            environment: "staging",
            detectedIssue: "cache miss storm",
            desiredOutcome: "restore p95 latency",
            autoApply: true,
            edition: "enterprise");
        Assert.Equal("auto-remediation-ready", remediation.Status);
    }

    private static OperationRuntime CreateRuntime(
        ExecutionMode mode = ExecutionMode.Plan,
        ExecutionTier tier = ExecutionTier.Plan)
    {
        return new OperationRuntime(
            mode,
            tier,
            "honua-gitops",
            AllowedEnvironments: ["dev", "staging", "prod"],
            TerraformRepository: "https://github.com/honua-io/honua-terraform",
            TerraformRef: "main",
            TerraformLocalPath: "/tmp/honua-terraform",
            TerraformDeploymentTargets: ["eks", "aks"]);
    }

    private static BackendGateway CreateGateway(HttpClient? httpClient = null)
    {
        return new BackendGateway(CreateBackendConfiguration(), httpClient);
    }

    private static BackendConfiguration CreateBackendConfiguration()
    {
        return new BackendConfiguration(
            HonuaApiBaseUri: new Uri("http://localhost:8080/base"),
            OTelBaseUri: new Uri("http://localhost:4318/otel"),
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
            RequestTimeout: TimeSpan.FromSeconds(5));
    }
}
