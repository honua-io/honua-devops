namespace Honua.DevOps.Agent.Operations.GitOps;

internal sealed record GitOpsCommandPlan(
    string Operation,
    string Summary,
    string Command,
    bool RequiresApproval);
