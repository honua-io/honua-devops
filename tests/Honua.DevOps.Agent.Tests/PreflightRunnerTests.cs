using System.Net;
using Honua.DevOps.Agent.Operations;
using Honua.DevOps.Agent.Operations.OperatorPolicy;
using OperatorPolicyModel = Honua.DevOps.Agent.Operations.OperatorPolicy.OperatorPolicy;

namespace Honua.DevOps.Agent.Tests;

public sealed class PreflightRunnerTests
{
    [Fact]
    public async Task RunAsync_AllowsCustomTargetSetWhenBackendsAreHealthy()
    {
        string terraformPath = CreateTerraformPath();
        try
        {
            TestHttpMessageHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.OK));
            using HttpClient httpClient = new(handler)
            {
                Timeout = TimeSpan.FromSeconds(5)
            };
            using BackendGateway gateway = new(CreateBackendConfiguration(), httpClient);
            OperationRuntime runtime = CreateRuntime(terraformPath, ["eks", "aca"]);

            int exitCode = await PreflightRunner.RunAsync(
                runtime,
                OperatorPolicyModel.Default,
                CreateBackendConfiguration(),
                gateway,
                CancellationToken.None);

            Assert.Equal(0, exitCode);
        }
        finally
        {
            Directory.Delete(terraformPath, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_FailsWhenNoDeploymentTargetsConfigured()
    {
        string terraformPath = CreateTerraformPath();
        try
        {
            TestHttpMessageHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.OK));
            using HttpClient httpClient = new(handler)
            {
                Timeout = TimeSpan.FromSeconds(5)
            };
            using BackendGateway gateway = new(CreateBackendConfiguration(), httpClient);
            OperationRuntime runtime = CreateRuntime(terraformPath, []);

            int exitCode = await PreflightRunner.RunAsync(
                runtime,
                OperatorPolicyModel.Default,
                CreateBackendConfiguration(),
                gateway,
                CancellationToken.None);

            Assert.Equal(2, exitCode);
        }
        finally
        {
            Directory.Delete(terraformPath, recursive: true);
        }
    }

    private static OperationRuntime CreateRuntime(string terraformPath, string[] targets)
    {
        return new OperationRuntime(
            ExecutionMode.Plan,
            ExecutionTier.Plan,
            GitOpsTool: "honua-gitops",
            AllowedEnvironments: ["dev", "staging", "prod"],
            TerraformRepository: "https://github.com/honua-io/honua-iac",
            TerraformRef: "main",
            TerraformLocalPath: terraformPath,
            TerraformDeploymentTargets: targets);
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

    private static string CreateTerraformPath()
    {
        string path = Path.Combine(Path.GetTempPath(), $"honua-devops-preflight-{Guid.NewGuid():N}");
        _ = Directory.CreateDirectory(path);
        return path;
    }
}
