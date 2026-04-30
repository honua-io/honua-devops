namespace Honua.DevOps.Agent.Operations.GitOps;

internal sealed record GitOpsStateTransitionPlan(
    string Operation,
    string Environment,
    string FromState,
    string ToState,
    bool MutatesState,
    bool RequiresApproval,
    bool Enabled,
    string Summary,
    string SuggestedCommand,
    IReadOnlyList<string> RequiredChecks);
