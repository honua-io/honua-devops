namespace Honua.DevOps.Agent.Operations.BugReport;

/// <summary>
/// A GitHub repository reference (<c>owner/name</c>) resolved from the
/// server-owned allowlist. Never constructed from event/customer data.
/// </summary>
internal sealed record RepoRef(string Owner, string Name)
{
    internal string FullName => $"{Owner}/{Name}";
}

/// <summary>
/// Server-owned mapping from a support/customer <c>component</c> classification
/// to the destination product repository. This is devops-owned configuration —
/// the customer- or support-reported classification is only ever a lookup key
/// here; it never names a repo directly. An unmapped component resolves to
/// nothing, and the adapter refuses to file (surfacing an audited error) rather
/// than guessing a destination.
/// </summary>
internal sealed class ComponentRepoAllowlist
{
    private readonly IReadOnlyDictionary<string, RepoRef> _map;

    internal ComponentRepoAllowlist(IReadOnlyDictionary<string, RepoRef> map)
    {
        // Keys are compared case-insensitively; a component's casing must not let a
        // caller slip past or duplicate a mapping.
        _map = map ?? throw new ArgumentNullException(nameof(map));
    }

    internal int Count => _map.Count;

    /// <summary>All mapped component keys (lower-cased), for banners/diagnostics.</summary>
    internal IEnumerable<string> Components => _map.Keys;

    /// <summary>
    /// Resolves a component to its destination repo. Returns false for an unknown,
    /// blank, or null component — the caller must NOT file in that case.
    /// </summary>
    internal bool TryResolve(string? component, out RepoRef repo)
    {
        if (!string.IsNullOrWhiteSpace(component)
            && _map.TryGetValue(component.Trim().ToLowerInvariant(), out RepoRef? resolved))
        {
            repo = resolved;
            return true;
        }

        repo = null!;
        return false;
    }

    /// <summary>
    /// Parses the allowlist from the <c>component=owner/repo,component2=owner/repo2</c>
    /// form used by <c>HONUA_DEVOPS_BUGREPORT_COMPONENT_MAP</c>. Empty/blank input
    /// yields an empty allowlist (default-safe: nothing is routable until the
    /// operator configures a mapping). Malformed entries throw so a typo cannot
    /// silently drop a route.
    /// </summary>
    internal static ComponentRepoAllowlist Parse(string? raw, string variableName = "HONUA_DEVOPS_BUGREPORT_COMPONENT_MAP")
    {
        Dictionary<string, RepoRef> map = new(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new ComponentRepoAllowlist(map);
        }

        foreach (string entry in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int separator = entry.IndexOf('=');
            if (separator <= 0 || separator == entry.Length - 1)
            {
                throw new InvalidOperationException(
                    $"Environment variable `{variableName}` entry `{entry}` must be `component=owner/repo`.");
            }

            string component = entry[..separator].Trim().ToLowerInvariant();
            string repoToken = entry[(separator + 1)..].Trim();

            string[] repoParts = repoToken.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (repoParts.Length != 2)
            {
                throw new InvalidOperationException(
                    $"Environment variable `{variableName}` entry `{entry}` must map to a `owner/repo` value.");
            }

            if (map.ContainsKey(component))
            {
                throw new InvalidOperationException(
                    $"Environment variable `{variableName}` maps component `{component}` more than once.");
            }

            map[component] = new RepoRef(repoParts[0], repoParts[1]);
        }

        return new ComponentRepoAllowlist(map);
    }
}
