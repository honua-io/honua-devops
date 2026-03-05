namespace Honua.DevOps.Agent.Operations;

internal sealed record BackendCallResult(
    bool IsSuccess,
    string Endpoint,
    string Detail,
    string PayloadPreview);
