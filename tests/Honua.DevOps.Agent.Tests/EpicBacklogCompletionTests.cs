using Honua.DevOps.Agent.Operations;
using Honua.DevOps.Agent.Operations.OperatorPolicy;
using OperatorPolicyModel = Honua.DevOps.Agent.Operations.OperatorPolicy.OperatorPolicy;

namespace Honua.DevOps.Agent.Tests;

public class EpicBacklogCompletionTests
{
    [Fact]
    public async Task PlanGitOpsPlatformAsync_CoversRepoWatchingPromotionDriftCiRollbackAndAudit()
    {
        HonuaOperationsToolkit toolkit = new(CreateRuntime(), CreateGateway());

        OperationResponse response = await toolkit.PlanGitOpsPlatformAsync(
            configRepository: "https://github.com/honua-io/customer-config",
            branch: "main",
            service: "roads-api",
            environmentsCsv: "dev,staging,prod",
            syncMode: "hybrid",
            alertTargetsCsv: "slack,email",
            commitSha: "abc123");

        Assert.Equal("gitops-platform-ready", response.Status);
        Assert.Contains(response.Findings, finding => finding.Contains("Repository watcher: hybrid", StringComparison.Ordinal));
        Assert.Contains(response.Findings, finding => finding.Contains("GitHub Actions and GitLab templates", StringComparison.Ordinal));
        Assert.Contains(response.Actions, action => action.Contains("honua apply -f desired-state --dry-run", StringComparison.Ordinal));
        Assert.Contains(response.Actions, action => action.Contains("honua rollback --to", StringComparison.Ordinal));
        Assert.Contains(response.ValidationChecks, check => check.Contains("Commit SHA is recorded", StringComparison.Ordinal));
        Assert.NotNull(response.Evidence);
        Assert.Equal("gitops-platform:roads-api", response.Evidence!.Scope);
    }

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
    public async Task AiDevOpsProTools_ReturnPlansAndGateCommunity()
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

        OperationResponse indexPlan = await toolkit.RecommendIndexesAsync(
            service: "roads-api",
            layer: "roads",
            queryPattern: "where tenant_id = ? and ST_Intersects(geometry, bbox)",
            currentIndexes: "primary key only",
            edition: "pro");
        Assert.Equal("index-plan-ready", indexPlan.Status);
        Assert.Contains(indexPlan.Actions, action => action.Contains("spatial index", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task CapacityAndMigrationAdvisors_ModelGaPlanningScope()
    {
        HonuaOperationsToolkit toolkit = new(CreateRuntime(), CreateGateway());

        OperationResponse forecast = await toolkit.CapacityForecastAsync(
            service: "roads-api",
            environment: "prod",
            metricWindow: "30d",
            currentDailyRequests: 1_000_000,
            growthRatePercent: 3,
            currentNodes: 2,
            cpuUtilizationPercent: 76,
            memoryUtilizationPercent: 68,
            edition: "pro");

        Assert.Equal("capacity-forecast-ready", forecast.Status);
        Assert.Contains(forecast.Findings, finding => finding.Contains("recommended nodes: 3", StringComparison.Ordinal));

        OperationResponse migration = await toolkit.MigrationAdvisorAsync(
            sourcePlatform: "ArcGIS Enterprise",
            serviceInventory: "24 map services, 3 geoprocessing services, custom extension",
            dataVolumeSummary: "4 TB feature data",
            protocolRequirements: "FeatureServer, MapServer, OGC API Features",
            migrationConstraints: "zero downtime",
            edition: "pro");

        Assert.Equal("migration-plan-ready", migration.Status);
        Assert.Contains(migration.Findings, finding => finding.Contains("Risk band: elevated", StringComparison.Ordinal));
        Assert.Contains(migration.Actions, action => action.Contains("completion percentage", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EnterpriseToolsRespectExecutionAndApprovalGates()
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

        OperationResponse incident = await toolkit.IncidentSummaryAsync(
            service: "roads-api",
            environment: "prod",
            timeRange: "10:00-10:45Z",
            timelineEvents: "10:00 alert; 10:15 mitigated; 10:45 recovered",
            affectedServices: "roads-api,tiles-api",
            edition: "enterprise");
        Assert.Equal("incident-summary-ready", incident.Status);

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
