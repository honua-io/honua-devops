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

        // Issue #151: a read-only runbook genuinely ran, but it mutated nothing — so it
        // reports `runbook-observed`. `runbook-executed` is reserved for an actuation with a
        // receipt and a successful mutating backend step.
        Assert.Equal("runbook-observed", response.Status);
        CapturedRequest captured = Assert.Single(handler.CapturedRequests);
        Assert.Equal("GET", captured.Method);
        Assert.Contains("/api/v1/admin/deploy/preflight?includeDiagnostics=true", captured.Uri, StringComparison.Ordinal);
        Assert.NotNull(response.BackendSteps);
        OperationBackendStep step = Assert.Single(response.BackendSteps!);
        Assert.Equal("runbook:deploy-preflight", step.Name);
        Assert.False(step.MutatesState);
    }

    [Fact]
    public async Task RunbookExecuteAsync_DeployRollbackDisabled_ReturnsSharedRefusalWithoutBackendCall()
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

        Assert.Equal("experimental-disabled", response.Status);
        Assert.Contains(response.Actions, action => action.Contains("forward", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(handler.CapturedRequests);
    }

    [Fact]
    public async Task RunbookExecuteAsync_ExecutesDeployRollbackAgainstHonuaAdminContract()
    {
        TestHttpMessageHandler handler = new(CreateMetadataOnlyRollbackResponse);
        using HttpClient httpClient = new(handler) { Timeout = TimeSpan.FromSeconds(5) };
        using BackendGateway gateway = new(CreateBackendConfiguration(), httpClient);
        HonuaOperationsToolkit toolkit = new(
            CreateRuntime(ExecutionMode.Execute, ExecutionTier.ExecuteLowerEnv, rollbackEnabled: true),
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
        Assert.Equal(2, handler.CapturedRequests.Count);
        Assert.Equal("GET", handler.CapturedRequests[0].Method);
        CapturedRequest rollbackRequest = handler.CapturedRequests[1];
        Assert.Equal("POST", rollbackRequest.Method);
        Assert.Contains("/api/v1/admin/deploy/operations/deploy-123/rollback", rollbackRequest.Uri, StringComparison.Ordinal);
        using JsonDocument requestJson = JsonDocument.Parse(rollbackRequest.Body!);
        Assert.Equal("operationId=deploy-123", requestJson.RootElement.GetProperty("reason").GetString());
        Assert.NotNull(response.BackendSteps);
        Assert.Collection(
            response.BackendSteps!,
            step =>
            {
                Assert.Equal("deploy-operation-read", step.Name);
                Assert.False(step.MutatesState);
            },
            step =>
            {
                Assert.Equal("deploy-operation-rollback", step.Name);
                Assert.True(step.MutatesState);
            });
    }

    [Fact]
    public async Task RunbookExecuteAsync_RollbackEnabledWithoutConfirmation_DoesNotCallBackend()
    {
        TestHttpMessageHandler handler = new(CreateMetadataOnlyRollbackResponse);
        using HttpClient httpClient = new(handler) { Timeout = TimeSpan.FromSeconds(5) };
        using BackendGateway gateway = new(CreateBackendConfiguration(), httpClient);
        HonuaOperationsToolkit toolkit = new(
            CreateRuntime(ExecutionMode.Execute, ExecutionTier.ExecuteLowerEnv, rollbackEnabled: true),
            gateway,
            DirectAllowedPolicy());

        OperationResponse response = await toolkit.RunbookExecuteAsync(
            runbookName: "deploy-rollback",
            service: "roads-api",
            environment: "dev",
            parameters: "operationId=deploy-123",
            confirmed: false,
            edition: "enterprise");

        Assert.Equal("confirmation-required", response.Status);
        Assert.Empty(handler.CapturedRequests);
    }

    [Fact]
    public async Task RunbookExecuteAsync_RollbackEnabledWithPrFirst_DoesNotCallBackend()
    {
        TestHttpMessageHandler handler = new(CreateMetadataOnlyRollbackResponse);
        using HttpClient httpClient = new(handler) { Timeout = TimeSpan.FromSeconds(5) };
        using BackendGateway gateway = new(CreateBackendConfiguration(), httpClient);
        HonuaOperationsToolkit toolkit = new(
            CreateRuntime(ExecutionMode.Execute, ExecutionTier.ExecuteLowerEnv, rollbackEnabled: true),
            gateway,
            OperatorPolicyModel.Default);

        await Assert.ThrowsAsync<InvalidOperationException>(() => toolkit.RunbookExecuteAsync(
            runbookName: "deploy-rollback",
            service: "roads-api",
            environment: "dev",
            parameters: "operationId=deploy-123",
            confirmed: true,
            edition: "enterprise"));

        Assert.Empty(handler.CapturedRequests);
    }

    [Fact]
    public async Task RunbookExecuteAsync_DataAffectingRollback_ReturnsApprovalRequiredWithoutPost()
    {
        TestHttpMessageHandler handler = new(_ => TestHttpMessageHandler.JsonOk(new
        {
            operationId = "deploy-123",
            status = "Succeeded",
            metadataRelease = new { rollbackPlan = new { @class = "SnapshotRestore", isDataAffecting = true } }
        }));
        using HttpClient httpClient = new(handler) { Timeout = TimeSpan.FromSeconds(5) };
        using BackendGateway gateway = new(CreateBackendConfiguration(), httpClient);
        HonuaOperationsToolkit toolkit = new(
            CreateRuntime(ExecutionMode.Execute, ExecutionTier.ExecuteLowerEnv, rollbackEnabled: true),
            gateway,
            DirectAllowedPolicy());

        OperationResponse response = await toolkit.RunbookExecuteAsync(
            runbookName: "deploy-rollback",
            service: "roads-api",
            environment: "dev",
            parameters: "operationId=deploy-123",
            confirmed: true,
            edition: "enterprise");

        Assert.Equal("runbook-approval-required", response.Status);
        CapturedRequest readRequest = Assert.Single(handler.CapturedRequests);
        Assert.Equal("GET", readRequest.Method);
        Assert.DoesNotContain(handler.CapturedRequests, request => request.Uri.EndsWith("/rollback", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AutoRemediationPlanAsync_RollbackDisabled_ReturnsSharedRefusalWithoutBackendCall()
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

        Assert.Equal("experimental-disabled", response.Status);
        Assert.Contains(response.Actions, action => action.Contains("forward", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(handler.CapturedRequests);
    }

    [Fact]
    public async Task AutoRemediationPlanAsync_RollsBackDeployOperationWhenOperationIdIsProvided()
    {
        TestHttpMessageHandler handler = new(CreateMetadataOnlyRollbackResponse);
        using HttpClient httpClient = new(handler) { Timeout = TimeSpan.FromSeconds(5) };
        using BackendGateway gateway = new(CreateBackendConfiguration(), httpClient);
        HonuaOperationsToolkit toolkit = new(
            CreateRuntime(ExecutionMode.Execute, ExecutionTier.ExecuteLowerEnv, rollbackEnabled: true),
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
        Assert.Equal(2, handler.CapturedRequests.Count);
        Assert.Equal("GET", handler.CapturedRequests[0].Method);
        CapturedRequest rollbackRequest = handler.CapturedRequests[1];
        Assert.Equal("POST", rollbackRequest.Method);
        Assert.Contains("/api/v1/admin/deploy/operations/deploy-123/rollback", rollbackRequest.Uri, StringComparison.Ordinal);
        using JsonDocument requestJson = JsonDocument.Parse(rollbackRequest.Body!);
        Assert.Contains("auto-remediation:roads-api", requestJson.RootElement.GetProperty("reason").GetString(), StringComparison.Ordinal);
        Assert.NotNull(response.BackendSteps);
        Assert.Contains(response.BackendSteps!, step => step.Name == "deploy-operation-rollback" && step.MutatesState);
    }

    [Fact]
    public async Task AutoRemediationPlanAsync_RollbackEnabledWithPrFirst_DoesNotCallBackend()
    {
        TestHttpMessageHandler handler = new(CreateMetadataOnlyRollbackResponse);
        using HttpClient httpClient = new(handler) { Timeout = TimeSpan.FromSeconds(5) };
        using BackendGateway gateway = new(CreateBackendConfiguration(), httpClient);
        HonuaOperationsToolkit toolkit = new(
            CreateRuntime(ExecutionMode.Execute, ExecutionTier.ExecuteLowerEnv, rollbackEnabled: true),
            gateway,
            OperatorPolicyModel.Default);

        OperationResponse response = await toolkit.AutoRemediationPlanAsync(
            service: "roads-api",
            environment: "dev",
            detectedIssue: "failed rollout operationId=deploy-123",
            desiredOutcome: "rollback to last healthy revision",
            autoApply: true,
            edition: "enterprise");

        Assert.Equal("auto-remediation-approval-required", response.Status);
        Assert.Empty(handler.CapturedRequests);
    }

    [Fact]
    public async Task AutoRemediationPlanAsync_ExecuteLowerEnvCannotRollbackProd()
    {
        TestHttpMessageHandler handler = new(CreateMetadataOnlyRollbackResponse);
        using HttpClient httpClient = new(handler) { Timeout = TimeSpan.FromSeconds(5) };
        using BackendGateway gateway = new(CreateBackendConfiguration(), httpClient);
        HonuaOperationsToolkit toolkit = new(
            CreateRuntime(ExecutionMode.Execute, ExecutionTier.ExecuteLowerEnv, rollbackEnabled: true),
            gateway,
            DirectAllowedPolicy());

        await Assert.ThrowsAsync<InvalidOperationException>(() => toolkit.AutoRemediationPlanAsync(
            service: "roads-api",
            environment: "prod",
            detectedIssue: "failed rollout operationId=deploy-123",
            desiredOutcome: "rollback to last healthy revision",
            autoApply: true,
            edition: "enterprise"));

        Assert.Empty(handler.CapturedRequests);
    }

    private static OperationRuntime CreateRuntime(
        ExecutionMode mode = ExecutionMode.Plan,
        ExecutionTier executionTier = ExecutionTier.Plan,
        bool rollbackEnabled = false)
    {
        return new OperationRuntime(
            mode,
            executionTier,
            "honua-gitops",
            AllowedEnvironments: ["dev", "staging", "prod"],
            TerraformRepository: "https://github.com/honua-io/honua-terraform",
            TerraformRef: "main",
            TerraformLocalPath: "/tmp/honua-terraform",
            TerraformDeploymentTargets: ["eks", "aks"],
            RollbackEnabled: rollbackEnabled);
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

    private static HttpResponseMessage CreateMetadataOnlyRollbackResponse(HttpRequestMessage request)
    {
        if (request.Method == HttpMethod.Post &&
            request.RequestUri!.AbsolutePath.EndsWith("/rollback", StringComparison.Ordinal))
        {
            return TestHttpMessageHandler.JsonOk(new { operationId = "deploy-123", status = "RolledBack" });
        }

        return TestHttpMessageHandler.JsonOk(new
        {
            operationId = "deploy-123",
            status = "Succeeded",
            metadataRelease = new { rollbackPlan = new { @class = "MetadataOnly", isDataAffecting = false } }
        });
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
