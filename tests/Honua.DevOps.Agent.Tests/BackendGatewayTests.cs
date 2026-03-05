using System.Net;
using Honua.DevOps.Agent.Operations;

namespace Honua.DevOps.Agent.Tests;

public class BackendGatewayTests
{
    [Fact]
    public void BuildEndpoint_PreservesBasePathSegments()
    {
        Uri baseUri = new("https://example.com/honua/api");

        Uri endpoint = BackendGateway.BuildEndpoint(baseUri, "v1/metrics/search");

        Assert.Equal("https://example.com/honua/api/v1/metrics/search", endpoint.ToString());
    }

    [Fact]
    public void BuildEndpoint_HandlesEmptyRelativePath()
    {
        Uri baseUri = new("https://example.com/honua/api");

        Uri endpoint = BackendGateway.BuildEndpoint(baseUri, string.Empty);

        Assert.Equal("https://example.com/honua/api/", endpoint.ToString());
    }

    [Fact]
    public async Task ProbeOtelAsync_TruncatesResponsePreview()
    {
        string largeBody = new('x', 1200);
        TestHttpMessageHandler handler = new(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(largeBody)
            });

        using HttpClient httpClient = new(handler)
        {
            Timeout = TimeSpan.FromSeconds(5)
        };
        using BackendGateway gateway = new(CreateBackendConfiguration(), httpClient);

        BackendCallResult result = await gateway.ProbeOtelAsync(CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.EndsWith("...", result.PayloadPreview, StringComparison.Ordinal);
        Assert.True(result.PayloadPreview.Length <= 403);
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
