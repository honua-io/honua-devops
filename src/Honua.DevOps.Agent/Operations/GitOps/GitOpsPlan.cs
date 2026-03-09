namespace Honua.DevOps.Agent.Operations.GitOps;

internal sealed record GitOpsPlan(
    string Engine,
    string RequestedAction,
    string EffectiveAction,
    string ActualStateSource,
    string DiffSummary,
    string DriftSummary,
    string GateStatus,
    IReadOnlyList<string> SupportedOperations,
    IReadOnlyList<string> RequiredEvidence,
    IReadOnlyList<GitOpsEnvironmentPlan> Environments,
    IReadOnlyList<GitOpsStateTransitionPlan> StateTransitions);
