using System.Net;
using System.Net.Http;
using Honua.DevOps.Agent.Operations;
using Honua.DevOps.Agent.Operations.DesiredState;
using Honua.DevOps.Agent.Operations.GitOps;

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
    public void BuildEndpoint_RejectsResolvedEndpointOutsideBaseHost()
    {
        Uri baseUri = new("https://example.com/honua/api");

        Assert.Throws<InvalidOperationException>(
            () => BackendGateway.BuildEndpoint(baseUri, "/https://evil.example/v1/logs/search"));
    }

    // Issue #153: the planning/diff route to /manifest/apply is pinned to dryRun=true and
    // verifies it rather than trusting the caller. A planning path that a boolean can switch
    // into a write is exactly the shared-authority bug the actuation spine exists to remove.
    [Fact]
    public async Task PreviewManifestAsync_RefusesANonDryRunRequestBeforeSending()
    {
        TestHttpMessageHandler handler = new(_ => TestHttpMessageHandler.JsonOk(new { status = "ok" }));
        using HttpClient httpClient = new(handler) { Timeout = TimeSpan.FromSeconds(5) };
        using BackendGateway gateway = new(BackendConfigurationFixture.Create(), httpClient);

        ManifestApplyRequest writeRequest = BackendGateway.BuildGitOpsManifestRequest(
            service: "roads-api",
            environments: ["dev"],
            revision: "main",
            action: "sync",
            changeSummary: "attempted write via the planning route",
            gitOpsTool: "honua-gitops",
            terraformRepository: "https://github.com/honua-io/honua-iac",
            terraformRef: "main",
            deploymentTargets: ["eks"],
            dryRun: false,
            executionMode: ExecutionMode.Execute,
            executionTier: ExecutionTier.ExecuteLowerEnv,
            allowedEnvironments: ["dev"]);

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => gateway.PreviewManifestAsync(writeRequest, CancellationToken.None));

        Assert.Contains("requires dryRun=true", exception.Message, StringComparison.Ordinal);
        Assert.Empty(handler.CapturedRequests);
    }

    [Fact]
    public async Task PlanGitOpsDeployAsync_SendsOnlyReadsPlansAndADryRunPreview()
    {
        TestHttpMessageHandler handler = new(_ => TestHttpMessageHandler.JsonOk(new { status = "ok" }));
        using HttpClient httpClient = new(handler) { Timeout = TimeSpan.FromSeconds(5) };
        using BackendGateway gateway = new(BackendConfigurationFixture.Create(), httpClient);

        using GitOpsDeployBackendResult result = await gateway.PlanGitOpsDeployAsync(
            service: "roads-api",
            environments: ["dev"],
            revision: "main",
            action: "sync",
            changeSummary: "plan",
            gitOpsTool: "honua-gitops",
            terraformRepository: "https://github.com/honua-io/honua-iac",
            terraformRef: "main",
            deploymentTargets: ["eks"],
            executionMode: ExecutionMode.Execute,
            executionTier: ExecutionTier.ExecuteLowerEnv,
            allowedEnvironments: ["dev"],
            deployTargetId: "prod-api",
            CancellationToken.None);

        Assert.NotNull(result);

        // The only POST body that reaches /manifest/apply on this path is a dry run, and no
        // deploy operation is created or submitted by planning.
        CapturedRequest preview = Assert.Single(
            handler.CapturedRequests,
            request => request.Uri.Contains("/manifest/apply", StringComparison.Ordinal));
        Assert.Contains("\"dryRun\":true", preview.Body!, StringComparison.Ordinal);
        Assert.DoesNotContain(
            handler.CapturedRequests,
            request => request.Uri.EndsWith("/deploy/operations", StringComparison.Ordinal)
                || request.Uri.EndsWith("/submit", StringComparison.Ordinal)
                || request.Uri.EndsWith("/rollback", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RequestTroubleshootAsync_PartialFailures_ReturnsUnsuccessfulResult()
    {
        int callCount = 0;
        TestHttpMessageHandler handler = new(_ =>
        {
            callCount++;
            return callCount == 2
                ? new HttpResponseMessage(HttpStatusCode.BadGateway)
                {
                    Content = new StringContent("upstream error")
                }
                : TestHttpMessageHandler.JsonOk(new { status = "ok" });
        });

        using HttpClient httpClient = new(handler)
        {
            Timeout = TimeSpan.FromSeconds(5)
        };
        using BackendGateway gateway = new(CreateBackendConfiguration(), httpClient);

        BackendCallResult result = await gateway.RequestTroubleshootAsync(
            service: "roads-api",
            environment: "prod",
            incidentSummary: "timeouts",
            suspectedComponent: "database",
            businessImpact: "degraded user experience",
            cancellationToken: CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains("2/3 endpoint calls succeeded", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RequestTroubleshootAsync_PropagatesIncidentContextToHonuaRequests()
    {
        TestHttpMessageHandler handler = new(_ => TestHttpMessageHandler.JsonOk(new { status = "ok" }));
        using HttpClient httpClient = new(handler)
        {
            Timeout = TimeSpan.FromSeconds(5)
        };
        using BackendGateway gateway = new(CreateBackendConfiguration(), httpClient);

        BackendCallResult result = await gateway.RequestTroubleshootAsync(
            service: "roads-api",
            environment: "prod",
            incidentSummary: "timeouts on export",
            suspectedComponent: "database pool",
            businessImpact: "degraded checkout",
            cancellationToken: CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(3, handler.CapturedRequests.Count);
        Assert.All(handler.CapturedRequests, request =>
        {
            Assert.Contains("service=roads-api", request.Uri, StringComparison.Ordinal);
            Assert.Contains("environment=prod", request.Uri, StringComparison.Ordinal);
            Assert.Contains("incidentSummary=timeouts%20on%20export", request.Uri, StringComparison.Ordinal);
            Assert.Contains("suspectedComponent=database%20pool", request.Uri, StringComparison.Ordinal);
            Assert.Contains("businessImpact=degraded%20checkout", request.Uri, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task RequestTuneAsync_PropagatesTuningContextToHonuaRequests()
    {
        TestHttpMessageHandler handler = new(_ => TestHttpMessageHandler.JsonOk(new { status = "ok" }));
        using HttpClient httpClient = new(handler)
        {
            Timeout = TimeSpan.FromSeconds(5)
        };
        using BackendGateway gateway = new(CreateBackendConfiguration(), httpClient);

        BackendCallResult result = await gateway.RequestTuneAsync(
            service: "roads-api",
            environment: "staging",
            workloadProfile: "batch imports",
            bottleneck: "cache miss storm",
            targetSlo: "p95 < 250ms",
            cancellationToken: CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(5, handler.CapturedRequests.Count);
        Assert.All(handler.CapturedRequests, request =>
        {
            Assert.Contains("service=roads-api", request.Uri, StringComparison.Ordinal);
            Assert.Contains("environment=staging", request.Uri, StringComparison.Ordinal);
            Assert.Contains("workloadProfile=batch%20imports", request.Uri, StringComparison.Ordinal);
            Assert.Contains("bottleneck=cache%20miss%20storm", request.Uri, StringComparison.Ordinal);
            Assert.Contains("targetSlo=p95%20%3C%20250ms", request.Uri, StringComparison.Ordinal);
        });
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
