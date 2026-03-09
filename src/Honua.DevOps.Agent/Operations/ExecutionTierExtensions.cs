namespace Honua.DevOps.Agent.Operations;

internal static class ExecutionTierExtensions
{
    internal static string ToConfigValue(this ExecutionTier tier)
    {
        return tier switch
        {
            ExecutionTier.Observe => "observe",
            ExecutionTier.Plan => "plan",
            ExecutionTier.Propose => "propose",
            ExecutionTier.ExecuteLowerEnv => "execute-lower-env",
            ExecutionTier.PromoteProd => "promote-prod",
            ExecutionTier.BreakGlass => "break-glass",
            _ => throw new ArgumentOutOfRangeException(nameof(tier), tier, "Unsupported execution tier.")
        };
    }
}
