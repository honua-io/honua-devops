using System.Text.Json;
using Honua.DevOps.Agent.Operations.Eval;

namespace Honua.DevOps.Agent.Operations;

/// <summary>
/// The <c>honua.operator-contract/v1</c> projection honua-iac#148 publishes from a
/// provisioned stack, read from the three authoritative Terraform outputs.
/// </summary>
/// <remarks>
/// <para>
/// This type is the reason the endpoint and the admin-key secret reference stopped
/// being caller arguments. Before it, <c>install_handoff</c> took whatever base URL
/// and secret ARN a caller passed and scraped a single scalar <c>honua_url</c>
/// output — so the binding recorded what the caller *said* was deployed, not what
/// the stack *reported*. The contract is the stack's own statement of its endpoint,
/// its secret locators, and the state/backend/identity that produced them.
/// </para>
/// <para>
/// <c>operator_contract</c> is a non-normative convenience envelope in the stack;
/// the three per-kind outputs are authoritative, so those are what is read, and
/// their <c>identity</c> blocks are required to agree.
/// </para>
/// </remarks>
internal sealed record OperatorContract(
    string SchemaVersion,
    string Status,
    string ContractDigest,
    string Endpoint,
    string AdminKeySecretRef,
    string? McpPath,
    string? ReadinessPath,
    string IacRoot,
    string ModuleSource,
    string? IacRevision,
    string? TerraformVersion,
    string? ProviderLockDigest,
    string? BackendConfigDigest,
    string? StateLineage,
    long? StateSerial,
    string? WorkloadIdentity,
    string AccountId,
    string Region,
    string Partition,
    string? ImageDigest)
{
    internal const string ExpectedSchemaVersion = "honua.operator-contract/v1";

    private static readonly string[] ContractOutputNames = ["deployment_contract", "validation_contract", "operations_contract"];

    /// <summary>
    /// Parses and validates the contract from a <c>terraform output -json</c>
    /// document, against the schema shipped by the same honua-iac checkout.
    /// </summary>
    /// <param name="terraformOutputJson">Raw stdout of <c>terraform output -json</c>.</param>
    /// <param name="schemaJson">Contents of <c>operator-contract.v1.schema.json</c>.</param>
    internal static bool TryRead(
        string terraformOutputJson,
        string schemaJson,
        out OperatorContract? contract,
        out string error)
    {
        contract = null;
        error = string.Empty;

        JsonDocument outputs;
        try
        {
            outputs = JsonDocument.Parse(terraformOutputJson);
        }
        catch (JsonException exception)
        {
            error = $"Terraform outputs were not valid JSON: {exception.Message}";
            return false;
        }

        using (outputs)
        {
            if (outputs.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = "Terraform outputs were not a JSON object.";
                return false;
            }

            Dictionary<string, JsonElement> contracts = new(StringComparer.Ordinal);
            foreach (string name in ContractOutputNames)
            {
                if (!TryReadOutputValue(outputs.RootElement, name, out JsonElement value)
                    || value.ValueKind != JsonValueKind.Object)
                {
                    error = $"The stack does not project the `{name}` output. This root emits no "
                        + $"`{ExpectedSchemaVersion}` projection, so no endpoint or secret reference can be "
                        + "sourced from it; honua-devops does not fall back to scraping a scalar URL output.";
                    return false;
                }

                contracts[name] = value;
            }

            // The document the schema describes is the envelope over the three
            // contracts. Assemble it so validation covers all three at once.
            using MemoryStream buffer = new();
            using (Utf8JsonWriter writer = new(buffer))
            {
                writer.WriteStartObject();
                writer.WriteString("schema_version", ExpectedSchemaVersion);
                foreach (string name in ContractOutputNames)
                {
                    writer.WritePropertyName(name);
                    contracts[name].WriteTo(writer);
                }

                writer.WriteEndObject();
            }

            string envelopeJson = System.Text.Encoding.UTF8.GetString(buffer.ToArray());

            IReadOnlyList<string> schemaErrors;
            try
            {
                schemaErrors = JsonSchemaValidator.Validate(envelopeJson, schemaJson);
            }
            catch (JsonException exception)
            {
                error = $"The operator-contract schema shipped by the honua-iac checkout is not valid JSON: {exception.Message}";
                return false;
            }

            if (schemaErrors.Count > 0)
            {
                error = "The operator contract does not satisfy `operator-contract.v1.schema.json`: "
                    + string.Join("; ", schemaErrors.Take(8));
                return false;
            }

            JsonElement deployment = contracts["deployment_contract"];
            JsonElement operations = contracts["operations_contract"];
            JsonElement identity = deployment.GetProperty("identity");

            // Every kind must describe the same deployment. A mismatched identity means
            // outputs were assembled from different applies and nothing downstream can
            // be trusted to describe one stack.
            string contractDigest = ReadString(identity, "contract_digest") ?? string.Empty;
            foreach (string name in ContractOutputNames)
            {
                JsonElement other = contracts[name].GetProperty("identity");
                if (!string.Equals(ReadString(other, "contract_digest"), contractDigest, StringComparison.Ordinal)
                    || !string.Equals(ReadString(other, "state_lineage"), ReadString(identity, "state_lineage"), StringComparison.Ordinal)
                    || !string.Equals(ReadString(other, "backend_config_digest"), ReadString(identity, "backend_config_digest"), StringComparison.Ordinal))
                {
                    error = $"The `{name}` identity does not agree with the deployment contract identity; "
                        + "the three outputs do not describe one apply.";
                    return false;
                }
            }

            // The digest output is the stack's own statement of the contract identity.
            // Disagreement means an output was edited in transit.
            if (TryReadOutputValue(outputs.RootElement, "operator_contract_digest", out JsonElement digestOutput)
                && digestOutput.ValueKind == JsonValueKind.String
                && !string.Equals(digestOutput.GetString(), contractDigest, StringComparison.Ordinal))
            {
                error = "The `operator_contract_digest` output does not match `identity.contract_digest`.";
                return false;
            }

            string status = ReadString(identity, "status") ?? "unqualified";
            JsonElement endpoints = deployment.GetProperty("endpoints");
            string? endpoint = ReadString(endpoints, "public_base_url");
            if (string.IsNullOrWhiteSpace(endpoint))
            {
                error = "The deployment contract carries no `endpoints.public_base_url`; the stack has no endpoint to hand off.";
                return false;
            }

            if (!deployment.GetProperty("secret_refs").TryGetProperty("admin_password", out JsonElement adminSecret)
                || adminSecret.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(adminSecret.GetString()))
            {
                error = "The deployment contract carries no `secret_refs.admin_password` locator.";
                return false;
            }

            // deployment.secret_refs and operations.secrets.references must agree for
            // shared keys; disagreement means the handoff would resolve a different
            // secret than day-2 operations reads.
            if (operations.TryGetProperty("secrets", out JsonElement secrets)
                && secrets.TryGetProperty("references", out JsonElement references)
                && references.TryGetProperty("admin_password", out JsonElement operationsAdmin)
                && operationsAdmin.TryGetProperty("id", out JsonElement operationsAdminId)
                && operationsAdminId.ValueKind == JsonValueKind.String
                && !string.Equals(operationsAdminId.GetString(), adminSecret.GetString(), StringComparison.Ordinal))
            {
                error = "`deployment_contract.secret_refs.admin_password` and "
                    + "`operations_contract.secrets.references.admin_password.id` name different secrets.";
                return false;
            }

            JsonElement platform = identity.GetProperty("platform");
            contract = new OperatorContract(
                SchemaVersion: ExpectedSchemaVersion,
                Status: status,
                ContractDigest: contractDigest,
                Endpoint: endpoint!.Trim(),
                AdminKeySecretRef: adminSecret.GetString()!.Trim(),
                McpPath: ReadString(endpoints, "mcp_path"),
                ReadinessPath: ReadString(endpoints, "readiness_path"),
                IacRoot: ReadString(identity, "iac_root") ?? string.Empty,
                ModuleSource: ReadString(identity, "module_source") ?? string.Empty,
                IacRevision: ReadString(identity, "iac_revision"),
                TerraformVersion: ReadString(identity, "terraform_version"),
                ProviderLockDigest: ReadString(identity, "provider_lock_digest"),
                BackendConfigDigest: ReadString(identity, "backend_config_digest"),
                StateLineage: ReadString(identity, "state_lineage"),
                StateSerial: ReadInt64(identity, "state_serial"),
                WorkloadIdentity: ReadString(identity, "workload_identity"),
                AccountId: ReadString(platform, "account_id") ?? string.Empty,
                Region: ReadString(platform, "region") ?? string.Empty,
                Partition: ReadString(platform, "partition") ?? string.Empty,
                ImageDigest: ReadString(identity, "image_digest"));
            return true;
        }
    }

    /// <summary>
    /// True when the stack reports every immutable pin a release consumer requires.
    /// The contract itself says certified consumers must reject an unqualified
    /// contract, so this gates the handoff rather than merely annotating it.
    /// </summary>
    internal bool IsQualified => string.Equals(Status, "qualified", StringComparison.Ordinal);

    private static bool TryReadOutputValue(JsonElement outputs, string name, out JsonElement value)
    {
        value = default;
        if (!outputs.TryGetProperty(name, out JsonElement output))
        {
            return false;
        }

        // `terraform output -json` wraps each output as {value, type, sensitive}.
        if (output.ValueKind == JsonValueKind.Object && output.TryGetProperty("value", out JsonElement wrapped))
        {
            value = wrapped;
            return true;
        }

        value = output;
        return true;
    }

    private static string? ReadString(JsonElement element, string name)
        => element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long? ReadInt64(JsonElement element, string name)
        => element.TryGetProperty(name, out JsonElement value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out long parsed)
            ? parsed
            : null;
}
