using System.Text.Json;

using Honua.DevOps.Agent.Operations;
using Honua.DevOps.Agent.Operations.RuntimeAdapters;
using Honua.DevOps.Agent.Operations.ServiceBundleReconciliation;

namespace Honua.DevOps.Agent.Tests;

// Proves the ServiceBundleReconciliation drift detection is real (drift-detected / no-drift /
// unreconcilable) and no longer the "check-required" / "export-required" stub.
public class ServiceBundleDriftDetectionTests
{
    [Fact]
    public void Build_RevisionMismatch_YieldsDriftDetected()
    {
        using JsonDocument export = ExportWith("roads-api", "dev", "release/2026.02");
        ServiceBundleReconciliationPlan plan = Build(export, desiredRevision: "release/2026.03");

        ServiceBundleDriftScope scope = ServiceState(plan);
        Assert.Equal("drift-detected", scope.DriftVerdict);
        Assert.Contains("release/2026.02", scope.DriftDetail, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_RevisionMatches_YieldsNoDrift()
    {
        using JsonDocument export = ExportWith("roads-api", "dev", "release/2026.03");
        ServiceBundleReconciliationPlan plan = Build(export, desiredRevision: "release/2026.03");

        Assert.Equal("no-drift", ServiceState(plan).DriftVerdict);
    }

    [Fact]
    public void Build_NoActualRevisionButDesiredAndExportCapableAdapters_YieldsDriftDetected()
    {
        // Export has no comparable revision for the service; with a desired revision and
        // export-capable adapters the missing actual state is treated as drift.
        using JsonDocument export = ExportWith("other-service", "dev", "release/2026.01");
        ServiceBundleReconciliationPlan plan = Build(export, desiredRevision: "release/2026.03");

        Assert.Equal("drift-detected", ServiceState(plan).DriftVerdict);
    }

    [Fact]
    public void Build_NoAdaptersResolved_FallsBackToIndeterminateNotNoDrift()
    {
        // The drift-unaware overload (no adapters, no desired revision) must not claim no-drift.
        ServiceBundleReconciliationPlan plan = ServiceBundleReconciliationPlanner.Build(
            "roads-api",
            ["dev"],
            CreateBackendConfiguration(),
            capabilitiesPayload: null,
            capabilitiesPreview: string.Empty,
            exportPayload: null,
            exportPreview: string.Empty);

        Assert.Equal("drift-indeterminate", ServiceState(plan).DriftVerdict);
    }

    private static ServiceBundleReconciliationPlan Build(JsonDocument export, string desiredRevision)
    {
        IReadOnlyList<RuntimeAdapterWorkflow> adapters = RuntimeAdapterRegistry
            .ResolveMany(["eks", "aks"])
            .Select(adapter => adapter.BuildWorkflow(new RuntimeAdapterRequest(
                Service: "roads-api",
                Environments: ["dev"],
                Revision: desiredRevision,
                Action: "sync",
                ChangeSummary: "drift test",
                GitOpsTool: "honua-gitops",
                TerraformRepository: "https://github.com/honua-io/honua-iac",
                TerraformRef: "main",
                TerraformLocalPath: "/tmp/honua-iac",
                DryRun: true,
                ExecutionMode: ExecutionMode.Plan,
                ExecutionTier: ExecutionTier.Plan)))
            .ToArray();

        return ServiceBundleReconciliationPlanner.Build(
            "roads-api",
            ["dev"],
            CreateBackendConfiguration(),
            capabilitiesPayload: null,
            capabilitiesPreview: string.Empty,
            exportPayload: export,
            exportPreview: string.Empty,
            adapters,
            desiredRevision);
    }

    private static ServiceBundleDriftScope ServiceState(ServiceBundleReconciliationPlan plan)
        => plan.DriftScopes.Single(scope => scope.Scope == "service-state");

    private static JsonDocument ExportWith(string service, string environment, string revision)
        => JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            items = new object[]
            {
                new
                {
                    metadata = new { @namespace = environment },
                    spec = new { deployment = new { service, environment, revision } }
                }
            }
        }));

    private static BackendConfiguration CreateBackendConfiguration()
        => new(
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
