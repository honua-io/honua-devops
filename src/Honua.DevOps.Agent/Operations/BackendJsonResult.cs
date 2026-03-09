using System.Text.Json;

namespace Honua.DevOps.Agent.Operations;

internal sealed record BackendJsonResult(
    BackendCallResult CallResult,
    JsonDocument? Payload) : IDisposable
{
    public void Dispose()
    {
        Payload?.Dispose();
    }
}
