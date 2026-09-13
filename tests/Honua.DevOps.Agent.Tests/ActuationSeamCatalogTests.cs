using System.Reflection;

using Honua.DevOps.Agent.Operations;
using Honua.DevOps.Agent.Operations.Actuation;

namespace Honua.DevOps.Agent.Tests;

public sealed class ActuationSeamCatalogTests
{
    [Fact]
    public void EveryNonGatewayActuationSeamHasTheRequiredGovernanceBindings()
    {
        string[] expectedRoutes =
        [
            "install-handoff",
            "terraform-exact-apply",
            "terraform-exact-destroy",
            "verify-install-handoff"
        ];

        Assert.Equal(expectedRoutes, ActuationSeamCatalog.Routes.Keys.OrderBy(key => key, StringComparer.Ordinal));
        Assert.All(ActuationSeamCatalog.Routes.Values, seam =>
        {
            Assert.True(seam.HasDurableOperation, seam.Route);
            Assert.True(seam.RequiresVerification, seam.Route);
            Assert.True(seam.RequiresAudit, seam.Route);
            Assert.False(string.IsNullOrWhiteSpace(seam.Owner), seam.Route);
        });

        Assert.True(ActuationSeamCatalog.Routes["terraform-exact-apply"].RequiresApproval);
        Assert.True(ActuationSeamCatalog.Routes["terraform-exact-destroy"].RequiresApproval);
        Assert.False(ActuationSeamCatalog.Routes["install-handoff"].RequiresApproval);
        Assert.False(ActuationSeamCatalog.Routes["verify-install-handoff"].RequiresApproval);
    }

    [Fact]
    public void CatalogOwnersAreExecutableProductionSeams()
    {
        Assert.NotNull(typeof(HonuaOperationsToolkit).GetMethod(
            nameof(HonuaOperationsToolkit.InstallHandoffAsync),
            BindingFlags.Instance | BindingFlags.Public));
        Assert.NotNull(typeof(HonuaOperationsToolkit).GetMethod(
            nameof(HonuaOperationsToolkit.VerifyInstallHandoffAsync),
            BindingFlags.Instance | BindingFlags.Public));
        Assert.NotNull(typeof(HonuaOperationsToolkit).GetMethod(
            nameof(HonuaOperationsToolkit.ProvisionInfrastructureAsync),
            BindingFlags.Instance | BindingFlags.Public));

        Assert.NotNull(typeof(IProvisioningProcessRunner).GetMethod(nameof(IProvisioningProcessRunner.RunAsync)));
        Assert.NotNull(typeof(IInstallHandoffVerifier).GetMethod(nameof(IInstallHandoffVerifier.VerifyAsync)));
    }
}
