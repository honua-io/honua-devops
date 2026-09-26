using System.Text.Json;

namespace Honua.DevOps.Agent.Operations;

internal enum ServerReceiptShape
{
    WorkflowOperation,
    FindingProposal
}

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

    private async Task<BackendJsonResult> CaptureServerOperationAsync(
        Task<BackendJsonResult> pending,
        string? expectedOperationId = null,
        bool isMutation = false,
        ServerReceiptShape shape = ServerReceiptShape.WorkflowOperation)
    {
        BackendJsonResult result = await pending;
        result = result with { CallResult = result.CallResult with { MutationAcknowledged = isMutation && result.CallResult.IsSuccess } };
        if (!result.CallResult.IsSuccess) return result;
        if (result.Payload is null || result.EvidenceBytes is null)
            return result with { CallResult = result.CallResult with
            {
                IsSuccess = false,
                Detail = "lineage-evidence-invalid: the server acknowledged the request without a readable operation receipt."
            } };
        try
        {
            ProvisioningEvidenceStore store = new(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "honua-devops", "provisioning", "evidence"));
            // This is a protected evidence replica, not an operation/proposal store.
            ProvisioningEvidenceReference receipt = store.Put("server-operation", result.EvidenceBytes);
            ServerOperationLineage lineage = ServerOperationLineage.Read(result.Payload.RootElement, receipt);
            if (shape == ServerReceiptShape.FindingProposal)
            {
                if (!TryClaimFinding(result.Payload.RootElement, lineage, out string? findingFailure))
                {
                    if (findingFailure is not null)
                        throw new InvalidDataException(findingFailure);
                    return result;
                }
            }
            else if (string.IsNullOrWhiteSpace(lineage.OperationId)
                || (expectedOperationId is not null && expectedOperationId != lineage.OperationId))
            {
                throw new InvalidDataException("The server did not return the requested canonical operation identity.");
            }
            string? configuredRoot = configuration.RootProvisioningOperationId;
            if (!string.IsNullOrWhiteSpace(configuredRoot) && lineage.RootProvisioningOperationId != configuredRoot)
                throw new InvalidDataException("The server did not retain the configured provisioning root; federation is unproven.");
            if (lineage.RootProvisioningOperationId is { } root)
                lineage = lineage with { ProvisioningLineage = HonuaOperationsToolkit.LoadVerifiedLineage(root, configuration.HonuaApiBaseUri) };
            return result with { CallResult = result.CallResult with { ServerLineage = lineage } };
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return result with { CallResult = result.CallResult with
            {
                IsSuccess = false,
                Detail = "lineage-evidence-invalid: the operation may already exist; reconcile using the same idempotency key. " + exception.Message
            } };
        }
    }

    // A finding proposal joins by proposalId and executionOperationId. Those are not copied
    // onto operationId. A blocked outcome with no server identity is not a lineage claim.
    private static bool TryClaimFinding(JsonElement payload, ServerOperationLineage lineage, out string? failure)
    {
        failure = null;
        string? status = payload.TryGetProperty("status", out JsonElement statusElement)
            && statusElement.ValueKind == JsonValueKind.String
            ? statusElement.GetString() : null;
        bool hasProposal = !string.IsNullOrWhiteSpace(lineage.ProposalId);
        bool hasExecution = !string.IsNullOrWhiteSpace(lineage.ExecutionOperationId);
        if (status == "ProposalCreated" && !hasProposal)
        {
            failure = "The server did not return the canonical proposal id.";
            return false;
        }
        if (status == "Executed" && !hasExecution)
        {
            failure = "The server did not return the canonical execution operation id.";
            return false;
        }
        if (status is "Failed" or "RolledBack" or "Indeterminate" or "Canceled" && !hasProposal && !hasExecution)
        {
            failure = "The server gateway outcome omitted proposal and execution ids.";
            return false;
        }
        return hasProposal || hasExecution
            || !string.IsNullOrWhiteSpace(lineage.OperationId)
            || !string.IsNullOrWhiteSpace(lineage.OperationInstanceId);
    }
}
