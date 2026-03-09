namespace Honua.DevOps.Agent.Operations.GitOps;

internal sealed record GitOpsDriftStatus(
    string Scope,
    string Status,
    string Detail);
