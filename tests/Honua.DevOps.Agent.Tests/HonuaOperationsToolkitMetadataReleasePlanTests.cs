using Honua.DevOps.Agent.Operations;
using Honua.DevOps.Agent.Operations.GitOps;

namespace Honua.DevOps.Agent.Tests;

// Issue #57 fast-follow: plan_metadata_release_gitops fuses the metadata-release change-set builder
// with the honua-gitops planner so a single read-only response carries BOTH a populated
// MetadataReleaseChangeSet and a GitOpsPlan whose MetadataRelease summary is populated. These tests
// mirror the MetadataReleaseChangeSetBuilderTests / GitOpsMetadataReleasePlanTests fixtures and the
// toolkit/deploy test construction patterns, and assert the read-only, backend-free, deterministic
// posture.
public sealed class HonuaOperationsToolkitMetadataReleasePlanTests
{
    private const string ReadyPackage = """
    {
      "releaseId": "rel-2026-03-meta",
      "service": "roads-api",
      "desiredRevision": "release/2026.03",
      "targetEnvironments": ["staging", "prod"],
      "semanticResources": [
        { "kind": "Layer", "name": "roads", "action": "upsert" },
        { "kind": "Style", "name": "roads-default", "action": "upsert" }
      ],
      "compatibility": { "status": "compatible", "breakingChanges": 0, "warnings": 0 },
      "scriptCoverage": { "covered": 2, "total": 2, "uncovered": [] },
      "rollbackPolicy": { "classification": "automatic", "knownGoodRevision": "release/2026.02" }
    }
    """;

    private const string BlockedPackage = """
    {
      "releaseId": "rel-bad",
      "service": "roads-api",
      "desiredRevision": "release/2026.04",
      "targetEnvironments": ["prod"],
      "compatibility": { "status": "incompatible", "breakingChanges": 2, "warnings": 0 },
      "rollbackPolicy": { "classification": "manual", "knownGoodRevision": "release/2026.03" }
    }
    """;

