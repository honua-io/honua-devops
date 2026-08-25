using Honua.DevOps.Agent.Operations.Troubleshooting;

namespace Honua.DevOps.Agent.Operations.Eval;

/// <summary>
/// Resolves the <c>--eval-fault-set</c> selector to fault-catalog scenarios.
/// </summary>
internal static class BlindEvalFaultSet
{
    internal const string SmokeSet = "smoke";
    internal const string AllSet = "all";
    internal const string CategoryPrefix = "category:";

    /// <summary>
    /// The recurring lane's default: one scenario per distinct failure family across
    /// credentials, connectivity, cache, DNS, ingress, and rollout, so a scheduled run
    /// is bounded in provider calls while still spanning the catalog's shape.
    /// </summary>
    private static readonly string[] SmokeScenarioIds =
    [
        "FAULT-001",
        "FAULT-002",
        "FAULT-003",
        "FAULT-004",
        "FAULT-005",
        "FAULT-006"
    ];

    internal static IReadOnlyList<FaultScenario> Resolve(string selector)
    {
        if (string.IsNullOrWhiteSpace(selector))
        {
            throw new InvalidOperationException("Fault set selector must not be empty.");
        }

        string normalized = selector.Trim();

        if (normalized.Equals(AllSet, StringComparison.OrdinalIgnoreCase))
        {
            return FaultCatalog.All;
        }

        if (normalized.Equals(SmokeSet, StringComparison.OrdinalIgnoreCase))
        {
            return ResolveIds(SmokeScenarioIds, SmokeSet);
        }

        if (normalized.StartsWith(CategoryPrefix, StringComparison.OrdinalIgnoreCase))
        {
            string categoryValue = normalized[CategoryPrefix.Length..].Trim();
            FaultScenario[] byCategory = FaultCatalog.All
                .Where(scenario => scenario.Category.ToConfigValue()
                    .Equals(categoryValue, StringComparison.OrdinalIgnoreCase))
                .ToArray();

            if (byCategory.Length == 0)
            {
                throw new InvalidOperationException(
                    $"Fault set `{selector}` matched no scenarios. Unknown fault category `{categoryValue}`.");
            }

            return byCategory;
        }

        string[] ids = normalized
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (ids.Length == 0)
        {
            throw new InvalidOperationException($"Fault set `{selector}` matched no scenarios.");
        }

        return ResolveIds(ids, selector);
    }

    private static IReadOnlyList<FaultScenario> ResolveIds(IReadOnlyList<string> ids, string selector)
    {
        List<FaultScenario> scenarios = new(ids.Count);
        foreach (string id in ids)
        {
            FaultScenario scenario = FaultCatalog.Resolve(id)
                ?? throw new InvalidOperationException(
                    $"Fault set `{selector}` references unknown scenario id `{id}`.");
            scenarios.Add(scenario);
        }

        return scenarios;
    }
}
