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
        JsonElement parametersRoot = root.TryGetProperty("target", out JsonElement target) ? target : root;
        if (parametersRoot.TryGetProperty("parameters", out JsonElement parameters)
            && parameters.TryGetProperty("rootProvisioningOperationId", out JsonElement parameter))
        {
            string? storedRoot = parameter.GetString();
            if (string.IsNullOrWhiteSpace(storedRoot) || (provisioningRoot is not null && provisioningRoot != storedRoot))
                throw new InvalidDataException("Conflicting server provisioning roots.");
            provisioningRoot = storedRoot;
        }
        return new(Text("operationId"), Text("operationInstanceId"), Text("proposalId"), Text("correlationId"),
            Text("auditId"), Text("executionId"), Text("jobId"), Text("providerOperationId"), provisioningRoot,
            Copy("evidenceRefs"), Copy("decisionAudit") ?? Copy("audit"), receipt);
    }

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
