namespace Honua.DevOps.Agent.Operations.RuntimeAdapters;

internal static class RuntimeAdapterExtensions
{
    internal static RuntimeAdapterWorkflow BuildWorkflow(this IRuntimeAdapter adapter, RuntimeAdapterRequest request)
    {
        return new RuntimeAdapterWorkflow(
            Capability: adapter.Capability,
            Validate: adapter.Validate(request),
            PlanInfrastructure: adapter.PlanInfrastructure(request),
            ApplyInfrastructure: adapter.ApplyInfrastructure(request),
            PlanRelease: adapter.PlanRelease(request),
            ApplyRelease: adapter.ApplyRelease(request),
            Verify: adapter.Verify(request),
            Rollback: adapter.Rollback(request),
            DetectDrift: adapter.DetectDrift(request),
            ExportActualState: adapter.ExportActualState(request));
    }
}
