using Honua.DevOps.Agent.Operations;

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

    private static bool LiveIntegrationEnabled()
    {
        return string.Equals(
            Environment.GetEnvironmentVariable("HONUA_DEVOPS_LIVE_INTEGRATION"),
            "true",
            StringComparison.OrdinalIgnoreCase);
    }
}
