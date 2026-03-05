namespace Honua.DevOps.Agent.Operations;

internal sealed record OperationRuntime(
    ExecutionMode ExecutionMode,
    string GitOpsTool,
    string[] AllowedEnvironments,
    string TerraformRepository,
    string TerraformRef,
    string TerraformLocalPath,
    string[] TerraformDeploymentTargets)
{
    private static readonly string[] DefaultEnvironments = ["dev", "staging", "prod"];

    private const string ExecutionModeVariable = "HONUA_DEVOPS_EXECUTION_MODE";
    private const string GitOpsToolVariable = "HONUA_DEVOPS_GITOPS_TOOL";
    private const string EnvironmentsVariable = "HONUA_DEVOPS_ALLOWED_ENVIRONMENTS";
    private const string TerraformRepositoryVariable = "HONUA_DEVOPS_TERRAFORM_REPO";
    private const string TerraformRefVariable = "HONUA_DEVOPS_TERRAFORM_REF";
    private const string TerraformTargetsVariable = "HONUA_DEVOPS_TERRAFORM_TARGETS";
    private const string TerraformLocalPathVariable = "HONUA_DEVOPS_TERRAFORM_LOCAL_PATH";

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

        string terraformLocalPath = ResolveTerraformLocalPath(
            Environment.GetEnvironmentVariable(TerraformLocalPathVariable));

        string[] terraformTargets = ParseTerraformTargets(
            Environment.GetEnvironmentVariable(TerraformTargetsVariable),
            terraformLocalPath);

        return new OperationRuntime(
            mode,
            gitOpsTool,
            environments,
            terraformRepository,
            terraformRef,
            terraformLocalPath,
            terraformTargets);
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
            return DefaultEnvironments;
        }

        string[] parsed = value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (parsed.Length == 0)
        {
            throw new InvalidOperationException(
                $"Environment variable `{EnvironmentsVariable}` parsed to an empty environment list.");
        }

        string[] invalid = parsed
            .Where(environment => !IsValidEnvironmentName(environment))
            .ToArray();
        if (invalid.Length > 0)
        {
            throw new InvalidOperationException(
                $"Environment variable `{EnvironmentsVariable}` contains invalid environment names: {string.Join(", ", invalid)}.");
        }

        return parsed;
    }

    private static string ResolveTerraformLocalPath(string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return configuredPath.Trim();
        }

        string siblingPath = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "..", "honua-terraform"));
        return siblingPath;
    }

    private static string[] ParseTerraformTargets(string? value, string terraformLocalPath)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            string[] parsed = value
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return parsed.Length == 0
                ? ["azure-functions", "lambda", "eks", "aks", "ecs", "aca"]
                : parsed;
        }

        string[] discovered = DiscoverTerraformTargets(terraformLocalPath);
        return discovered.Length == 0
            ? ["azure-functions", "lambda", "eks", "aks", "ecs", "aca"]
            : discovered;
    }

    private static string[] DiscoverTerraformTargets(string terraformLocalPath)
    {
        string modulesPath = Path.Combine(terraformLocalPath, "infrastructure", "terraform", "modules");
        if (!Directory.Exists(modulesPath))
        {
            return [];
        }

        HashSet<string> moduleNames = Directory
            .EnumerateDirectories(modulesPath)
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        List<string> discovered = [];
        if (moduleNames.Contains("azure-functions"))
        {
            discovered.Add("azure-functions");
        }

        if (moduleNames.Contains("aws-serverless") || moduleNames.Contains("lambda"))
        {
            discovered.Add("lambda");
        }

        if (moduleNames.Contains("aws-eks") || moduleNames.Contains("eks"))
        {
            discovered.Add("eks");
        }

        if (moduleNames.Contains("azure-aks") || moduleNames.Contains("aks"))
        {
            discovered.Add("aks");
        }

        if (moduleNames.Contains("aws-ecs") || moduleNames.Contains("ecs"))
        {
            discovered.Add("ecs");
        }

        if (moduleNames.Contains("azure-aca") || moduleNames.Contains("aca"))
        {
            discovered.Add("aca");
        }

        return discovered.ToArray();
    }

    private static bool IsValidEnvironmentName(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 40)
        {
            return false;
        }

        if (!char.IsLetterOrDigit(value[0]))
        {
            return false;
        }

        return value.All(character =>
            char.IsLetterOrDigit(character) ||
            character is '-' or '_');
    }
}
