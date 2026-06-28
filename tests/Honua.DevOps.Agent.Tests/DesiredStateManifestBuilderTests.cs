using Honua.DevOps.Agent.Operations;
using Honua.DevOps.Agent.Operations.DesiredState;

namespace Honua.DevOps.Agent.Tests;

public class DesiredStateManifestBuilderTests
{
    private static ManifestApplyRequest Build(
        IReadOnlyList<string> environments,
        ExecutionTier tier)
    {
        return DesiredStateManifestBuilder.BuildGitOpsDeployRequest(
            service: "roads-api",
            environments: environments,
            revision: "release/2026.03",
            action: "sync",
            changeSummary: "deploy now",
            gitOpsTool: "argocd",
            terraformRepository: "honua-io/honua-iac",
            terraformRef: "main",
            deploymentTargets: new[] { "aks" },
            dryRun: true,
            executionMode: ExecutionMode.Plan,
            executionTier: tier,
            allowedEnvironments: environments);
    }

    [Fact]
    public void ExecutionPolicyRef_ResolvesToControlPlaneDefault_ForNonBreakGlassTiers()
    {
        // ExecutionTier is internal, so iterate the tiers inside the test body rather
        // than via [InlineData] (which would force an internal parameter on a public test).
        foreach (ExecutionTier tier in new[]
                 {
                     ExecutionTier.Observe,
                     ExecutionTier.Plan,
                     ExecutionTier.Propose,
                     ExecutionTier.ExecuteLowerEnv,
                     ExecutionTier.PromoteProd
                 })
        {
            ManifestApplyRequest request = Build(new[] { "staging" }, tier);

            DesiredStateObjectReference reference = request.Resources[0].Spec.Relationships.ExecutionPolicyRef;
            Assert.Equal(DesiredStateApi.ExecutionPolicyDefaultName, reference.Name);
            Assert.Equal(DesiredStateApi.ControlPlaneNamespace, reference.Namespace);
            Assert.Equal(DesiredStateApi.ExecutionPolicyKind, reference.Kind);
        }
    }

    [Fact]
    public void ExecutionPolicyRef_ResolvesToBreakGlassObject_ForBreakGlassTier()
    {
        ManifestApplyRequest request = Build(new[] { "prod" }, ExecutionTier.BreakGlass);

        DesiredStateObjectReference reference = request.Resources[0].Spec.Relationships.ExecutionPolicyRef;
        Assert.Equal(DesiredStateApi.ExecutionPolicyBreakGlassName, reference.Name);
        Assert.Equal(DesiredStateApi.ControlPlaneNamespace, reference.Namespace);
    }

    [Fact]
    public void PromotionRef_NormalizesSourceEnvironmentSegment()
    {
        // Raw, un-normalized source ("Dev") must still produce the canonical
        // normalized Promotion name roads-api-dev-to-staging.
        ManifestApplyRequest request = Build(new[] { "Dev", "staging" }, ExecutionTier.ExecuteLowerEnv);

        DesiredStateObjectReference? promotionRef = request.Resources[1].Spec.Relationships.PromotionRef;
        Assert.NotNull(promotionRef);
        Assert.Equal("roads-api-dev-to-staging", promotionRef!.Name);
        Assert.Equal("staging", promotionRef.Namespace);
    }

    [Fact]
    public void PromotionRef_IsNullForFirstEnvironment()
    {
        ManifestApplyRequest request = Build(new[] { "staging" }, ExecutionTier.Plan);
        Assert.Null(request.Resources[0].Spec.Relationships.PromotionRef);
    }
}
