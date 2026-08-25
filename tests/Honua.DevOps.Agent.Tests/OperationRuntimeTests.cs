using Honua.DevOps.Agent.Operations;

namespace Honua.DevOps.Agent.Tests;

public class OperationRuntimeTests
{
    [Fact]
    public void Load_ThrowsWhenAllowedEnvironmentListParsesEmpty()
    {
        using TestEnvironmentVariableScope environment = new();
        ResetRuntimeVariables(environment);
        environment.Set("HONUA_DEVOPS_ALLOWED_ENVIRONMENTS", ", ,");

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(OperationRuntime.Load);

        Assert.Contains("HONUA_DEVOPS_ALLOWED_ENVIRONMENTS", exception.Message);
    }

    [Fact]
    public void Load_UsesDefaultAllowedEnvironmentsWhenUnset()
    {
        using TestEnvironmentVariableScope environment = new();
        ResetRuntimeVariables(environment);
        environment.Set("HONUA_DEVOPS_ALLOWED_ENVIRONMENTS", null);

        OperationRuntime runtime = OperationRuntime.Load();

        Assert.Equal(["dev", "staging", "prod"], runtime.AllowedEnvironments);
    }

    [Fact]
    public void Load_DefaultsPlanModeToPlanTier()
    {
        using TestEnvironmentVariableScope environment = new();
        ResetRuntimeVariables(environment);
        environment.Set("HONUA_DEVOPS_EXECUTION_MODE", "plan");
        environment.Set("HONUA_DEVOPS_EXECUTION_TIER", null);

        OperationRuntime runtime = OperationRuntime.Load();

        Assert.Equal(ExecutionTier.Plan, runtime.ExecutionTier);
    }

    [Fact]
    public void Load_DefaultsExecuteModeToExecuteLowerEnvTier()
    {
        using TestEnvironmentVariableScope environment = new();
        ResetRuntimeVariables(environment);
        environment.Set("HONUA_DEVOPS_EXECUTION_MODE", "execute");
        environment.Set("HONUA_DEVOPS_EXECUTION_TIER", null);

        OperationRuntime runtime = OperationRuntime.Load();

        Assert.Equal(ExecutionTier.ExecuteLowerEnv, runtime.ExecutionTier);
    }

    [Fact]
    public void Load_ParsesPromoteProdExecutionTier()
    {
        using TestEnvironmentVariableScope environment = new();
        ResetRuntimeVariables(environment);
        environment.Set("HONUA_DEVOPS_EXECUTION_TIER", "promote-prod");

        OperationRuntime runtime = OperationRuntime.Load();

        Assert.Equal(ExecutionTier.PromoteProd, runtime.ExecutionTier);
    }

    [Fact]
    public void Load_RejectsInvalidEnvironmentToken()
    {
        using TestEnvironmentVariableScope environment = new();
        ResetRuntimeVariables(environment);
        environment.Set("HONUA_DEVOPS_ALLOWED_ENVIRONMENTS", "dev,prod;rm");

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(OperationRuntime.Load);

        Assert.Contains("invalid environment names", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Load_ParsesDeployTargetIdForRealHonuaDeployControl()
    {
        using TestEnvironmentVariableScope environment = new();
        ResetRuntimeVariables(environment);
        environment.Set("HONUA_DEVOPS_DEPLOY_TARGET_ID", "prod-api:primary");

        OperationRuntime runtime = OperationRuntime.Load();

        Assert.Equal("prod-api:primary", runtime.DeployTargetId);
    }

    [Fact]
    public void Load_RejectsUnsafeDeployTargetId()
    {
        using TestEnvironmentVariableScope environment = new();
        ResetRuntimeVariables(environment);
        environment.Set("HONUA_DEVOPS_DEPLOY_TARGET_ID", "prod-api;rm");

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(OperationRuntime.Load);

        Assert.Contains("HONUA_DEVOPS_DEPLOY_TARGET_ID", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_DefaultsTerraformSourceToHonuaIac()
    {
        using TestEnvironmentVariableScope environment = new();
        ResetRuntimeVariables(environment);

        OperationRuntime runtime = OperationRuntime.Load();

        Assert.Equal("https://github.com/honua-io/honua-iac", runtime.TerraformRepository);
        Assert.Equal("trunk", runtime.TerraformRef);
        Assert.EndsWith(
            Path.DirectorySeparatorChar + "honua-iac",
            runtime.TerraformLocalPath,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Load_KeepsExplicitTerraformSourceOverrides()
    {
        using TestEnvironmentVariableScope environment = new();
        ResetRuntimeVariables(environment);
        environment.Set("HONUA_DEVOPS_TERRAFORM_REPO", "https://github.example/acme/infra");
        environment.Set("HONUA_DEVOPS_TERRAFORM_REF", "release/2026.1");
        environment.Set("HONUA_DEVOPS_TERRAFORM_LOCAL_PATH", "/srv/acme-infra");

        OperationRuntime runtime = OperationRuntime.Load();

        Assert.Equal("https://github.example/acme/infra", runtime.TerraformRepository);
        Assert.Equal("release/2026.1", runtime.TerraformRef);
        Assert.Equal("/srv/acme-infra", runtime.TerraformLocalPath);
    }

    private static void ResetRuntimeVariables(TestEnvironmentVariableScope environment)
    {
        string[] variableNames =
        [
            "HONUA_DEVOPS_EXECUTION_MODE",
            "HONUA_DEVOPS_EXECUTION_TIER",
            "HONUA_DEVOPS_GITOPS_TOOL",
            "HONUA_DEVOPS_ALLOWED_ENVIRONMENTS",
            "HONUA_DEVOPS_TERRAFORM_REPO",
            "HONUA_DEVOPS_TERRAFORM_REF",
            "HONUA_DEVOPS_TERRAFORM_TARGETS",
            "HONUA_DEVOPS_TERRAFORM_LOCAL_PATH",
            "HONUA_DEVOPS_DEPLOY_TARGET_ID"
        ];

        foreach (string variableName in variableNames)
        {
            environment.Set(variableName, null);
        }
    }
}
