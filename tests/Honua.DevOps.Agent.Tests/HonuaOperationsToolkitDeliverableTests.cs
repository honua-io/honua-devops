using Honua.DevOps.Agent.Operations;
using Honua.DevOps.Agent.Operations.Deliverable;

namespace Honua.DevOps.Agent.Tests;

public class HonuaOperationsToolkitDeliverableTests
{
    [Fact]
    public async Task PlanDeliverableLifecycleAsync_ReturnsPlanDryRunWithNoBackendMutation()
    {
        TestHttpMessageHandler handler = new(_ => TestHttpMessageHandler.JsonOk(new { status = "ok" }));
        using HttpClient httpClient = new(handler) { Timeout = TimeSpan.FromSeconds(5) };
        using BackendGateway gateway = new(CreateBackendConfiguration(), httpClient);
        HonuaOperationsToolkit toolkit = new(CreateRuntime(), gateway, defaultEdition: "enterprise");

        OperationResponse response = await toolkit.PlanDeliverableLifecycleAsync(
            workItemId: "GIS-42",
            kind: "map",
            currentState: "draft",
            lowerEnvironment: "dev",
            publishEnvironment: "prod",
            previewUrl: "",
            edition: "enterprise",
            cancellationToken: CancellationToken.None);

        Assert.Equal("deliverable-lifecycle-plan", response.Status);
        Assert.NotNull(response.DeliverableLifecycle);
        Assert.Empty(handler.CapturedRequests); // no backend call at all — pure planner.

        DeliverableProjection projection = response.DeliverableLifecycle!;
        Assert.Equal("GIS-42:map", projection.DeliverableId);
        Assert.Equal("draft", projection.CurrentState);
        Assert.Equal(3, projection.Transitions.Count);
        Assert.False(projection.PreviewLink.Available); // never fabricated.

        // Governed Preview -> Approved action is surfaced.
        Assert.Single(projection.SuggestedActions);
        Assert.All(projection.SuggestedActions, action => Assert.True(action.RequiresApproval));
        Assert.Contains(response.ValidationChecks, c => c == "deliverable-lifecycle-no-promotion-execution");
    }

    [Fact]
    public async Task PlanDeliverableLifecycleAsync_IsDeterministic()
    {
        using BackendGateway gateway = CreateGateway();
        HonuaOperationsToolkit toolkit = new(CreateRuntime(), gateway, defaultEdition: "enterprise");

        OperationResponse first = await toolkit.PlanDeliverableLifecycleAsync(
            "GIS-42", "map", "draft", "dev", "prod", "", "enterprise", CancellationToken.None);
        OperationResponse second = await toolkit.PlanDeliverableLifecycleAsync(
            "GIS-42", "map", "draft", "dev", "prod", "", "enterprise", CancellationToken.None);

        Assert.Equal(first.Summary, second.Summary);
        Assert.Equal(first.Findings, second.Findings);
        Assert.Equal(first.Actions, second.Actions);
        Assert.Equal(first.ValidationChecks, second.ValidationChecks);
    }

    [Fact]
    public async Task PlanDeliverableLifecycleAsync_SingleEnvLifecycleAllowedAtPro()
    {
        using BackendGateway gateway = CreateGateway();
        HonuaOperationsToolkit toolkit = new(CreateRuntime(), gateway, defaultEdition: "pro");

        OperationResponse response = await toolkit.PlanDeliverableLifecycleAsync(
            "GIS-42", "map", "draft", "dev", "prod", "", "pro", CancellationToken.None);

        Assert.Equal("deliverable-lifecycle-plan", response.Status);
        DeliverableProjection projection = response.DeliverableLifecycle!;

        // Single-env steps are unlocked; cross-env promotion is planned but edition-gated.
        Assert.False(projection.CrossEnvironmentPromotionUnlocked);
        DeliverableTransitionProjection publish = projection.Transitions.Single(t => t.ToState == "published");
        Assert.True(publish.EditionGated);
        Assert.Equal("enterprise", publish.RequiredEdition);

        DeliverableTransitionProjection approval = projection.Transitions.Single(t => t.ToState == "approved");
        Assert.False(approval.EditionGated);
        Assert.NotNull(approval.ApprovalAction);
    }

    [Fact]
    public async Task PlanDeliverableLifecycleAsync_RefusedBelowProAsEditionGated()
    {
        using BackendGateway gateway = CreateGateway();
        HonuaOperationsToolkit toolkit = new(CreateRuntime(), gateway, defaultEdition: "community");

        OperationResponse response = await toolkit.PlanDeliverableLifecycleAsync(
            "GIS-42", "map", "draft", "dev", "prod", "", "community", CancellationToken.None);

        Assert.Equal("edition-gated", response.Status);
        Assert.Contains("pro", response.Summary, StringComparison.Ordinal);
        Assert.Null(response.DeliverableLifecycle);
    }

    [Fact]
    public async Task PlanDeliverableLifecycleAsync_CrossEnvPromotionUnlockedAtEnterprise()
    {
        using BackendGateway gateway = CreateGateway();
        HonuaOperationsToolkit toolkit = new(CreateRuntime(), gateway, defaultEdition: "enterprise");

        OperationResponse response = await toolkit.PlanDeliverableLifecycleAsync(
            "GIS-42", "map", "approved", "dev", "prod", "", "enterprise", CancellationToken.None);

        DeliverableProjection projection = response.DeliverableLifecycle!;
        Assert.True(projection.CrossEnvironmentPromotionUnlocked);

        DeliverableTransitionProjection publish = Assert.Single(projection.Transitions);
        Assert.Equal("published", publish.ToState);
        Assert.False(publish.EditionGated);
        Assert.Equal("prod", publish.TargetEnvironment);
        Assert.Contains("slo-gate-evidence", publish.RequiredEvidence);
    }

    [Fact]
    public async Task PlanDeliverableLifecycleAsync_SurfacesProvidedPreviewLink()
    {
        using BackendGateway gateway = CreateGateway();
        HonuaOperationsToolkit toolkit = new(CreateRuntime(), gateway, defaultEdition: "enterprise");

        OperationResponse response = await toolkit.PlanDeliverableLifecycleAsync(
            "GIS-42", "dashboard", "preview", "dev", "prod",
            previewUrl: "https://preview.honua.io/GIS-42",
            edition: "enterprise",
            cancellationToken: CancellationToken.None);

        DeliverableProjection projection = response.DeliverableLifecycle!;
        Assert.True(projection.PreviewLink.Available);
        Assert.Equal("https://preview.honua.io/GIS-42", projection.PreviewLink.Href);
    }

    private static BackendGateway CreateGateway()
    {
        TestHttpMessageHandler handler = new(_ => TestHttpMessageHandler.JsonOk(new { status = "ok" }));
        HttpClient httpClient = new(handler) { Timeout = TimeSpan.FromSeconds(5) };
        return new BackendGateway(CreateBackendConfiguration(), httpClient);
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
            TerraformDeploymentTargets: ["eks", "aks"],
            DeployTargetId: null);
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
