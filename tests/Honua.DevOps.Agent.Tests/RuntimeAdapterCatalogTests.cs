using Honua.DevOps.Agent.Operations;
using Honua.DevOps.Agent.Operations.RuntimeAdapters;

namespace Honua.DevOps.Agent.Tests;

public sealed class RuntimeAdapterCatalogTests
{
    [Fact]
    public void ResolveMany_ReturnsExpectedFamilies()
    {
        IReadOnlyList<RuntimeAdapterCapability> adapters = RuntimeAdapterCatalog.ResolveMany(
            ["azure-functions", "eks", "aca"]);

        Assert.Collection(
            adapters,
            adapter =>
            {
                Assert.Equal("azure-functions", adapter.Target);
                Assert.Equal("serverless", adapter.Family);
                Assert.True(adapter.RequiresOutOfBandMigrations);
            },
            adapter =>
            {
                Assert.Equal("eks", adapter.Target);
                Assert.Equal("kubernetes", adapter.Family);
                Assert.False(adapter.SupportsTrafficShifting);
            },
            adapter =>
            {
                Assert.Equal("aca", adapter.Target);
                Assert.Equal("managed-container", adapter.Family);
                Assert.True(adapter.SupportsTrafficShifting);
            });
    }

    [Fact]
    public void Resolve_RejectsUnknownTarget()
    {
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => RuntimeAdapterCatalog.Resolve("nomad"));

        Assert.Contains("Unsupported runtime adapter target", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildWorkflow_ExposesSharedLifecycleOperations()
    {
        IRuntimeAdapter adapter = RuntimeAdapterRegistry.Resolve("eks");
        RuntimeAdapterRequest request = new(
            Service: "roads-api",
            Environments: ["dev", "staging"],
            Revision: "main",
            Action: "plan",
            ChangeSummary: "release candidate",
            GitOpsTool: "honua-gitops",
            TerraformRepository: "https://github.com/honua-io/honua-terraform",
            TerraformRef: "main",
            TerraformLocalPath: "/tmp/honua-terraform",
            DryRun: true,
            ExecutionMode: ExecutionMode.Plan,
            ExecutionTier: ExecutionTier.Plan);

        RuntimeAdapterWorkflow workflow = adapter.BuildWorkflow(request);

        Assert.Equal("validate", workflow.Validate.Operation);
        Assert.Equal("plan-infra", workflow.PlanInfrastructure.Operation);
        Assert.Equal("plan-release", workflow.PlanRelease.Operation);
        Assert.Equal("verify", workflow.Verify.Operation);
        Assert.Equal("rollback", workflow.Rollback.Operation);
        Assert.Contains("helm upgrade --install", workflow.PlanRelease.SuggestedCommands.First(), StringComparison.Ordinal);
    }
}
