namespace Honua.DevOps.Agent.Operations;

internal sealed record OperationRuntime(
    ExecutionMode ExecutionMode,
    string GitOpsTool,
    string[] AllowedEnvironments)
{
    private const string ExecutionModeVariable = "HONUA_DEVOPS_EXECUTION_MODE";
    private const string GitOpsToolVariable = "HONUA_DEVOPS_GITOPS_TOOL";
    private const string EnvironmentsVariable = "HONUA_DEVOPS_ALLOWED_ENVIRONMENTS";

    internal static OperationRuntime Load()
    {
        ExecutionMode mode = ParseExecutionMode(
            Environment.GetEnvironmentVariable(ExecutionModeVariable));

        string? gitOpsTool = Environment.GetEnvironmentVariable(GitOpsToolVariable)?.Trim();
        if (string.IsNullOrWhiteSpace(gitOpsTool))
        {
            gitOpsTool = "flux";
        }

        string[] environments = ParseEnvironments(
            Environment.GetEnvironmentVariable(EnvironmentsVariable));

        return new OperationRuntime(mode, gitOpsTool, environments);
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
}
