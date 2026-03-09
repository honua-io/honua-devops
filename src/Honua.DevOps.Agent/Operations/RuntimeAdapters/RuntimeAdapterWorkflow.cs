namespace Honua.DevOps.Agent.Operations.RuntimeAdapters;

internal sealed record RuntimeAdapterWorkflow(
    RuntimeAdapterCapability Capability,
    RuntimeAdapterStepPlan Validate,
    RuntimeAdapterStepPlan PlanInfrastructure,
    RuntimeAdapterStepPlan ApplyInfrastructure,
    RuntimeAdapterStepPlan PlanRelease,
    RuntimeAdapterStepPlan ApplyRelease,
    RuntimeAdapterStepPlan Verify,
    RuntimeAdapterStepPlan Rollback,
    RuntimeAdapterStepPlan DetectDrift,
    RuntimeAdapterStepPlan ExportActualState);
