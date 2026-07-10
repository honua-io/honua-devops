using Honua.DevOps.Agent.Operations;
using Honua.DevOps.Agent.Operations.Observability;

namespace Honua.DevOps.Agent.Tests;

public class LiveHonuaIntegrationTests
{
    [Fact]
    public async Task LiveHonua_AdminReadContractsAreReachableWhenEnabled()
    {
        if (!LiveIntegrationEnabled())
        {
            return;
        }

        using BackendGateway gateway = new(BackendConfiguration.Load());

        BackendCallResult preflight = await gateway.RequestDeployPreflightAsync(
            includeDiagnostics: true,
            CancellationToken.None);
        Assert.True(preflight.IsSuccess, $"{preflight.Detail}: {preflight.PayloadPreview}");

        using BackendJsonResult capabilities = await gateway.GetCapabilitySnapshotAsync(CancellationToken.None);
        Assert.True(
            capabilities.CallResult.IsSuccess,
            $"{capabilities.CallResult.Detail}: {capabilities.CallResult.PayloadPreview}");

        using BackendJsonResult manifest = await gateway.ExportManifestSnapshotAsync(CancellationToken.None);
        Assert.True(
            manifest.CallResult.IsSuccess,
            $"{manifest.CallResult.Detail}: {manifest.CallResult.PayloadPreview}");
    }

    [Fact]
    public async Task LiveHonua_DeployPlanUsesConfiguredTargetWhenEnabled()
    {
        if (!LiveIntegrationEnabled())
        {
            return;
        }

        OperationRuntime runtime = OperationRuntime.Load();
        if (string.IsNullOrWhiteSpace(runtime.DeployTargetId))
        {
            return;
        }

        using BackendGateway gateway = new(BackendConfiguration.Load());
        string? desiredRevision = Environment.GetEnvironmentVariable("HONUA_DEVOPS_LIVE_DESIRED_REVISION")?.Trim();
        if (string.IsNullOrWhiteSpace(desiredRevision))
        {
            desiredRevision = "honua-devops-live-test";
        }

        BackendCallResult plan = await gateway.PlanDeployOperationAsync(
            runtime.DeployTargetId,
            desiredRevision,
            currentRevision: null,
            new Dictionary<string, string>
            {
                ["source"] = "honua-devops-live-test",
                ["dryRun"] = "true"
            },
            CancellationToken.None);

        Assert.True(plan.IsSuccess, $"{plan.Detail}: {plan.PayloadPreview}");
    }

    [Fact]
    public async Task LiveHonua_McpOpsReadLoopIsReachableWhenEnabled()
    {
        if (!LiveIntegrationEnabled())
        {
            return;
        }

        using BackendGateway gateway = new(BackendConfiguration.Load());
        OpsObserveDiagnoseProposeLoop loop = new(OperationRuntime.Load(), gateway);

        OpsLoopReport report = await loop.RunAsync(
            findingId: string.Empty,
            severity: string.Empty,
            rule: string.Empty,
            lookbackHours: 1,
            pageSize: 10,
            proposeRecommendedAction: false,
            CancellationToken.None);

        Assert.NotEqual("observability-unavailable", report.Status);
        Assert.Equal("honua-server-mcp", report.ObservabilitySource);
        Assert.Contains("honua_ops_health", report.McpToolsUsed);
        Assert.Contains("honua_ops_findings", report.McpToolsUsed);
        Assert.Contains("honua_alert_events", report.McpToolsUsed);
        Assert.Contains("honua_operate_events", report.McpToolsUsed);
    }

    private static bool LiveIntegrationEnabled()
    {
        return string.Equals(
            Environment.GetEnvironmentVariable("HONUA_DEVOPS_LIVE_INTEGRATION"),
            "true",
            StringComparison.OrdinalIgnoreCase);
    }
}
