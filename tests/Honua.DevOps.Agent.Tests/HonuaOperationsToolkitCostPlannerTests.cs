using System.Text.Json;
using Honua.DevOps.Agent.Operations;

namespace Honua.DevOps.Agent.Tests;

public sealed class HonuaOperationsToolkitCostPlannerTests
{
    [Fact]
    public async Task PlanCostOptimizationAsync_CarriesTypedPlanAndStaysReadOnly()
    {
        TestHttpMessageHandler handler = new(_ => TestHttpMessageHandler.JsonOk(new { status = "ok" }));
        using HttpClient httpClient = new(handler)
        {
            Timeout = TimeSpan.FromSeconds(5)
        };
        using BackendGateway gateway = new(CreateBackendConfiguration(), httpClient);
        HonuaOperationsToolkit toolkit = new(CreateRuntime(), gateway);

        OperationResponse response = await toolkit.PlanCostOptimizationAsync(
            workloadName: "tile-renderer",
            targetsCsv: "lambda,eks,aca",
            vCpu: 2,
            memoryGib: 4,
            requestsPerSecond: 12,
            avgRequestMillis: 90,
            dutyCycle: 0.2,
            minReplicas: 3,
            requiresPersistentState: false,
            latencySensitiveSustained: false,
            metricsSource: "OTEL p50 over 7d",
            cancellationToken: CancellationToken.None);

        Assert.Equal("cost-plan-ready", response.Status);
        Assert.NotNull(response.CostOptimization);
        Assert.Equal("OTEL p50 over 7d", response.CostOptimization!.MetricsProvenance);
        Assert.NotEmpty(response.CostOptimization.Assumptions);
        Assert.Equal(3, response.CostOptimization.Estimates.Count);
        Assert.False(string.IsNullOrWhiteSpace(response.CostOptimization.RecommendedTarget));

        // Pure planner: no backend calls should have been made.
        Assert.Empty(handler.CapturedRequests);
        Assert.Null(response.BackendSteps);

        // The typed plan must be JsonIgnore'd from the LLM-facing wire shape.
        string wire = JsonSerializer.Serialize(response);
        Assert.DoesNotContain("RecommendedTarget", wire, StringComparison.Ordinal);
        Assert.DoesNotContain("EstimatedMonthlyUsd", wire, StringComparison.Ordinal);

        // ...but the human/LLM-readable findings still surface the comparison and assumptions.
        Assert.Contains(response.ValidationChecks, check => check.StartsWith("assumption:", StringComparison.Ordinal));
        Assert.Contains(response.Findings, finding => finding.Contains("/mo", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PlanCostOptimizationAsync_DefaultsToConfiguredDeploymentTargets()
    {
        using BackendGateway gateway = CreateGateway(_ => TestHttpMessageHandler.JsonOk(new { status = "ok" }));
        HonuaOperationsToolkit toolkit = new(CreateRuntime(), gateway);

        OperationResponse response = await toolkit.PlanCostOptimizationAsync(
            workloadName: "tile-renderer",
            targetsCsv: "",
            vCpu: 1,
            memoryGib: 2,
            requestsPerSecond: 50,
            avgRequestMillis: 60,
            dutyCycle: 0.9,
            minReplicas: 2,
            requiresPersistentState: false,
            latencySensitiveSustained: false,
            metricsSource: "",
            cancellationToken: CancellationToken.None);

        // CreateRuntime configures eks + aks as the deployment targets.
        Assert.NotNull(response.CostOptimization);
        Assert.Equal(2, response.CostOptimization!.Estimates.Count);
        Assert.All(
            response.CostOptimization.Estimates,
            estimate => Assert.Contains(estimate.Target, new[] { "eks", "aks" }));
        Assert.Contains("operator-described", response.CostOptimization.MetricsProvenance, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PlanCostOptimizationAsync_RejectsUnsafeWorkloadName()
    {
        using BackendGateway gateway = CreateGateway(_ => TestHttpMessageHandler.JsonOk(new { status = "ok" }));
        HonuaOperationsToolkit toolkit = new(CreateRuntime(), gateway);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => toolkit.PlanCostOptimizationAsync(
                workloadName: "tile;drop",
                targetsCsv: "eks",
                vCpu: 1,
                memoryGib: 2,
                requestsPerSecond: 10,
                avgRequestMillis: 50,
                dutyCycle: 0.5,
                minReplicas: 1,
                requiresPersistentState: false,
                latencySensitiveSustained: false,
                metricsSource: "test",
                cancellationToken: CancellationToken.None));
    }

    private static OperationRuntime CreateRuntime()
    {
        return new OperationRuntime(
            ExecutionMode.Plan,
            ExecutionTier.Plan,
            "honua-gitops",
            AllowedEnvironments: ["dev", "staging", "prod"],
            TerraformRepository: "https://github.com/honua-io/honua-terraform",
            TerraformRef: "main",
            TerraformLocalPath: "/tmp/honua-terraform",
            TerraformDeploymentTargets: ["eks", "aks"],
            DeployTargetId: null);
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
