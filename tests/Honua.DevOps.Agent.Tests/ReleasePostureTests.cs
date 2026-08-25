using System.Net;
using Honua.DevOps.Agent.Operations;
using Microsoft.Extensions.AI;

namespace Honua.DevOps.Agent.Tests;

// Release posture: single-environment deploy + health-gated fix-forward. Rollback and
// cross-environment promotion are experimental and OFF by default; plan_forward_fix is the
// forward-only recovery path.
public class ReleasePostureTests
{
    [Fact]
    public void OperationRuntimeLoad_DefaultsExperimentalCapabilitiesOff()
    {
        using TestEnvironmentVariableScope scope = new();
        scope.Set(OperationRuntime.RollbackEnabledVariable, null);
        scope.Set(OperationRuntime.CrossEnvironmentPromotionEnabledVariable, null);

        OperationRuntime runtime = OperationRuntime.Load();

        Assert.False(runtime.RollbackEnabled);
        Assert.False(runtime.CrossEnvironmentPromotionEnabled);
    }

    [Theory]
    [InlineData("true")]
    [InlineData("1")]
    [InlineData("YES")]
    [InlineData("On")]
    public void OperationRuntimeLoad_EnablesRollbackOnTruthyFlag(string value)
    {
        using TestEnvironmentVariableScope scope = new();
        scope.Set(OperationRuntime.RollbackEnabledVariable, value);

        OperationRuntime runtime = OperationRuntime.Load();

        Assert.True(runtime.RollbackEnabled);
    }

    [Theory]
    [InlineData("false")]
    [InlineData("0")]
    [InlineData("")]
    [InlineData("maybe")]
    public void OperationRuntimeLoad_KeepsRollbackOffForNonTruthyFlag(string value)
    {
        using TestEnvironmentVariableScope scope = new();
        scope.Set(OperationRuntime.RollbackEnabledVariable, value);

        OperationRuntime runtime = OperationRuntime.Load();

        Assert.False(runtime.RollbackEnabled);
    }

    [Fact]
    public void CapabilityToolset_OmitsRollbackTool_ButKeepsForwardFix_WhenRollbackDisabled()
    {
        using BackendGateway gateway = CreateGateway(_ => TestHttpMessageHandler.JsonOk(new { status = "ok" }));
        IList<AITool> tools = CapabilityToolset.Create(CreateRuntime(rollbackEnabled: false), gateway);

        string[] names = [.. tools.Select(tool => tool.Name)];
        Assert.DoesNotContain("rollback_gitops_operation", names);
        Assert.Contains("plan_forward_fix", names);
    }

    [Fact]
    public void CapabilityToolset_AdvertisesRollbackTool_WhenRollbackEnabled()
    {
        using BackendGateway gateway = CreateGateway(_ => TestHttpMessageHandler.JsonOk(new { status = "ok" }));
        IList<AITool> tools = CapabilityToolset.Create(CreateRuntime(rollbackEnabled: true), gateway);

        string[] names = [.. tools.Select(tool => tool.Name)];
        Assert.Contains("rollback_gitops_operation", names);
        Assert.Contains("plan_forward_fix", names);
    }

