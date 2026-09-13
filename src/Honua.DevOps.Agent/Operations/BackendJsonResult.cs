using System.Text.Json;

namespace Honua.DevOps.Agent.Operations;

internal sealed record BackendJsonResult(
    BackendCallResult CallResult,
    JsonDocument? Payload,
    [property: System.Text.Json.Serialization.JsonIgnore] byte[]? EvidenceBytes = null) : IDisposable
{
    public void Dispose()
    {
        Payload?.Dispose();
    }
}
