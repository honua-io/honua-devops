namespace Honua.DevOps.Agent.Operations;

internal enum ExecutionTier
{
    Observe,
    Plan,
    Propose,
    ExecuteLowerEnv,
    PromoteProd,
    BreakGlass
}
