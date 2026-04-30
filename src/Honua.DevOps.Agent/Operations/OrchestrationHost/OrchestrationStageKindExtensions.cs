namespace Honua.DevOps.Agent.Operations.OrchestrationHost;

internal static class OrchestrationStageKindExtensions
{
    internal static string ToConfigValue(this OrchestrationStageKind stage)
    {
        return stage switch
        {
            OrchestrationStageKind.CaptureIntent => "capture-intent",
            OrchestrationStageKind.GroundCandidates => "ground-candidates",
            OrchestrationStageKind.Clarify => "clarify",
            OrchestrationStageKind.CompilePlan => "compile-plan",
            OrchestrationStageKind.ValidatePlan => "validate-plan",
            OrchestrationStageKind.DryRun => "dry-run",
            OrchestrationStageKind.Execute => "execute",
            OrchestrationStageKind.ComposeMap => "compose-map",
            OrchestrationStageKind.ComposeApp => "compose-app",
            OrchestrationStageKind.Publish => "publish",
            OrchestrationStageKind.ReturnResultPackage => "return-result-package",
            _ => throw new InvalidOperationException("Unsupported orchestration stage.")
        };
    }
}
