namespace Honua.DevOps.Agent.Operations;

internal sealed record OperationBackendStep(
    string Name,
    string Endpoint,
    bool Success,
    string Detail,
    string PayloadPreview,
    bool MutatesState);