    [Fact]
    public async Task PlanMetadataReleaseGitOpsAsync_ReadyPackage_CarriesChangeSetAndPopulatedPlanSummary()
    {
        TestHttpMessageHandler handler = new(_ => TestHttpMessageHandler.JsonOk(new { status = "ok" }));
        using HttpClient httpClient = new(handler)
        {
            Timeout = TimeSpan.FromSeconds(5)
        };
        using BackendGateway gateway = new(CreateBackendConfiguration(), httpClient);
        HonuaOperationsToolkit toolkit = new(CreateRuntime(), gateway);

        OperationResponse response = await toolkit.PlanMetadataReleaseGitOpsAsync(
            ReadyPackage,
            CancellationToken.None);

        // Read-only: no backend call of any kind was made.
        Assert.Empty(handler.CapturedRequests);

        Assert.Equal(MetadataChangeSetReadiness.Ready, response.Status);

        // Change set is populated.
        Assert.NotNull(response.MetadataReleaseChangeSet);
        Assert.Equal("rel-2026-03-meta", response.MetadataReleaseChangeSet!.ReleasePackageId);
        Assert.Equal("roads-api", response.MetadataReleaseChangeSet.Service);
        Assert.Equal(2, response.MetadataReleaseChangeSet.SemanticResources.Count);

        // GitOps plan is populated and metadata-release-aware.
        Assert.NotNull(response.GitOpsPlan);
        Assert.NotNull(response.ReleaseOrchestration);
        Assert.NotNull(response.ServiceBundleReconciliation);
        Assert.Equal("manifest-export-unavailable", response.GitOpsPlan!.ActualStateSource);

        GitOpsMetadataReleaseSummary summary = Assert.IsType<GitOpsMetadataReleaseSummary>(response.GitOpsPlan.MetadataRelease);
        Assert.Equal("rel-2026-03-meta", summary.ReleasePackageId);
        Assert.Equal("compatible", summary.CompatibilityStatus);
        Assert.Equal(0, summary.BreakingChanges);
        Assert.Equal("covered", summary.ScriptCoverage);
        Assert.Equal(MetadataRollbackClass.Automatic, summary.RollbackClassification);
        Assert.Equal("release/2026.02", summary.KnownGoodRevision);
        Assert.Equal(2, summary.SemanticResources.Count);
        Assert.Empty(summary.BlockingReasons);

        // Both targeted environments are tagged in-scope.
        Assert.All(
            response.GitOpsPlan.Environments,
            environment => Assert.Equal(GitOpsPlanner.MetadataTargetStatus.InScope, environment.MetadataTargetStatus));

        // The metadata-release summary surfaces in the plan output action lines.
        Assert.Contains(response.Actions, action => action.Contains("compatibility: compatible", StringComparison.Ordinal));
        Assert.Contains(response.Actions, action => action.Contains("Metadata semantic resource `Layer/roads`", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PlanMetadataReleaseGitOpsAsync_BlockedPackage_StillReturnsPlanAndSurfacesBlockingReasons()
    {
        TestHttpMessageHandler handler = new(_ => TestHttpMessageHandler.JsonOk(new { status = "ok" }));
        using HttpClient httpClient = new(handler)
        {
            Timeout = TimeSpan.FromSeconds(5)
        };
        using BackendGateway gateway = new(CreateBackendConfiguration(), httpClient);
        HonuaOperationsToolkit toolkit = new(CreateRuntime(), gateway);

        OperationResponse response = await toolkit.PlanMetadataReleaseGitOpsAsync(
            BlockedPackage,
            CancellationToken.None);

        Assert.Empty(handler.CapturedRequests);
        Assert.Equal(MetadataChangeSetReadiness.Blocked, response.Status);

        Assert.NotNull(response.MetadataReleaseChangeSet);
        Assert.Equal(MetadataChangeSetReadiness.Blocked, response.MetadataReleaseChangeSet!.Readiness);

        Assert.NotNull(response.GitOpsPlan);
        GitOpsMetadataReleaseSummary summary = Assert.IsType<GitOpsMetadataReleaseSummary>(response.GitOpsPlan!.MetadataRelease);
        Assert.Equal("incompatible", summary.CompatibilityStatus);
        Assert.Equal(2, summary.BreakingChanges);
        Assert.NotEmpty(summary.BlockingReasons);

        // Blocking reasons surface in the plan findings and actions.
        Assert.Contains(response.Findings, finding => finding.StartsWith("Blocking:", StringComparison.Ordinal));
        Assert.Contains(response.Actions, action => action.StartsWith("Metadata release blocking reason:", StringComparison.Ordinal));
        Assert.Contains(response.Risks, risk => risk.Contains("blocked by the supplied compatibility report", StringComparison.Ordinal));

        // The single targeted environment is in scope.
        GitOpsEnvironmentPlan prod = Assert.Single(response.GitOpsPlan.Environments);
        Assert.Equal("prod", prod.Environment);
        Assert.Equal(GitOpsPlanner.MetadataTargetStatus.InScope, prod.MetadataTargetStatus);
    }

    [Fact]
    public async Task PlanMetadataReleaseGitOpsAsync_InvalidJson_ReturnsGracefulUnknownResponse()
    {
        TestHttpMessageHandler handler = new(_ => TestHttpMessageHandler.JsonOk(new { status = "ok" }));
        using HttpClient httpClient = new(handler)
        {
            Timeout = TimeSpan.FromSeconds(5)
        };
        using BackendGateway gateway = new(CreateBackendConfiguration(), httpClient);
        HonuaOperationsToolkit toolkit = new(CreateRuntime(), gateway);

        OperationResponse response = await toolkit.PlanMetadataReleaseGitOpsAsync(
            "{ not valid json",
            CancellationToken.None);

        Assert.Empty(handler.CapturedRequests);
        Assert.Equal(MetadataChangeSetReadiness.Unknown, response.Status);
        Assert.Null(response.MetadataReleaseChangeSet);
        Assert.Null(response.GitOpsPlan);
        Assert.Contains(response.Findings, finding => finding.StartsWith("Parse error:", StringComparison.Ordinal));
        Assert.Contains(response.ValidationChecks, check => check == "metadata-release-gitops-plan-read-only-no-backend-call");
    }

    [Fact]
    public async Task PlanMetadataReleaseGitOpsAsync_SameInputTwice_IsDeterministicAndBackendFree()
    {
        TestHttpMessageHandler handler = new(_ => TestHttpMessageHandler.JsonOk(new { status = "ok" }));
        using HttpClient httpClient = new(handler)
        {
            Timeout = TimeSpan.FromSeconds(5)
        };
        using BackendGateway gateway = new(CreateBackendConfiguration(), httpClient);
        HonuaOperationsToolkit toolkit = new(CreateRuntime(), gateway);

        OperationResponse first = await toolkit.PlanMetadataReleaseGitOpsAsync(ReadyPackage, CancellationToken.None);
        OperationResponse second = await toolkit.PlanMetadataReleaseGitOpsAsync(ReadyPackage, CancellationToken.None);

        Assert.Empty(handler.CapturedRequests);

        Assert.Equal(first.Status, second.Status);
        Assert.Equal(first.Summary, second.Summary);
        Assert.Equal(first.Findings, second.Findings);
        Assert.Equal(first.Actions, second.Actions);
        Assert.Equal(first.ValidationChecks, second.ValidationChecks);
        Assert.Equal(first.Risks, second.Risks);

        // The typed plan projection (including the metadata-release summary) is value-equal. The
        // summary's collection members are fresh List instances per run, so compare scalar fields and
        // flattened resource content rather than relying on record reference-equality of the lists.
        GitOpsMetadataReleaseSummary firstSummary = first.GitOpsPlan!.MetadataRelease!;
        GitOpsMetadataReleaseSummary secondSummary = second.GitOpsPlan!.MetadataRelease!;
        Assert.Equal(firstSummary.ReleasePackageId, secondSummary.ReleasePackageId);
        Assert.Equal(firstSummary.Readiness, secondSummary.Readiness);
        Assert.Equal(firstSummary.CompatibilityStatus, secondSummary.CompatibilityStatus);
        Assert.Equal(firstSummary.BreakingChanges, secondSummary.BreakingChanges);
        Assert.Equal(firstSummary.Warnings, secondSummary.Warnings);
        Assert.Equal(firstSummary.ScriptCoverage, secondSummary.ScriptCoverage);
        Assert.Equal(firstSummary.RollbackClassification, secondSummary.RollbackClassification);
        Assert.Equal(firstSummary.KnownGoodRevision, secondSummary.KnownGoodRevision);
        Assert.Equal(
            firstSummary.SemanticResources.Select(resource => $"{resource.Kind}/{resource.Name}/{resource.Action}"),
            secondSummary.SemanticResources.Select(resource => $"{resource.Kind}/{resource.Name}/{resource.Action}"));
        Assert.Equal(firstSummary.BlockingReasons, secondSummary.BlockingReasons);
        Assert.Equal(
            first.GitOpsPlan.Environments.Select(environment => $"{environment.Environment}|{environment.MetadataTargetStatus}"),
            second.GitOpsPlan.Environments.Select(environment => $"{environment.Environment}|{environment.MetadataTargetStatus}"));
    }

    private static OperationRuntime CreateRuntime(
        string gitOpsTool = "honua-gitops",
        ExecutionMode mode = ExecutionMode.Plan,
        ExecutionTier executionTier = ExecutionTier.Plan,
        string? deployTargetId = null)
    {
        return new OperationRuntime(
            mode,
            executionTier,
            gitOpsTool,
            AllowedEnvironments: ["dev", "staging", "prod"],
            TerraformRepository: "https://github.com/honua-io/honua-iac",
            TerraformRef: "main",
            TerraformLocalPath: "/tmp/honua-iac",
            TerraformDeploymentTargets: ["eks", "aks"],
            DeployTargetId: deployTargetId);
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
