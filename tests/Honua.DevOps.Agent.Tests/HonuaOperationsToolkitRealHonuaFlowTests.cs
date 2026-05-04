using System.Text.Json;
using Honua.DevOps.Agent.Operations;
using Honua.DevOps.Agent.Operations.OperatorPolicy;
using OperatorPolicyModel = Honua.DevOps.Agent.Operations.OperatorPolicy.OperatorPolicy;

namespace Honua.DevOps.Agent.Tests;

public class HonuaOperationsToolkitRealHonuaFlowTests
{
    [Fact]
    public async Task RunbookExecuteAsync_ExecutesDeployPreflightAgainstHonuaAdminContract()
    {
        TestHttpMessageHandler handler = new(_ => TestHttpMessageHandler.JsonOk(new { status = "ok" }));
        using HttpClient httpClient = new(handler) { Timeout = TimeSpan.FromSeconds(5) };
        using BackendGateway gateway = new(CreateBackendConfiguration(), httpClient);
        HonuaOperationsToolkit toolkit = new(CreateRuntime(), gateway);

        OperationResponse response = await toolkit.RunbookExecuteAsync(
            runbookName: "deploy-preflight",
            service: "roads-api",
            environment: "dev",
            parameters: string.Empty,
            confirmed: true,
            edition: "enterprise");

        Assert.Equal("runbook-executed", response.Status);
        CapturedRequest captured = Assert.Single(handler.CapturedRequests);
        Assert.Equal("GET", captured.Method);
        Assert.Contains("/api/v1/admin/deploy/preflight?includeDiagnostics=true", captured.Uri, StringComparison.Ordinal);
        Assert.NotNull(response.BackendSteps);
        OperationBackendStep step = Assert.Single(response.BackendSteps!);
        Assert.Equal("runbook:deploy-preflight", step.Name);
        Assert.False(step.MutatesState);
    }

    [Fact]
    public async Task RunbookExecuteAsync_ExecutesDeployRollbackAgainstHonuaAdminContract()
    {
        TestHttpMessageHandler handler = new(_ => TestHttpMessageHandler.JsonOk(new { status = "ok" }));
        using HttpClient httpClient = new(handler) { Timeout = TimeSpan.FromSeconds(5) };
        using BackendGateway gateway = new(CreateBackendConfiguration(), httpClient);
        HonuaOperationsToolkit toolkit = new(
            CreateRuntime(ExecutionMode.Execute, ExecutionTier.ExecuteLowerEnv),
            gateway,
            DirectAllowedPolicy());

        OperationResponse response = await toolkit.RunbookExecuteAsync(
            runbookName: "deploy-rollback",
            service: "roads-api",
            environment: "dev",
            parameters: "operationId=deploy-123",
            confirmed: true,
            edition: "enterprise");

        Assert.Equal("runbook-executed", response.Status);
        CapturedRequest captured = Assert.Single(handler.CapturedRequests);
        Assert.Equal("POST", captured.Method);
        Assert.Contains("/api/v1/admin/deploy/operations/deploy-123/rollback", captured.Uri, StringComparison.Ordinal);
        using JsonDocument requestJson = JsonDocument.Parse(captured.Body!);
        Assert.Equal("operationId=deploy-123", requestJson.RootElement.GetProperty("reason").GetString());
        Assert.NotNull(response.BackendSteps);
        OperationBackendStep step = Assert.Single(response.BackendSteps!);
        Assert.Equal("runbook:deploy-rollback", step.Name);
        Assert.True(step.MutatesState);
    }

    [Fact]
    public async Task AutoRemediationPlanAsync_RollsBackDeployOperationWhenOperationIdIsProvided()
    {
        TestHttpMessageHandler handler = new(_ => TestHttpMessageHandler.JsonOk(new { status = "ok" }));
        using HttpClient httpClient = new(handler) { Timeout = TimeSpan.FromSeconds(5) };
        using BackendGateway gateway = new(CreateBackendConfiguration(), httpClient);
        HonuaOperationsToolkit toolkit = new(
            CreateRuntime(ExecutionMode.Execute, ExecutionTier.ExecuteLowerEnv),
            gateway,
            DirectAllowedPolicy());

        OperationResponse response = await toolkit.AutoRemediationPlanAsync(
            service: "roads-api",
            environment: "dev",
            detectedIssue: "failed rollout operationId=deploy-123",
            desiredOutcome: "rollback to last healthy revision",
            autoApply: true,
            edition: "enterprise");

        Assert.Equal("auto-remediation-applied", response.Status);
        CapturedRequest captured = Assert.Single(handler.CapturedRequests);
        Assert.Equal("POST", captured.Method);
        Assert.Contains("/api/v1/admin/deploy/operations/deploy-123/rollback", captured.Uri, StringComparison.Ordinal);
        using JsonDocument requestJson = JsonDocument.Parse(captured.Body!);
        Assert.Contains("auto-remediation:roads-api", requestJson.RootElement.GetProperty("reason").GetString(), StringComparison.Ordinal);
        Assert.NotNull(response.BackendSteps);
        OperationBackendStep step = Assert.Single(response.BackendSteps!);
        Assert.Equal("auto-remediation", step.Name);
        Assert.True(step.MutatesState);
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
            RequestTimeout: TimeSpan.FromSeconds(5));
    }

    private static OperatorPolicyModel DirectAllowedPolicy()
    {
        return new OperatorPolicyModel(
            ApprovalMode.DirectAllowed,
            "stdout-evidence",
            new SupportSessionPolicy(SupportSessionAccess.OperatorScoped, 30, true),
            BreakGlassPostActionReviewRequired: true);
    }
}