    [Fact]
    public async Task RollbackGitOpsOperationAsync_RefusesWhenRollbackDisabled()
    {
        using BackendGateway gateway = CreateGateway(_ => TestHttpMessageHandler.JsonOk(new { status = "ok" }));
        HonuaOperationsToolkit toolkit = new(CreateRuntime(rollbackEnabled: false), gateway);

        OperationResponse response = await toolkit.RollbackGitOpsOperationAsync(
            "op-123",
            "revert bad deploy",
            CancellationToken.None);

        Assert.Equal("experimental-disabled", response.Status);
        Assert.Contains(response.Actions, action => action.Contains("forward", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task DeployServiceWithGitOpsAsync_RefusesPromote_WhenCrossEnvPromotionDisabled()
    {
        using BackendGateway gateway = CreateGateway(_ => TestHttpMessageHandler.JsonOk(new { status = "ok" }));
        HonuaOperationsToolkit toolkit = new(CreateRuntime(crossEnvEnabled: false), gateway);

        OperationResponse response = await toolkit.DeployServiceWithGitOpsAsync(
            service: "roads-api",
            environmentsCsv: "staging",
            revision: "main",
            action: "promote",
            changeSummary: "promote validated build",
            cancellationToken: CancellationToken.None);

        Assert.Equal("experimental-disabled", response.Status);
    }

    [Fact]
    public async Task DeployServiceWithGitOpsAsync_RefusesMultiEnvironmentDeploy_WhenCrossEnvPromotionDisabled()
    {
        using BackendGateway gateway = CreateGateway(_ => TestHttpMessageHandler.JsonOk(new { status = "ok" }));
        HonuaOperationsToolkit toolkit = new(CreateRuntime(crossEnvEnabled: false), gateway);

        OperationResponse response = await toolkit.DeployServiceWithGitOpsAsync(
            service: "roads-api",
            environmentsCsv: "dev,staging",
            revision: "main",
            action: "sync",
            changeSummary: "roll out",
            cancellationToken: CancellationToken.None);

        Assert.Equal("experimental-disabled", response.Status);
    }

    [Fact]
    public async Task DeployServiceWithGitOpsAsync_AllowsSingleEnvironmentSync_WhenCrossEnvPromotionDisabled()
    {
        using BackendGateway gateway = CreateGateway(_ => TestHttpMessageHandler.JsonOk(new { status = "ok" }));
        HonuaOperationsToolkit toolkit = new(CreateRuntime(crossEnvEnabled: false), gateway);

        OperationResponse response = await toolkit.DeployServiceWithGitOpsAsync(
            service: "roads-api",
            environmentsCsv: "dev",
            revision: "main",
            action: "sync",
            changeSummary: "roll out",
            cancellationToken: CancellationToken.None);

        // The single-environment forward path is not gated as experimental.
        Assert.NotEqual("experimental-disabled", response.Status);
    }

    [Fact]
    public async Task PlanForwardFixAsync_ReportsHealthyConverged_WhenBackendHealthy()
    {
        using BackendGateway gateway = CreateGateway(_ => TestHttpMessageHandler.JsonOk(new { status = "ok" }));
        HonuaOperationsToolkit toolkit = new(CreateRuntime(), gateway);

        OperationResponse response = await toolkit.PlanForwardFixAsync(
            service: "roads-api",
            environment: "dev",
            forwardRevision: "main",
            priorOperationId: "",
            symptoms: "",
            cancellationToken: CancellationToken.None);

        Assert.Equal("healthy-converged", response.Status);
    }

    [Fact]
    public async Task PlanForwardFixAsync_RequiresForwardFix_WhenReadinessUnhealthy()
    {
        using BackendGateway gateway = CreateGateway(request =>
            request.RequestUri!.AbsoluteUri.Contains("healthz/ready", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : TestHttpMessageHandler.JsonOk(new { status = "ok" }));
        HonuaOperationsToolkit toolkit = new(CreateRuntime(), gateway);

        OperationResponse response = await toolkit.PlanForwardFixAsync(
            service: "roads-api",
            environment: "dev",
            forwardRevision: "fix/main",
            priorOperationId: "",
            symptoms: "5xx spike",
            cancellationToken: CancellationToken.None);

        Assert.Equal("forward-fix-required", response.Status);
        // Recovery is forward-only: at least one action mentions rolling forward, none issues a rollback.
        Assert.Contains(response.Actions, action => action.Contains("FORWARD", StringComparison.Ordinal));
        Assert.DoesNotContain(response.Actions, action => action.Contains("roll back", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task PlanForwardFixAsync_ReportsBackendUnavailable_WhenBackendDown()
    {
        using BackendGateway gateway = CreateGateway(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        HonuaOperationsToolkit toolkit = new(CreateRuntime(), gateway);

        OperationResponse response = await toolkit.PlanForwardFixAsync(
            service: "roads-api",
            environment: "dev",
            forwardRevision: "main",
            priorOperationId: "",
            symptoms: "",
            cancellationToken: CancellationToken.None);

        Assert.Equal("backend-unavailable", response.Status);
    }

    [Fact]
    public async Task PlanForwardFixAsync_RejectsMultipleEnvironments()
    {
        using BackendGateway gateway = CreateGateway(_ => TestHttpMessageHandler.JsonOk(new { status = "ok" }));
        HonuaOperationsToolkit toolkit = new(CreateRuntime(), gateway);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => toolkit.PlanForwardFixAsync(
                service: "roads-api",
                environment: "dev,staging",
                forwardRevision: "main",
                priorOperationId: "",
                symptoms: "",
                cancellationToken: CancellationToken.None));
    }

    private static OperationRuntime CreateRuntime(
        bool rollbackEnabled = false,
        bool crossEnvEnabled = false)
    {
        return new OperationRuntime(
            ExecutionMode.Plan,
            ExecutionTier.Plan,
            GitOpsTool: "honua-gitops",
            AllowedEnvironments: ["dev", "staging", "prod"],
            TerraformRepository: "https://github.com/honua-io/honua-iac",
            TerraformRef: "main",
            TerraformLocalPath: "/tmp/honua-iac",
            TerraformDeploymentTargets: ["eks", "aks"],
            DeployTargetId: null,
            ProductionEnvironments: null,
            RollbackEnabled: rollbackEnabled,
            CrossEnvironmentPromotionEnabled: crossEnvEnabled);
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
