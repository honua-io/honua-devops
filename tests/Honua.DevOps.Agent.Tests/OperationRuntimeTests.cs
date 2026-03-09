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
            "HONUA_DEVOPS_TERRAFORM_LOCAL_PATH"
        ];

        foreach (string variableName in variableNames)
        {
            environment.Set(variableName, null);
        }
    }
}
