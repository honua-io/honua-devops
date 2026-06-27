namespace Honua.DevOps.Agent.Operations;

// Shared deployment input validators. Extracted from HonuaOperationsToolkit so the
// Console bridge proposal path validates service, environments, revision, action, and
// free-text the same way DeployServiceWithGitOpsAsync does, without duplicating rules.
internal static class DeploymentInputs
{
    internal static string Normalize(string? value, string fallback)
    {
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }

    private static readonly char[] EnvironmentTokenSeparators = ['-', '_', '.', '/', ':', ' '];

    /// <summary>
    /// Classifies whether a target environment is production. Fails closed: an
    /// absent/blank environment is treated as production, and any environment whose
    /// configured name or token denotes production is matched, so a name such as
    /// <c>production</c>, <c>prod-eu</c>, or <c>eu_prod</c> can never slip past the
    /// production execution guards. An exact <see cref="StringComparer.OrdinalIgnoreCase"/>
    /// membership test (as used previously) only matched the literal token <c>prod</c>
    /// and let every other production alias evade the guard.
    /// </summary>
    /// <param name="environment">The candidate environment name.</param>
    /// <param name="productionEnvironments">
    /// Operator-configured production environment names; matched exactly in addition
    /// to the built-in <c>prod*</c>/<c>prd</c> heuristic.
    /// </param>
    internal static bool IsProductionEnvironment(
        string? environment,
        IReadOnlyCollection<string>? productionEnvironments = null)
    {
        // Fail closed: an unknown environment is treated as production so a missing
        // or malformed target cannot be silently downgraded to a lower-env write.
        if (string.IsNullOrWhiteSpace(environment))
        {
            return true;
        }

        string normalized = environment.Trim().ToLowerInvariant();

        if (productionEnvironments is not null)
        {
            foreach (string configured in productionEnvironments)
            {
                if (!string.IsNullOrWhiteSpace(configured)
                    && string.Equals(configured.Trim(), normalized, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        foreach (string token in normalized.Split(
            EnvironmentTokenSeparators,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (token.StartsWith("prod", StringComparison.Ordinal) || token == "prd")
            {
                return true;
            }
        }

        return false;
    }

    internal static string ValidateServiceName(string value)
    {
        string service = Normalize(value, string.Empty);
        if (service.Length is < 1 or > 80)
        {
            throw new InvalidOperationException("Service name must be 1-80 characters.");
        }

        if (!char.IsLetterOrDigit(service[0]))
        {
            throw new InvalidOperationException("Service name must start with a letter or digit.");
        }

        if (service.Any(character =>
                !(char.IsLetterOrDigit(character) || character is '-' or '_' or '.')))
        {
            throw new InvalidOperationException(
                "Service name contains invalid characters. Allowed characters: letters, numbers, '-', '_', '.'.");
        }

        return service;
    }

    internal static string ValidateRevision(string value, string fieldName)
    {
        string revision = Normalize(value, "HEAD");
        if (revision.Length is < 1 or > 128)
        {
            throw new InvalidOperationException($"{fieldName} must be 1-128 characters.");
        }

        if (revision.Any(char.IsWhiteSpace))
        {
            throw new InvalidOperationException($"{fieldName} must not contain whitespace.");
        }

        if (revision.Any(character =>
                !(char.IsLetterOrDigit(character) || character is '-' or '_' or '.' or '/' or '@' or ':')))
        {
            throw new InvalidOperationException(
                $"{fieldName} contains invalid characters.");
        }

        return revision;
    }

    internal static string ValidateAction(string value)
    {
        string normalized = Normalize(value, "sync").ToLowerInvariant();
        return normalized switch
        {
            "sync" => "sync",
            "apply" => "apply",
            "prune" => "prune",
            "dry-run" => "dry-run",
            "dryrun" => "dry-run",
            "plan" => "plan",
            "promote" => "promote",
            _ => throw new InvalidOperationException(
                $"Invalid deployment action `{value}`. Allowed values: sync, apply, prune, dry-run, plan, promote.")
        };
    }

    internal static string SanitizeFreeText(string? value, string fallback)
    {
        string normalized = Normalize(value, fallback);
        char[] filtered = normalized
            .Where(character => !char.IsControl(character) || character is '\r' or '\n' or '\t')
            .ToArray();
        string compact = new string(filtered).Trim();
        if (compact.Length == 0)
        {
            return fallback;
        }

        const int maxLength = 600;
        return compact.Length <= maxLength
            ? compact
            : compact[..maxLength];
    }

    internal static string[] ParseEnvironments(string value, IReadOnlyList<string> allowedEnvironments)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                "Deployment environments are required and must match the configured allowed environment list.");
        }

        string[] requested = value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (requested.Length == 0)
        {
            throw new InvalidOperationException(
                "Deployment environments are required and must not be empty.");
        }

        string[] invalid = requested
            .Where(item => !allowedEnvironments.Contains(item, StringComparer.OrdinalIgnoreCase))
            .ToArray();
        if (invalid.Length > 0)
        {
            throw new InvalidOperationException(
                $"Invalid deployment environments: {string.Join(", ", invalid)}. Allowed values: {string.Join(", ", allowedEnvironments)}.");
        }

        return requested;
    }
}
