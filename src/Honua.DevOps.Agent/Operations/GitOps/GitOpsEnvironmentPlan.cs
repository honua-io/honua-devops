namespace Honua.DevOps.Agent.Operations.GitOps;

internal sealed record GitOpsEnvironmentPlan(
    string Environment,
    string ActualRevision,
    string DesiredRevision,
    string DiffStatus,
    string GateStatus,
    IReadOnlyList<GitOpsDriftStatus> Drift,
    IReadOnlyList<GitOpsCommandPlan> Commands);
