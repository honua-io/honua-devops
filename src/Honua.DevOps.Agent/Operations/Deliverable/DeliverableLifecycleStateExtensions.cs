namespace Honua.DevOps.Agent.Operations.Deliverable;

internal static class DeliverableLifecycleStateExtensions
{
    // Stable wire/config token for a lifecycle state. Mirrors the ToConfigValue()
    // style used by ReleaseStageKindExtensions / ApprovalModeExtensions so the
    // vocabulary stays consistent across the toolkit.
    internal static string ToConfigValue(this DeliverableLifecycleState state)
    {
        return state switch
        {
            DeliverableLifecycleState.Draft => "draft",
            DeliverableLifecycleState.Preview => "preview",
            DeliverableLifecycleState.Approved => "approved",
            DeliverableLifecycleState.Published => "published",
            _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Unsupported deliverable lifecycle state.")
        };
    }

    // Parse a config/wire token back into a state. Tolerant of case and surrounding
    // whitespace; never guesses an unknown token into a default.
    internal static bool TryParse(string? value, out DeliverableLifecycleState state)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "draft":
                state = DeliverableLifecycleState.Draft;
                return true;
            case "preview":
                state = DeliverableLifecycleState.Preview;
                return true;
            case "approved":
                state = DeliverableLifecycleState.Approved;
                return true;
            case "published":
                state = DeliverableLifecycleState.Published;
                return true;
            default:
                state = DeliverableLifecycleState.Draft;
                return false;
        }
    }

    // Parse or fall back to Draft. Used by the toolkit when the caller omits or
    // mistypes the current state — the lifecycle still plans from the safe start.
    internal static DeliverableLifecycleState ParseOrDraft(string? value)
        => TryParse(value, out DeliverableLifecycleState state) ? state : DeliverableLifecycleState.Draft;
}
