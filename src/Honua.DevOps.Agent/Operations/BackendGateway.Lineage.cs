namespace Honua.DevOps.Agent.Operations;

internal sealed partial class BackendGateway
{
    private IReadOnlyDictionary<string, string>? BindProvisioningRoot(IReadOnlyDictionary<string, string>? parameters)
    {
        string? root = configuration.RootProvisioningOperationId;
        if (string.IsNullOrWhiteSpace(root))
        {
            if (parameters?.ContainsKey("rootProvisioningOperationId") == true)
                throw new InvalidDataException("Provisioning root must come from the configured verified handoff.");
            return parameters;
        }
        HonuaOperationsToolkit.LoadVerifiedLineage(root, configuration.HonuaApiBaseUri);
        Dictionary<string, string> bound = parameters is null ? [] : new(parameters);
        if (bound.TryGetValue("rootProvisioningOperationId", out string? supplied) && supplied != root)
            throw new InvalidDataException("The request substitutes a different provisioning root.");
        bound["rootProvisioningOperationId"] = root;
        return bound;
    }

    private async Task<BackendJsonResult> CaptureServerOperationAsync(Task<BackendJsonResult> pending, string? expectedOperationId = null)
    {
        BackendJsonResult result = await pending;
        if (!result.CallResult.IsSuccess || result.Payload is null || result.EvidenceBytes is null) return result;
        try
        {
            ProvisioningEvidenceStore store = new(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "honua-devops", "provisioning", "evidence"));
            // This is a protected evidence replica, not an operation/proposal store.
            ProvisioningEvidenceReference receipt = store.Put("server-operation", result.EvidenceBytes);
            ServerOperationLineage lineage = ServerOperationLineage.Read(result.Payload.RootElement, receipt);
            if (string.IsNullOrWhiteSpace(lineage.OperationId)
                || (expectedOperationId is not null && expectedOperationId != lineage.OperationId))
                throw new InvalidDataException("The server did not return the requested canonical operation identity.");
            string? configuredRoot = configuration.RootProvisioningOperationId;
            if (!string.IsNullOrWhiteSpace(configuredRoot) && lineage.RootProvisioningOperationId != configuredRoot)
                throw new InvalidDataException("The server did not retain the configured provisioning root; federation is unproven.");
            if (lineage.RootProvisioningOperationId is { } root)
                lineage = lineage with { ProvisioningLineage = HonuaOperationsToolkit.LoadVerifiedLineage(root, configuration.HonuaApiBaseUri) };
            return result with { CallResult = result.CallResult with { ServerLineage = lineage } };
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException)
        {
            return result with { CallResult = result.CallResult with
            {
                IsSuccess = false,
                Detail = "lineage-evidence-invalid: the operation may already exist; reconcile using the same idempotency key. " + exception.Message
            } };
        }
    }
}
