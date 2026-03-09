namespace Honua.DevOps.Agent.Operations.RuntimeAdapters;

internal interface IRuntimeAdapter
{
    RuntimeAdapterCapability Capability { get; }

    RuntimeAdapterStepPlan Validate(RuntimeAdapterRequest request);

    RuntimeAdapterStepPlan PlanInfrastructure(RuntimeAdapterRequest request);

    RuntimeAdapterStepPlan ApplyInfrastructure(RuntimeAdapterRequest request);

    RuntimeAdapterStepPlan PlanRelease(RuntimeAdapterRequest request);

    RuntimeAdapterStepPlan ApplyRelease(RuntimeAdapterRequest request);

    RuntimeAdapterStepPlan Verify(RuntimeAdapterRequest request);

    RuntimeAdapterStepPlan Rollback(RuntimeAdapterRequest request);

    RuntimeAdapterStepPlan DetectDrift(RuntimeAdapterRequest request);

    RuntimeAdapterStepPlan ExportActualState(RuntimeAdapterRequest request);
}
