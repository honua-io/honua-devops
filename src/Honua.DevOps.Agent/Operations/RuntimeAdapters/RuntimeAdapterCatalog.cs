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

    internal static RuntimeAdapterCapability BuildGeoprocessingBatchCapability(string target)
    {
        // Per-ENVIRONMENT AWS Batch (Fargate-Spot) GP substrate provision. Infra IS the
        // deliverable: the durable substrate (compute-env, queue, IAM, ECR, and the pooled
        // s/m/l/xl job-definition tiers) is the "release", so there is no separate
        // release-backend deploy, no traffic shifting, and no out-of-band migration. The
        // substrate is provisioned once per env (rarely); per-job sizing is a SubmitJob-time
        // runtime concern. Rollback is a terraform-state revert (destroy / re-apply prior
        // substrate config), and drift is detected against the substrate shape.
        return new RuntimeAdapterCapability(
            Target: target,
            Family: "batch-job",
            SupportsInfraPlanning: true,
            SupportsInfraApply: true,
            SupportsReleasePlanning: false,
            SupportsReleaseApply: false,
            SupportsVerify: true,
            SupportsRollback: true,
            SupportsDrift: true,
            SupportsActualStateExport: true,
            RequiresOutOfBandMigrations: false,
            SupportsTrafficShifting: false);
    }
}
