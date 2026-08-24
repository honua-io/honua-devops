namespace Honua.DevOps.Agent.Operations;

internal sealed record OperationRuntime(
    ExecutionMode ExecutionMode,
    ExecutionTier ExecutionTier,
    string GitOpsTool,
    string[] AllowedEnvironments,
    string TerraformRepository,
    string TerraformRef,
    string TerraformLocalPath,
    string[] TerraformDeploymentTargets,
    string? DeployTargetId = null,
    string[]? ProductionEnvironments = null,
    bool RollbackEnabled = false,
    bool CrossEnvironmentPromotionEnabled = false)
{
    private static readonly string[] DefaultEnvironments = ["dev", "staging", "prod"];
    private static readonly string[] DefaultProductionEnvironments = ["prod", "production", "prd"];

    /// <summary>
    /// True when the supplied environment is production under the configured
    /// production-environment names plus the built-in fail-closed heuristic. Used
    /// by the deployment authorizer so production aliases (e.g. <c>production</c>)
    /// cannot evade the production execution tiers/guards.
    /// </summary>
    internal bool IsProductionEnvironment(string? environment)
        => DeploymentInputs.IsProductionEnvironment(
            environment,
            ProductionEnvironments is { Length: > 0 } configured ? configured : DefaultProductionEnvironments);

    private const string ExecutionModeVariable = "HONUA_DEVOPS_EXECUTION_MODE";
    private const string ExecutionTierVariable = "HONUA_DEVOPS_EXECUTION_TIER";
    private const string GitOpsToolVariable = "HONUA_DEVOPS_GITOPS_TOOL";
    private const string EnvironmentsVariable = "HONUA_DEVOPS_ALLOWED_ENVIRONMENTS";
    private const string TerraformRepositoryVariable = "HONUA_DEVOPS_TERRAFORM_REPO";
    private const string TerraformRefVariable = "HONUA_DEVOPS_TERRAFORM_REF";
    private const string TerraformTargetsVariable = "HONUA_DEVOPS_TERRAFORM_TARGETS";
    private const string TerraformLocalPathVariable = "HONUA_DEVOPS_TERRAFORM_LOCAL_PATH";
    private const string DeployTargetIdVariable = "HONUA_DEVOPS_DEPLOY_TARGET_ID";
    private const string ProductionEnvironmentsVariable = "HONUA_DEVOPS_PRODUCTION_ENVIRONMENTS";

    // Release posture: rollback + cross-environment promotion are EXPERIMENTAL and OFF for the
    // MVP release. The operate story is single-environment deploy with health-gated fix-forward
    // convergence; rollback/auto-rollback and cross-environment promotion are post-release. The
    // code is retained but not advertised/actuated unless these flags are explicitly enabled.
    internal const string RollbackEnabledVariable = "HONUA_DEVOPS_EXPERIMENTAL_ROLLBACK";
    internal const string CrossEnvironmentPromotionEnabledVariable = "HONUA_DEVOPS_EXPERIMENTAL_CROSS_ENV_PROMOTION";

    /// <summary>
    /// Fail-closed runtime: plan mode, observe tier, no deploy target. Used where a runtime
    /// is optional so that omitting it can only ever REMOVE authority, never grant it.
    /// </summary>
    internal static OperationRuntime SafeDefault { get; } = new(
        ExecutionMode: ExecutionMode.Plan,
        ExecutionTier: ExecutionTier.Observe,
        GitOpsTool: "honua-gitops",
        AllowedEnvironments: DefaultEnvironments,
        TerraformRepository: "honua-iac",
        TerraformRef: "main",
        TerraformLocalPath: string.Empty,
        TerraformDeploymentTargets: []);

    internal static OperationRuntime Load()
    {
        ExecutionMode mode = ParseExecutionMode(
            Environment.GetEnvironmentVariable(ExecutionModeVariable));
        ExecutionTier tier = ParseExecutionTier(
            Environment.GetEnvironmentVariable(ExecutionTierVariable),
            mode);

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
            terraformRepository = "https://github.com/honua-io/honua-iac";
        }

        string? terraformRef = Environment.GetEnvironmentVariable(TerraformRefVariable)?.Trim();
        if (string.IsNullOrWhiteSpace(terraformRef))
        {
            terraformRef = "trunk";
        }

        string terraformLocalPath = ResolveTerraformLocalPath(
            Environment.GetEnvironmentVariable(TerraformLocalPathVariable));

        string[] terraformTargets = ParseTerraformTargets(
            Environment.GetEnvironmentVariable(TerraformTargetsVariable),
            terraformLocalPath);
        string? deployTargetId = NormalizeOptionalIdentifier(
            Environment.GetEnvironmentVariable(DeployTargetIdVariable),
            DeployTargetIdVariable);

        string[] productionEnvironments = ParseProductionEnvironments(
            Environment.GetEnvironmentVariable(ProductionEnvironmentsVariable));

        bool rollbackEnabled = ParseExperimentalFlag(
            Environment.GetEnvironmentVariable(RollbackEnabledVariable));
        bool crossEnvironmentPromotionEnabled = ParseExperimentalFlag(
            Environment.GetEnvironmentVariable(CrossEnvironmentPromotionEnabledVariable));

        return new OperationRuntime(
            mode,
            tier,
            gitOpsTool,
            environments,
            terraformRepository,
            terraformRef,
            terraformLocalPath,
            terraformTargets,
            deployTargetId,
            productionEnvironments,
            rollbackEnabled,
            crossEnvironmentPromotionEnabled);
    }

    /// <summary>
    /// Parses an experimental capability flag. Default-OFF: only an explicit truthy value
    /// (<c>true</c>/<c>1</c>/<c>yes</c>/<c>on</c>, case-insensitive) enables the capability.
    /// Any other value (including unset, empty, or <c>false</c>) leaves it disabled.
    /// </summary>
    private static bool ParseExperimentalFlag(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "true" or "1" or "yes" or "on" or "enabled" => true,
            _ => false
        };
    }

    private static string[] ParseProductionEnvironments(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return DefaultProductionEnvironments;
        }

        string[] parsed = value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return parsed.Length == 0 ? DefaultProductionEnvironments : parsed;
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

    private static ExecutionTier ParseExecutionTier(string? value, ExecutionMode mode)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return mode == ExecutionMode.Execute
                ? ExecutionTier.ExecuteLowerEnv
                : ExecutionTier.Plan;
        }

        string normalized = value.Trim().ToLowerInvariant();
        return normalized switch
        {
            "observe" => ExecutionTier.Observe,
            "plan" => ExecutionTier.Plan,
            "propose" => ExecutionTier.Propose,
            "execute-lower-env" or "execute_lower_env" or "lower-env" => ExecutionTier.ExecuteLowerEnv,
            "promote-prod" or "promote_prod" => ExecutionTier.PromoteProd,
            "break-glass" or "break_glass" => ExecutionTier.BreakGlass,
            _ => throw new InvalidOperationException(
                $"Invalid `{ExecutionTierVariable}` value `{value}`. Allowed values: observe, plan, propose, execute-lower-env, promote-prod, break-glass.")
        };
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

        string siblingPath = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "..", "honua-iac"));
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

    private static string? NormalizeOptionalIdentifier(string? value, string variableName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string trimmed = value.Trim();
        if (trimmed.Length > 128)
        {
            throw new InvalidOperationException(
                $"Environment variable `{variableName}` must be 128 characters or fewer.");
        }

        if (!char.IsLetterOrDigit(trimmed[0]))
        {
            throw new InvalidOperationException(
                $"Environment variable `{variableName}` must start with a letter or digit.");
        }

        if (trimmed.Any(character =>
                !(char.IsLetterOrDigit(character) || character is '-' or '_' or '.' or ':' or '/')))
        {
            throw new InvalidOperationException(
                $"Environment variable `{variableName}` contains invalid characters.");
        }

        return trimmed;
    }
}
