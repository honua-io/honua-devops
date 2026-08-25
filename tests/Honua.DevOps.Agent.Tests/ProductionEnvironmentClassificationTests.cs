using Honua.DevOps.Agent.Operations;

namespace Honua.DevOps.Agent.Tests;

public class ProductionEnvironmentClassificationTests
{
    [Theory]
    [InlineData("prod")]
    [InlineData("Prod")]
    [InlineData("production")]
    [InlineData("PRODUCTION")]
    [InlineData("prd")]
    [InlineData("prod-eu")]
    [InlineData("eu_prod")]
    [InlineData("prod1")]
    public void IsProductionEnvironment_DetectsProductionAliases(string environment)
    {
        Assert.True(DeploymentInputs.IsProductionEnvironment(environment));
    }

    [Theory]
    [InlineData("dev")]
    [InlineData("staging")]
    [InlineData("stage")]
    [InlineData("qa")]
    [InlineData("test")]
    public void IsProductionEnvironment_DoesNotFlagLowerEnvironments(string environment)
    {
        Assert.False(DeploymentInputs.IsProductionEnvironment(environment));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void IsProductionEnvironment_FailsClosedOnMissingEnvironment(string? environment)
    {
        // An unknown/blank environment must be treated as production so a malformed
        // target cannot be silently downgraded to a lower-env write.
        Assert.True(DeploymentInputs.IsProductionEnvironment(environment));
    }

    [Fact]
    public void IsProductionEnvironment_HonorsConfiguredCustomProductionName()
    {
        string[] configured = ["dev", "staging", "live"];
        Assert.True(DeploymentInputs.IsProductionEnvironment("live", configured));
        // Not matched by the heuristic, and only counts because it is configured.
        Assert.False(DeploymentInputs.IsProductionEnvironment("live"));
    }

    [Fact]
    public void OperationRuntime_IsProductionEnvironment_UsesDefaultsWhenUnconfigured()
    {
        OperationRuntime runtime = new(
            ExecutionMode.Plan,
            ExecutionTier.Plan,
            "honua-gitops",
            AllowedEnvironments: ["dev", "staging", "production"],
            TerraformRepository: "https://github.com/honua-io/honua-iac",
            TerraformRef: "main",
            TerraformLocalPath: "/tmp/honua-iac",
            TerraformDeploymentTargets: ["eks"]);

        Assert.True(runtime.IsProductionEnvironment("production"));
        Assert.False(runtime.IsProductionEnvironment("staging"));
    }
}
