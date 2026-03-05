namespace Honua.DevOps.Agent.Operations;

internal sealed record OperationResponse(
    string Status,
    string Summary,
    IReadOnlyList<string> Findings,
    IReadOnlyList<string> Actions,
    IReadOnlyList<string> ValidationChecks,
    IReadOnlyList<string> Risks);
