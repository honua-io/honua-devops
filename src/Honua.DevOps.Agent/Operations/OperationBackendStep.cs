namespace Honua.DevOps.Agent.Operations;

internal sealed record OperationBackendStep(
    string Name,
    string Endpoint,
    bool Success,
    string Detail,
    string PayloadPreview,
    bool MutatesState)
{
    /// <summary>
    /// Projects a <see cref="BackendCallResult"/> into a named
    /// <see cref="OperationBackendStep"/>. Single source of truth for this mapping,
    /// which was previously hand-copied across the operations toolkit, the Console
    /// bridge, and the GitOps executor (audit #118).
    /// </summary>
    internal static OperationBackendStep From(string name, BackendCallResult result, bool mutatesState)
        => new(
            Name: name,
            Endpoint: result.Endpoint,
            Success: result.IsSuccess,
            Detail: result.Detail,
            PayloadPreview: result.PayloadPreview,
            MutatesState: mutatesState);
}
