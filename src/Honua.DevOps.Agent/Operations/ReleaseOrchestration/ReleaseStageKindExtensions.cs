namespace Honua.DevOps.Agent.Operations.ReleaseOrchestration;

internal static class ReleaseStageKindExtensions
{
    internal static string ToConfigValue(this ReleaseStageKind kind)
    {
        return kind switch
        {
            ReleaseStageKind.Preflight => "preflight",
            ReleaseStageKind.Backup => "backup",
            ReleaseStageKind.Migration => "migration",
            ReleaseStageKind.Rollout => "rollout",
            ReleaseStageKind.Smoke => "smoke",
            ReleaseStageKind.SloWatch => "slo-watch",
            ReleaseStageKind.Promote => "promote",
            ReleaseStageKind.Rollback => "rollback",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported release stage kind.")
        };
    }
}
