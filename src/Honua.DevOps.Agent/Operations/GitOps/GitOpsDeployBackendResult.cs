using System.Text.Json;

namespace Honua.DevOps.Agent.Operations.GitOps;

internal sealed record GitOpsDeployBackendResult(
    BackendCallResult ApplyResult,
    BackendCallResult ExportResult,
    BackendCallResult CapabilitiesResult,
    BackendCallResult CombinedResult,
    JsonDocument? ExportPayload,
    JsonDocument? CapabilitiesPayload) : IDisposable
{
    public void Dispose()
    {
        ExportPayload?.Dispose();
        CapabilitiesPayload?.Dispose();
    }
}
