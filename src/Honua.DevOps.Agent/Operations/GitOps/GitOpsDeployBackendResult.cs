using System.Text.Json;

namespace Honua.DevOps.Agent.Operations.GitOps;

internal sealed record GitOpsDeployBackendResult(
    BackendCallResult ApplyResult,
    BackendCallResult ExportResult,
    BackendCallResult CapabilitiesResult,
    BackendCallResult CombinedResult,
    JsonDocument? ExportPayload,
    JsonDocument? CapabilitiesPayload,
    BackendCallResult? DeployPreflightResult = null,
    BackendCallResult? DeployPlanResult = null,
    BackendCallResult? DeployOperationResult = null,
    BackendCallResult? DeployOperationStatusResult = null,
    BackendCallResult? ManifestDriftResult = null) : IDisposable
{
    public void Dispose()
    {
        ExportPayload?.Dispose();
        CapabilitiesPayload?.Dispose();
    }
}
