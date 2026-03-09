using Honua.DevOps.Agent.Operations;
using Honua.DevOps.Agent.Operations.ReleaseOrchestration;

namespace Honua.DevOps.Agent.Tests;

public sealed class HonuaOperationsToolkitUpgradeTests
{
    [Fact]
    public async Task PlanServerUpgradeAsync_EmitsReleaseOrchestrationPlan()
    {
        using BackendGateway gateway = CreateGateway(_ => TestHttpMessageHandler.JsonOk(new { status = "ok" }));
        HonuaOperationsToolkit toolkit = new(
            CreateRuntime(targets: ["lambda", "eks"]),
            gateway);

        OperationResponse response = await toolkit.PlanServerUpgradeAsync(
            environment: "staging",
            currentVersion: "2026.02",
            targetVersion: "2026.03",
            maintenanceWindow: "saturday-0200",
            constraints: "no downtime",
            cancellationToken: CancellationToken.None);

        Assert.NotNull(response.ReleaseOrchestration);
        Assert.Equal("out-of-band-migration", response.ReleaseOrchestration!.MigrationMode);
        Assert.Equal("single-environment-auto-advance", response.ReleaseOrchestration.PromotionPolicy.Gate);
        Assert.Contains(response.ReleaseOrchestration.RollbackClasses, item => item == "schema");
        Assert.Contains(response.ReleaseOrchestration.RollbackPolicy.Triggers, trigger => trigger == "failed-slo-gate");
        Assert.Contains(response.ReleaseOrchestration.Stages, stage => stage.Kind == ReleaseStageKind.SloWatch);
        Assert.Contains(response.ReleaseOrchestration.Stages, stage => stage.Kind == ReleaseStageKind.Rollback);
        Assert.Contains(
            response.ReleaseOrchestration.Stages.Single(stage => stage.Kind == ReleaseStageKind.SloWatch).SuggestedCommands,
            command => command.Contains("slo-release-watch.sh", StringComparison.Ordinal));
        Assert.Contains(response.Evidence!.RequiredChecks, item => item == "slo-gate-evidence");
    }

    private static OperationRuntime CreateRuntime(
        string gitOpsTool = "honua-gitops",
        ExecutionMode mode = ExecutionMode.Plan,
        ExecutionTier executionTier = ExecutionTier.Plan,
        string[]? targets = null)
    {
        return new OperationRuntime(
            mode,
            executionTier,
            gitOpsTool,
            AllowedEnvironments: ["dev", "staging", "prod"],
            TerraformRepository: "https://github.com/honua-io/honua-terraform",
            TerraformRef: "main",
            TerraformLocalPath: "/tmp/honua-terraform",
            TerraformDeploymentTargets: targets ?? ["eks", "aks"]);
    }

    private static BackendGateway CreateGateway(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        TestHttpMessageHandler handler = new(responder);
        HttpClient httpClient = new(handler)
        {
            Timeout = TimeSpan.FromSeconds(5)
        };

        return new BackendGateway(CreateBackendConfiguration(), httpClient);
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
            RequestTimeout: TimeSpan.FromSeconds(5));
    }
}
