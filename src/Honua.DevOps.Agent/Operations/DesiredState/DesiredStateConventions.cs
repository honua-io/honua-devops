namespace Honua.DevOps.Agent.Operations.DesiredState;

// Shared naming and runtime-target rules sourced from desired-state/conventions.env.
// Kept as a typed record so the drift detector validates against the same
// contract the scaffold and validation scripts use, rather than hard-coded
// strings scattered across the codebase.
internal sealed record DesiredStateConventions(
    IReadOnlyList<string> AllowedRuntimeTargets,
    string ControlPlaneNamespace,
    string PlatformStackPrefix,
    string ExecutionPolicyDefaultName,
    string ExecutionPolicyBreakGlassName,
    string PlatformReleaseNameTemplate,
    string PromotionNameTemplate,
    string ServiceBundleNameTemplate)
{
    internal static DesiredStateConventions Parse(string conventionsFileText)
    {
        Dictionary<string, string> values = conventionsFileText
            .Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Trim())
            .Where(line => !string.IsNullOrWhiteSpace(line) && !line.StartsWith('#'))
            .Select(line =>
            {
                int separator = line.IndexOf('=');
                if (separator <= 0)
                {
                    throw new InvalidOperationException($"Invalid conventions line `{line}`.");
                }

                return new KeyValuePair<string, string>(
                    line[..separator].Trim(),
                    line[(separator + 1)..].Trim());
            })
            .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);

        return new DesiredStateConventions(
            AllowedRuntimeTargets: RequireList(values, "ALLOWED_RUNTIME_TARGETS"),
            ControlPlaneNamespace: Require(values, "CONTROL_PLANE_NAMESPACE"),
            PlatformStackPrefix: Require(values, "PLATFORM_STACK_PREFIX"),
            ExecutionPolicyDefaultName: Require(values, "EXECUTION_POLICY_DEFAULT_NAME"),
            ExecutionPolicyBreakGlassName: Require(values, "EXECUTION_POLICY_BREAK_GLASS_NAME"),
            PlatformReleaseNameTemplate: Require(values, "PLATFORM_RELEASE_NAME_TEMPLATE"),
            PromotionNameTemplate: Require(values, "PROMOTION_NAME_TEMPLATE"),
            ServiceBundleNameTemplate: Require(values, "SERVICE_BUNDLE_NAME_TEMPLATE"));
    }

    internal string RenderPlatformReleaseName(string service, string environment, string revision) =>
        ApplyTemplate(
            PlatformReleaseNameTemplate,
            ("service", NormalizeToken(service)),
            ("environment", environment),
            ("revision", NormalizeToken(revision)));

    internal string RenderPromotionName(string service, string source, string target) =>
        ApplyTemplate(
            PromotionNameTemplate,
            ("service", NormalizeToken(service)),
            ("source", source),
            ("target", target));

    internal string RenderServiceBundleName(string service, string environment) =>
        ApplyTemplate(
            ServiceBundleNameTemplate,
            ("service", NormalizeToken(service)),
            ("environment", environment));

    internal static string NormalizeToken(string value)
    {
        string normalized = System.Text.RegularExpressions.Regex
            .Replace(value.Trim().ToLowerInvariant(), "[^a-z0-9]+", "-")
            .Trim('-');

        return string.IsNullOrWhiteSpace(normalized) ? "item" : normalized;
    }

    private static string ApplyTemplate(string template, params (string Key, string Value)[] replacements)
    {
        string rendered = template;
        foreach ((string key, string value) in replacements)
        {
            rendered = rendered.Replace($"{{{key}}}", value, StringComparison.Ordinal);
        }

        return rendered;
    }

    private static IReadOnlyList<string> RequireList(IReadOnlyDictionary<string, string> values, string key) =>
        Require(values, key)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToArray();

    private static string Require(IReadOnlyDictionary<string, string> values, string key)
    {
        if (!values.TryGetValue(key, out string? value) || string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"Missing or empty desired-state convention `{key}`.");
        }

        return value;
    }
}
