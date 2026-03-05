using System.Text.Json;
using Honua.DevOps.Agent.Operations;

namespace Honua.DevOps.Agent.Tests;

public class HonuaOperationsToolkitDeployTests
{
    [Fact]
    public async Task DeployServiceWithGitOpsAsync_ThrowsOnInvalidEnvironmentInput()
    {
        using BackendGateway gateway = CreateGateway(_ => TestHttpMessageHandler.JsonOk(new { status = "ok" }));
        HonuaOperationsToolkit toolkit = new(CreateRuntime(), gateway);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => toolkit.DeployServiceWithGitOpsAsync(
                "roads-api",
                "qa",
                "main",
                "sync",
                "release",
                CancellationToken.None));
    }

    [Fact]
    public async Task DeployServiceWithGitOpsAsync_ThrowsOnInvalidActionInput()
    {
        using BackendGateway gateway = CreateGateway(_ => TestHttpMessageHandler.JsonOk(new { status = "ok" }));
        HonuaOperationsToolkit toolkit = new(CreateRuntime(), gateway);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => toolkit.DeployServiceWithGitOpsAsync(
                "roads-api",
                "dev",
                "main",
                "sync;rm",
                "release",
                CancellationToken.None));
    }

    [Fact]
    public async Task DeployServiceWithGitOpsAsync_ThrowsOnUnsafeServiceName()
    {
        using BackendGateway gateway = CreateGateway(_ => TestHttpMessageHandler.JsonOk(new { status = "ok" }));
        HonuaOperationsToolkit toolkit = new(CreateRuntime(), gateway);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => toolkit.DeployServiceWithGitOpsAsync(
                "roads-api;drop",
                "dev",
                "main",
                "sync",
                "release",
                CancellationToken.None));
    }

    [Fact]
    public async Task DeployServiceWithGitOpsAsync_SanitizesPayloadAndCommands()
    {
        TestHttpMessageHandler handler = new(_ => TestHttpMessageHandler.JsonOk(new { status = "ok" }));
        using HttpClient httpClient = new(handler)
        {
            Timeout = TimeSpan.FromSeconds(5)
        };
        using BackendGateway gateway = new(CreateBackendConfiguration(), httpClient);

        OperationRuntime runtime = CreateRuntime();
        HonuaOperationsToolkit toolkit = new(runtime, gateway);

        OperationResponse response = await toolkit.DeployServiceWithGitOpsAsync(
            service: "roads-api",
            environmentsCsv: "dev",
            revision: "feature/main",
            action: "dryrun",
            changeSummary: "deploy\u0000 now",
            cancellationToken: CancellationToken.None);

        CapturedRequest applyRequest = Assert.Single(
            handler.CapturedRequests,
            request => request.Method == HttpMethod.Post.Method);
        using JsonDocument requestJson = JsonDocument.Parse(applyRequest.Body!);
        string serialized = requestJson.RootElement.ToString();

        Assert.Contains("\"changeSummary\":\"deploy now\"", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u0000", serialized, StringComparison.Ordinal);
        Assert.Contains(response.Actions, action =>
            action.Contains("honua gitops dry-run --service roads-api --env dev --revision feature/main", StringComparison.Ordinal));
    }

    private static OperationRuntime CreateRuntime(string gitOpsTool = "honua-gitops")
    {
        return new OperationRuntime(
            ExecutionMode.Plan,
            gitOpsTool,
            AllowedEnvironments: ["dev", "staging", "prod"],
            TerraformRepository: "https://github.com/honua-io/honua-terraform",
            TerraformRef: "main",
            TerraformLocalPath: "/tmp/honua-terraform",
            TerraformDeploymentTargets: ["eks", "aks"]);
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
