namespace Honua.DevOps.Agent.Operations.RuntimeAdapters;

internal static class RuntimeAdapterRegistry
{
    private static readonly IRuntimeAdapter[] Adapters =
    [
        new AzureFunctionsRuntimeAdapter(),
        new AwsLambdaRuntimeAdapter(),
        new AksRuntimeAdapter(),
        new EksRuntimeAdapter(),
        new AwsEcsRuntimeAdapter(),
        new AzureContainerAppsRuntimeAdapter(),
        GpRuntimeAdapter.Default,
        AzureGpRuntimeAdapter.Default
    ];

    internal static IRuntimeAdapter Resolve(string target)
    {
        return Adapters.SingleOrDefault(adapter =>
                   adapter.Capability.Target.Equals(target, StringComparison.OrdinalIgnoreCase))
               ?? throw new InvalidOperationException(
                   $"Unsupported runtime adapter target `{target}`. Supported targets: azure-functions, lambda, aks, eks, ecs, aca, gp, azure-gp.");
    }

    internal static IReadOnlyList<IRuntimeAdapter> ResolveMany(IEnumerable<string> targets)
    {
        return targets
            .Where(target => !string.IsNullOrWhiteSpace(target))
            .Select(target => Resolve(target.Trim()))
            .DistinctBy(adapter => adapter.Capability.Target, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
