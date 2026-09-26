using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.DevOps.Agent.Operations;

/// <summary>Server-assigned identities, without fallback aliases. Receipt hashes cover the HTTP entity bytes.</summary>
internal sealed record ServerOperationLineage(
    [property: JsonPropertyName("operationId")] string? OperationId,
    [property: JsonPropertyName("operationInstanceId")] string? OperationInstanceId,
    [property: JsonPropertyName("proposalId")] string? ProposalId,
    [property: JsonPropertyName("correlationId")] string? CorrelationId,
    [property: JsonPropertyName("auditId")] string? AuditId,
    [property: JsonPropertyName("executionId")] string? ExecutionId,
    [property: JsonPropertyName("executionOperationId")] string? ExecutionOperationId,
    [property: JsonPropertyName("jobId")] string? JobId,
    [property: JsonPropertyName("providerOperationId")] string? ProviderOperationId,
    [property: JsonPropertyName("rootProvisioningOperationId")] string? RootProvisioningOperationId,
    [property: JsonPropertyName("evidenceRefs")] JsonElement? EvidenceRefs,
    [property: JsonPropertyName("decisionAudit")] JsonElement? DecisionAudit,
    [property: JsonPropertyName("receipt")] ProvisioningEvidenceReference Receipt,
    [property: JsonPropertyName("provisioningLineage")] ProvisioningLineage? ProvisioningLineage = null)
{
    internal static ServerOperationLineage Read(JsonElement document, ProvisioningEvidenceReference receipt)
    {
        RejectDuplicates(document);
        JsonElement root = document;
        foreach (string envelope in new[] { "operation", "data" })
            if (root.TryGetProperty(envelope, out JsonElement nested) && nested.ValueKind == JsonValueKind.Object)
                root = nested;
        string? Text(string name) => root.TryGetProperty(name, out JsonElement value) && value.ValueKind != JsonValueKind.Null
            ? value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
                ? value.GetString() : throw new InvalidDataException($"Invalid server identity: {name}.")
            : null;
        JsonElement? Copy(string name) => root.TryGetProperty(name, out JsonElement value) ? value.Clone() : null;
        string? provisioningRoot = Text("rootProvisioningOperationId");
        void TakeRoot(JsonElement container)
        {
            if (container.ValueKind != JsonValueKind.Object
                || !container.TryGetProperty("rootProvisioningOperationId", out JsonElement parameter))
                return;
            if (parameter.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(parameter.GetString()))
                throw new InvalidDataException("Conflicting server provisioning roots.");
            string storedRoot = parameter.GetString()!;
            if (provisioningRoot is not null && provisioningRoot != storedRoot)
                throw new InvalidDataException("Conflicting server provisioning roots.");
            provisioningRoot = storedRoot;
        }
        JsonElement parametersRoot = root.TryGetProperty("target", out JsonElement target) ? target : root;
        if (parametersRoot.TryGetProperty("parameters", out JsonElement parameters))
            TakeRoot(parameters);
        if (root.TryGetProperty("metadataRelease", out JsonElement metadataRelease))
            TakeRoot(metadataRelease);
        // executionOperationId is the finding-proposal actuator id. It is not operationId.
        return new(Text("operationId"), Text("operationInstanceId"), Text("proposalId"), Text("correlationId"),
            Text("auditId"), Text("executionId"), Text("executionOperationId"), Text("jobId"), Text("providerOperationId"),
            provisioningRoot, Copy("evidenceRefs"), Copy("decisionAudit") ?? Copy("audit"), receipt);
    }

    internal static void RejectDuplicateFields(JsonElement element) => RejectDuplicates(element);

    private static void RejectDuplicates(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            HashSet<string> names = new(StringComparer.Ordinal);
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("Duplicate server evidence field.");
                RejectDuplicates(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (JsonElement item in element.EnumerateArray()) RejectDuplicates(item);
    }
}
