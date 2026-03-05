namespace Honua.DevOps.Agent.Operations;

internal sealed record OperationRuntime(
    ExecutionMode ExecutionMode,
    string GitOpsTool,
    string[] AllowedEnvironments,
    string TerraformRepository,
    string TerraformRef,
    string[] TerraformDeploymentTargets)
{
    private const string ExecutionModeVariable = "HONUA_DEVOPS_EXECUTION_MODE";
    private const string GitOpsToolVariable = "HONUA_DEVOPS_GITOPS_TOOL";
    private const string EnvironmentsVariable = "HONUA_DEVOPS_ALLOWED_ENVIRONMENTS";
    private const string TerraformRepositoryVariable = "HONUA_DEVOPS_TERRAFORM_REPO";
    private const string TerraformRefVariable = "HONUA_DEVOPS_TERRAFORM_REF";
    private const string TerraformTargetsVariable = "HONUA_DEVOPS_TERRAFORM_TARGETS";

    internal static OperationRuntime Load()
    {
        ExecutionMode mode = ParseExecutionMode(
            Environment.GetEnvironmentVariable(ExecutionModeVariable));

        string? gitOpsTool = Environment.GetEnvironmentVariable(GitOpsToolVariable)?.Trim();
        if (string.IsNullOrWhiteSpace(gitOpsTool))
        {
            gitOpsTool = "honua-gitops";
        }

        string[] environments = ParseEnvironments(
            Environment.GetEnvironmentVariable(EnvironmentsVariable));

        string? terraformRepository = Environment.GetEnvironmentVariable(TerraformRepositoryVariable)?.Trim();
        if (string.IsNullOrWhiteSpace(terraformRepository))
        {
            terraformRepository = "https://github.com/honua-io/honua-terraform";
        }

        string? terraformRef = Environment.GetEnvironmentVariable(TerraformRefVariable)?.Trim();
        if (string.IsNullOrWhiteSpace(terraformRef))
        {
            terraformRef = "main";
        }

        string[] terraformTargets = ParseTerraformTargets(
            Environment.GetEnvironmentVariable(TerraformTargetsVariable));

        return new OperationRuntime(mode, gitOpsTool, environments, terraformRepository, terraformRef, terraformTargets);
    }

    private static ExecutionMode ParseExecutionMode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return ExecutionMode.Plan;
        }

        if (value.Equals("plan", StringComparison.OrdinalIgnoreCase))
        {
            return ExecutionMode.Plan;
        }

        if (value.Equals("execute", StringComparison.OrdinalIgnoreCase))
        {
            return ExecutionMode.Execute;
        }

        throw new InvalidOperationException(
            $"Invalid `{ExecutionModeVariable}` value `{value}`. Allowed values: plan, execute.");
    }

    private static string[] ParseEnvironments(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return ["dev", "staging", "prod"];
        }

        return value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string[] ParseTerraformTargets(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return ["azure-functions", "lambda", "eks", "aks", "ecs", "aca"];
        }

        string[] parsed = value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return parsed.Length == 0
            ? ["azure-functions", "lambda", "eks", "aks", "ecs", "aca"]
            : parsed;
    }
}
