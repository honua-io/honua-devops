namespace Honua.DevOps.Agent.Operations.RuntimeAdapters;

internal static class RuntimeAdapterCatalog
{
    internal static IReadOnlyList<RuntimeAdapterCapability> ResolveMany(IEnumerable<string> targets)
    {
        return RuntimeAdapterRegistry.ResolveMany(targets)
            .Select(adapter => adapter.Capability)
            .ToArray();
    }

    internal static RuntimeAdapterCapability Resolve(string target)
    {
        return RuntimeAdapterRegistry.Resolve(target).Capability;
    }

    internal static RuntimeAdapterCapability BuildServerlessCapability(string target)
    {
        return new RuntimeAdapterCapability(
            Target: target,
            Family: "serverless",
            SupportsInfraPlanning: true,
            SupportsInfraApply: true,
            SupportsReleasePlanning: true,
            SupportsReleaseApply: true,
            SupportsVerify: true,
            SupportsRollback: true,
            SupportsDrift: true,
            SupportsActualStateExport: true,
            RequiresOutOfBandMigrations: true,
            SupportsTrafficShifting: true);
    }

    internal static RuntimeAdapterCapability BuildKubernetesCapability(string target)
    {
        return new RuntimeAdapterCapability(
            Target: target,
            Family: "kubernetes",
            SupportsInfraPlanning: true,
            SupportsInfraApply: true,
            SupportsReleasePlanning: true,
            SupportsReleaseApply: true,
            SupportsVerify: true,
            SupportsRollback: true,
            SupportsDrift: true,
            SupportsActualStateExport: true,
            RequiresOutOfBandMigrations: false,
            SupportsTrafficShifting: false);
    }

    internal static RuntimeAdapterCapability BuildManagedContainerCapability(string target)
    {
        return new RuntimeAdapterCapability(
            Target: target,
            Family: "managed-container",
            SupportsInfraPlanning: true,
            SupportsInfraApply: true,
            SupportsReleasePlanning: true,
            SupportsReleaseApply: true,
            SupportsVerify: true,
            SupportsRollback: true,
            SupportsDrift: true,
            SupportsActualStateExport: true,
            RequiresOutOfBandMigrations: false,
            SupportsTrafficShifting: true);
    }
}
