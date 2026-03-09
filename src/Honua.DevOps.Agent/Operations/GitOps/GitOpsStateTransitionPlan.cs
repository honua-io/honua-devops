namespace Honua.DevOps.Agent.Operations.GitOps;

internal sealed record GitOpsStateTransitionPlan(
    string Operation,
    string Environment,
    bool Enabled,
    string Summary,
    string SuggestedCommand,
    IReadOnlyList<string> RequiredChecks);
