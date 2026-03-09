namespace Honua.DevOps.Agent.Operations.RuntimeAdapters;

internal sealed record RuntimeAdapterStepPlan(
    string Operation,
    string Summary,
    IReadOnlyList<string> SuggestedCommands,
    IReadOnlyList<string> ValidationChecks,
    IReadOnlyList<string> Risks);
