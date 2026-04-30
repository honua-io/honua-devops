namespace Honua.DevOps.Agent.Operations.OrchestrationHost;

internal enum OrchestrationStageKind
{
    CaptureIntent,
    GroundCandidates,
    Clarify,
    CompilePlan,
    ValidatePlan,
    DryRun,
    Execute,
    ComposeMap,
    ComposeApp,
    Publish,
    ReturnResultPackage
}
