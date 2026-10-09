using System.Net;
using Honua.DevOps.Agent.Operations;
using Microsoft.Extensions.AI;

namespace Honua.DevOps.Agent.Tests;

// Release posture: single-environment deploy + health-gated fix-forward. Rollback and
// cross-environment promotion are experimental and OFF by default; recovery is a forward
// change through the governed deploy path.
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
    public void CapabilityToolset_OmitsRollbackTool_WhenRollbackDisabled()
    {
        using BackendGateway gateway = CreateGateway(_ => TestHttpMessageHandler.JsonOk(new { status = "ok" }));
        IList<AITool> tools = CapabilityToolset.Create(CreateRuntime(rollbackEnabled: false), gateway);

        string[] names = [.. tools.Select(tool => tool.Name)];
        Assert.DoesNotContain("rollback_gitops_operation", names);
        Assert.Contains("deploy_service_gitops", names);
    }

    [Fact]
    public void CapabilityToolset_AdvertisesRollbackTool_WhenRollbackEnabled()
    {
        using BackendGateway gateway = CreateGateway(_ => TestHttpMessageHandler.JsonOk(new { status = "ok" }));
        IList<AITool> tools = CapabilityToolset.Create(CreateRuntime(rollbackEnabled: true), gateway);

        string[] names = [.. tools.Select(tool => tool.Name)];
        Assert.Contains("rollback_gitops_operation", names);
        Assert.Contains("deploy_service_gitops", names);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CapabilityToolset_DoesNotRegisterRetiredOpsLoopTools(bool rollbackEnabled)
    {
        // 2026.1 owner ruling: the devops ops loop is deleted, not flag-gated. Day-2
        // observe/propose belongs to honua-server /mcp (honua_ops_*, honua_propose_*).
        using BackendGateway gateway = CreateGateway(_ => TestHttpMessageHandler.JsonOk(new { status = "ok" }));
        IList<AITool> tools = CapabilityToolset.Create(CreateRuntime(rollbackEnabled), gateway);

        string[] names = [.. tools.Select(tool => tool.Name)];
        string[] retired =
        [
            "honua_observe_diagnose_propose", "honua_auto_remediation_plan", "honua_runbook_execute",
            "plan_server_upgrade", "plan_forward_fix", "honua_diagnose", "honua_explain_slow_queries",
            "analyze_logs", "analyze_metrics", "tune_performance", "troubleshoot_incident"
        ];
        Assert.Empty(names.Intersect(retired, StringComparer.Ordinal));
        Assert.Contains("provision_infrastructure", names);
        Assert.Contains("install_handoff", names);
        Assert.Contains("verify_install_handoff", names);
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
