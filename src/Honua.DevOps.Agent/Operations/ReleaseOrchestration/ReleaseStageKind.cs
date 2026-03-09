namespace Honua.DevOps.Agent.Operations.ReleaseOrchestration;

internal enum ReleaseStageKind
{
    Preflight,
    Backup,
    Migration,
    Rollout,
    Smoke,
    SloWatch,
    Promote,
    Rollback
}
