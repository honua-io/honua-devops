using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Honua.DevOps.Agent.Operations;

namespace Honua.DevOps.Agent.Tests;

public class HonuaOperatorReadinessTests
{
    [Fact]
    public async Task DescribeEnvironmentAsync_AggregatesReadinessCapabilitiesAndManifest()
    {
        TestHttpMessageHandler handler = new(request =>
        {
            string path = request.RequestUri!.AbsolutePath;
            if (path.Contains("healthz/ready", StringComparison.Ordinal))
            {
                return TestHttpMessageHandler.JsonOk(new { status = "ready" });
            }

            if (path.Contains("/api/v1/admin/capabilities", StringComparison.Ordinal))
            {
                return TestHttpMessageHandler.JsonOk(new
                {
                    edition = "enterprise",
                    features = new[] { "runbooks", "auto-remediation" }
                });
            }

            if (path.Contains("/api/v1/admin/manifest", StringComparison.Ordinal))
            {
                return TestHttpMessageHandler.JsonOk(new
                {
                    services = new[] { "roads-api", "geocoder" }
                });
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        using HttpClient httpClient = new(handler) { Timeout = TimeSpan.FromSeconds(5) };
        using BackendGateway gateway = new(CreateBackendConfiguration(), httpClient);
        HonuaOperationsToolkit toolkit = new(CreateRuntime(), gateway, defaultEdition: "pro");

        OperationResponse response = await toolkit.DescribeEnvironmentAsync();

        Assert.Equal("environment-described", response.Status);
        Assert.NotNull(response.BackendSteps);
        Assert.Equal(3, response.BackendSteps!.Count);
        Assert.All(response.BackendSteps, step => Assert.False(step.MutatesState));
        Assert.Contains(response.Findings, finding => finding.Contains("Detected edition: enterprise", StringComparison.Ordinal));
        Assert.Equal(3, handler.CapturedRequests.Count);
    }

    [Fact]
    public void ExtractEditionFromCapabilities_ReturnsEditionWhenPresent()
    {
        using JsonDocument document = JsonDocument.Parse("""{"edition":"pro"}""");
        Assert.Equal("pro", BackendGateway.ExtractEditionFromCapabilities(document));
    }

    [Fact]
    public void ExtractEditionFromCapabilities_FindsNestedEdition()
    {
        using JsonDocument document = JsonDocument.Parse("""{"license":{"licenseEdition":"enterprise"}}""");
        Assert.Equal("enterprise", BackendGateway.ExtractEditionFromCapabilities(document));
    }

    [Fact]
    public void ExtractEditionFromCapabilities_ReturnsNullWhenAbsent()
    {
        using JsonDocument document = JsonDocument.Parse("""{"features":["a","b"]}""");
        Assert.Null(BackendGateway.ExtractEditionFromCapabilities(document));
    }

    [Fact]
    public async Task EditionGatedTool_UsesSessionDefaultWhenCallerOmitsEdition()
    {
        TestHttpMessageHandler handler = new(_ => TestHttpMessageHandler.JsonOk(new { status = "ok" }));
        using HttpClient httpClient = new(handler) { Timeout = TimeSpan.FromSeconds(5) };
        using BackendGateway gateway = new(CreateBackendConfiguration(), httpClient);
        HonuaOperationsToolkit toolkit = new(CreateRuntime(), gateway, defaultEdition: "pro");

        OperationResponse response = await toolkit.ExplainSlowQueriesAsync(
            service: "roads-api",
            environment: "dev",
            timeframe: "1h",
            slowQuerySample: "SELECT * FROM parcels",
            edition: string.Empty);

        Assert.NotEqual("edition-gated", response.Status);
    }

    [Fact]
    public async Task EditionGatedTool_GatesWhenSessionDefaultIsCommunity()
    {
        TestHttpMessageHandler handler = new(_ => TestHttpMessageHandler.JsonOk(new { status = "ok" }));
        using HttpClient httpClient = new(handler) { Timeout = TimeSpan.FromSeconds(5) };
        using BackendGateway gateway = new(CreateBackendConfiguration(), httpClient);
        HonuaOperationsToolkit toolkit = new(CreateRuntime(), gateway, defaultEdition: "community");

        OperationResponse response = await toolkit.ExplainSlowQueriesAsync(
            service: "roads-api",
            environment: "dev",
            timeframe: "1h",
            slowQuerySample: "SELECT * FROM parcels",
            edition: string.Empty);

        Assert.Equal("edition-gated", response.Status);
    }

    private static OperationRuntime CreateRuntime()
    {
        return new OperationRuntime(
            ExecutionMode.Plan,
            ExecutionTier.Plan,
            "honua-gitops",
            AllowedEnvironments: ["dev", "staging", "prod"],
            TerraformRepository: "https://github.com/honua-io/honua-iac",
            TerraformRef: "main",
            TerraformLocalPath: "/tmp/honua-iac",
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
}
