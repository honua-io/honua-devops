using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.DevOps.Agent.Operations;

/// <summary>
/// The structured "what must be approved" projection a <c>provision_infrastructure
/// action=plan</c> response carries. It repeats the lineage identities and adds the
/// stack, environment and next action, so an issuer never has to recover them from the
/// summary prose or the confirmation challenge.
/// </summary>
internal sealed record ProvisionApprovalRequest(
    [property: JsonPropertyName("provisioningOperationId")] string ProvisioningOperationId,
    [property: JsonPropertyName("planSha256")] string PlanSha256,
    [property: JsonPropertyName("planMetadataDigest")] string PlanMetadataDigest,
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("stack")] string Stack,
    [property: JsonPropertyName("environment")] string Environment,
    [property: JsonPropertyName("planExpiresAtUtc")] DateTimeOffset? PlanExpiresAtUtc = null)
{
    [JsonPropertyName("receiptSchemaVersion")]
    public string ReceiptSchemaVersion { get; init; } = ApprovalReceiptIssuer.SchemaVersion;
}

/// <summary>What the approving principal asked for. Everything not named here is read from the plan.</summary>
internal sealed record ApprovalIssueRequest(
    string PlanResponseJson,
    string Action,
    string Issuer,
    string SigningMode,
    int TtlMinutes = ApprovalReceiptIssuer.DefaultTtlMinutes,
    string Decision = "approved",
    string? Stack = null,
    string? Environment = null);

/// <summary>A refusal to issue. Never carries a partial receipt.</summary>
internal sealed class ApprovalIssueRefusedException(string code, string message) : Exception(message)
{
    internal string Code { get; } = code;
}

/// <summary>The emitted receipt and the evidentiary posture its mode implies.</summary>
internal sealed record IssuedApprovalReceipt(string ReceiptJson, string ApprovalReceiptId, bool Evidentiary);

/// <summary>
/// Issues <c>honua.devops.provision-approval/v1</c> receipts from a reviewed plan response.
/// </summary>
/// <remarks>
/// <para>
/// This is the approval half of the plan → approve → apply loop, and it is meant to run
/// as a DIFFERENT principal from the agent that applies: under <c>kms-mac</c> the issuer
/// holds <c>kms:GenerateMac</c> only and the applying agent <c>kms:VerifyMac</c> only, so
/// neither can complete the loop alone.
/// </para>
/// <para>
/// The issuer uses the same canonical payload helper and the same signature provider
/// types the verifier uses, and validates its output against the published schema
/// before emitting it. It never invents a binding: every identity comes from the plan
/// response, and a flag may only fill in a value the response does not carry — it can
/// never contradict one.
/// </para>
/// </remarks>
internal static class ApprovalReceiptIssuer
{
    internal const string SchemaVersion = "honua.devops.provision-approval/v1";
    internal const int DefaultTtlMinutes = 15;
    internal const int MaxTtlMinutes = 60;

    /// <summary>The only environment a <c>local-hmac-dev</c> receipt may name.</summary>
    internal const string LocalHmacDevEnvironment = "dev";

    internal static async Task<IssuedApprovalReceipt> IssueAsync(
        ApprovalIssueRequest request,
        IApprovalSignatureProvider signatureProvider,
        DateTimeOffset? now = null,
        CancellationToken cancellationToken = default)
    {
        string action = Normalize(request.Action);
        if (action is not ("apply" or "destroy"))
        {
            throw Refuse("invalid-action", "--action must be `apply` or `destroy`.");
        }

        string decision = Normalize(request.Decision);
        if (decision is not ("approved" or "rejected"))
        {
            throw Refuse("invalid-decision", "--decision must be `approved` or `rejected`.");
        }

        if (!ApprovalSigningModes.IsKnown(request.SigningMode))
        {
            throw Refuse(
                "invalid-signing-mode",
                $"--signing-mode must be one of: {string.Join(", ", ApprovalSigningModes.All)}.");
        }

        if (!string.Equals(request.SigningMode, signatureProvider.SigningMode, StringComparison.Ordinal))
        {
            throw Refuse(
                "signing-mode-mismatch",
                $"Requested signing mode `{request.SigningMode}` does not match the configured provider `{signatureProvider.SigningMode}`.");
        }

        if (request.TtlMinutes < 1 || request.TtlMinutes > MaxTtlMinutes)
        {
            throw Refuse(
                "ttl-out-of-range",
                $"--ttl-minutes must be between 1 and {MaxTtlMinutes}; the verifier refuses any receipt whose lifetime exceeds one hour.");
        }

        if (string.IsNullOrWhiteSpace(request.Issuer) || request.Issuer.Length > 200)
        {
            throw Refuse("invalid-issuer", "--issuer must be a non-empty identity of at most 200 characters.");
        }

        PlanBinding binding = ReadPlanBinding(request.PlanResponseJson);

        if (binding.Action is not null && !string.Equals(binding.Action, action, StringComparison.Ordinal))
        {
            throw Refuse(
                "action-mismatch",
                $"The plan response is a `{binding.Action}` plan; it cannot be approved for `{action}`.");
        }

        string stack = Reconcile("stack", binding.Stack, request.Stack);
        string environment = Reconcile("environment", binding.Environment, request.Environment);

        // A receipt its own verifier could have forged is tolerable only where nothing
        // depends on it. Refuse to mint one for anything but the dev environment; the
        // verifier refuses the same receipt independently.
        if (string.Equals(request.SigningMode, ApprovalSigningModes.LocalHmacDev, StringComparison.Ordinal)
            && !string.Equals(environment, LocalHmacDevEnvironment, StringComparison.Ordinal))
        {
            throw Refuse(
                "local-hmac-dev-environment-refused",
                $"`{ApprovalSigningModes.LocalHmacDev}` receipts are refused outside environment `{LocalHmacDevEnvironment}` (plan environment is `{environment}`). Use `{ApprovalSigningModes.KmsMac}`.");
        }

        // Plan expiry is deliberately NOT judged here: the apply wrapper owns that clock
        // and refuses an expired saved plan with `plan-expired` regardless of the receipt.
        DateTimeOffset issued = TruncateToSeconds(now ?? DateTimeOffset.UtcNow);

        DateTimeOffset expires = issued.AddMinutes(request.TtlMinutes);
        string keyId = signatureProvider.ResolveKeyId(request.Issuer)
            ?? throw Refuse(
                "issuer-not-configured",
                $"Issuer `{request.Issuer}` has no configured key for `{signatureProvider.SigningMode}`.");

        string receiptId = $"approval-{Guid.NewGuid():n}";
        string canonical = ApprovalReceiptCanonicalization.Payload(
            SchemaVersion,
            receiptId,
            request.Issuer,
            keyId,
            binding.ProvisioningOperationId,
            binding.PlanSha256,
            binding.PlanMetadataDigest,
            action,
            stack,
            environment,
            decision,
            issued,
            expires,
            request.SigningMode);

        ApprovalSignature signature;
        try
        {
            signature = await signatureProvider.SignAsync(request.Issuer, canonical, cancellationToken);
        }
        catch (KmsMacAccessDeniedException denied)
        {
            throw Refuse("kms-generate-mac-denied", denied.Message);
        }

        if (!string.Equals(signature.KeyId, keyId, StringComparison.Ordinal)
            || !string.Equals(signature.SigningMode, request.SigningMode, StringComparison.Ordinal))
        {
            throw Refuse("signature-binding-mismatch", "The signature provider signed under a different key or mode than the receipt declares.");
        }

        // Property order mirrors the schema's `required` list; the signature covers the
        // values, not the order, but a reviewer reads the document top to bottom.
        Dictionary<string, object> receipt = new()
        {
            ["schemaVersion"] = SchemaVersion,
            ["approvalReceiptId"] = receiptId,
            ["issuer"] = request.Issuer,
            ["keyId"] = keyId,
            ["provisioningOperationId"] = binding.ProvisioningOperationId,
            ["planSha256"] = binding.PlanSha256,
            ["planMetadataDigest"] = binding.PlanMetadataDigest,
            ["action"] = action,
            ["stack"] = stack,
            ["environment"] = environment,
            ["decision"] = decision,
            ["signingMode"] = request.SigningMode,
            ["issuedAtUtc"] = issued.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            ["expiresAtUtc"] = expires.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            ["signature"] = signature.Signature,
        };
        string receiptJson = JsonSerializer.Serialize(receipt, new JsonSerializerOptions { WriteIndented = true });

        IReadOnlyList<string> findings = ProvisioningContracts.ValidateProvisionApproval(receiptJson);
        if (findings.Count > 0)
        {
            throw Refuse(
                "receipt-schema-invalid",
                $"The issued receipt does not satisfy {SchemaVersion}: {string.Join("; ", findings)}");
        }

        return new IssuedApprovalReceipt(receiptJson, receiptId, ApprovalSigningModes.IsEvidentiary(request.SigningMode));
    }

    private sealed record PlanBinding(
        string ProvisioningOperationId,
        string PlanSha256,
        string PlanMetadataDigest,
        string? Action,
        string? Stack,
        string? Environment);

    /// <summary>
    /// Reads the binding from a saved plan response. Accepts the bare tool result
    /// (an <see cref="OperationResponse"/> document) or an MCP <c>tools/call</c> result
    /// that wraps it in <c>structuredContent</c> or a text content item.
    /// </summary>
    private static PlanBinding ReadPlanBinding(string planResponseJson)
    {
        if (string.IsNullOrWhiteSpace(planResponseJson))
        {
            throw Refuse("plan-response-missing", "The plan response is empty.");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(planResponseJson, new JsonDocumentOptions { MaxDepth = 64 });
        }
        catch (JsonException exception)
        {
            throw Refuse("plan-response-invalid", $"The plan response is not valid JSON: {exception.Message}");
        }

        using (document)
        {
            JsonElement response = Unwrap(document.RootElement, out JsonDocument? inner);
            using (inner)
            {
                string? status = ReadString(response, "status");
                if (status is not null && status is not ("terraform-plan-ready" or "terraform-destroy-plan-ready"))
                {
                    throw Refuse(
                        "plan-response-not-ready",
                        $"The response status is `{status}`, not a ready plan; only a reviewed plan can be approved.");
                }

                JsonElement? lineage = Property(response, "provisioningLineage");
                JsonElement? request = Property(response, "approvalRequest");

                string? operationId = ReadString(request, "provisioningOperationId") ?? ReadString(lineage, "provisioningOperationId");
                string? planSha = ReadString(request, "planSha256") ?? ReadString(lineage, "planSha256");
                string? metadataDigest = ReadString(request, "planMetadataDigest") ?? ReadString(lineage, "planMetadataDigest");

                // Where both projections are present they must agree; a disagreement means
                // the document was edited, and an edited plan response is not approvable.
                RequireAgreement("provisioningOperationId", ReadString(request, "provisioningOperationId"), ReadString(lineage, "provisioningOperationId"));
                RequireAgreement("planSha256", ReadString(request, "planSha256"), ReadString(lineage, "planSha256"));
                RequireAgreement("planMetadataDigest", ReadString(request, "planMetadataDigest"), ReadString(lineage, "planMetadataDigest"));

                if (operationId is null || planSha is null || metadataDigest is null)
                {
                    throw Refuse(
                        "plan-response-incomplete",
                        "The plan response does not carry provisioningLineage.provisioningOperationId, planSha256 and planMetadataDigest.");
                }

                string? action = ReadString(request, "action");
                if (action is null && status is not null)
                {
                    action = status == "terraform-destroy-plan-ready" ? "destroy" : "apply";
                }

                return new PlanBinding(
                    operationId,
                    planSha.ToLowerInvariant(),
                    metadataDigest.ToLowerInvariant(),
                    action,
                    ReadString(request, "stack"),
                    ReadString(request, "environment"));
            }
        }
    }

    private static JsonElement Unwrap(JsonElement root, out JsonDocument? inner)
    {
        inner = null;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw Refuse("plan-response-invalid", "The plan response must be a JSON object.");
        }

        // A JSON-RPC envelope from a recorded MCP session.
        if (Property(root, "result") is { ValueKind: JsonValueKind.Object } rpcResult)
        {
            root = rpcResult;
        }

        if (Property(root, "structuredContent") is { ValueKind: JsonValueKind.Object } structured)
        {
            return structured;
        }

        if (Property(root, "content") is { ValueKind: JsonValueKind.Array } content
            && Property(root, "provisioningLineage") is null)
        {
            foreach (JsonElement item in content.EnumerateArray())
            {
                if (ReadString(item, "type") == "text" && ReadString(item, "text") is { } text)
                {
                    try
                    {
                        inner = JsonDocument.Parse(text);
                    }
                    catch (JsonException)
                    {
                        continue;
                    }

                    if (inner.RootElement.ValueKind == JsonValueKind.Object)
                    {
                        return inner.RootElement;
                    }

                    inner.Dispose();
                    inner = null;
                }
            }
        }

        return root;
    }

    private static string Reconcile(string name, string? fromPlan, string? fromFlag)
    {
        string? flag = string.IsNullOrWhiteSpace(fromFlag) ? null : fromFlag.Trim();
        if (fromPlan is not null && flag is not null && !string.Equals(fromPlan, flag, StringComparison.Ordinal))
        {
            throw Refuse(
                $"{name}-mismatch",
                $"--{name} `{flag}` contradicts the plan response's {name} `{fromPlan}`; an approval cannot retarget a plan.");
        }

        return fromPlan ?? flag ?? throw Refuse(
            $"{name}-missing",
            $"The plan response does not carry approvalRequest.{name}; pass --{name} explicitly.");
    }

    private static void RequireAgreement(string name, string? a, string? b)
    {
        if (a is not null && b is not null && !string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
        {
            throw Refuse(
                "plan-response-inconsistent",
                $"approvalRequest.{name} and provisioningLineage.{name} disagree; the plan response was altered.");
        }
    }

    private static JsonElement? Property(JsonElement? element, string name)
    {
        if (element is not { ValueKind: JsonValueKind.Object } value)
        {
            return null;
        }

        foreach (JsonProperty property in value.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value;
            }
        }

        return null;
    }

    private static string? ReadString(JsonElement? element, string name)
        => Property(element, name) is { ValueKind: JsonValueKind.String } value
           && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()
            : null;

    private static DateTimeOffset TruncateToSeconds(DateTimeOffset value)
    {
        DateTimeOffset utc = value.ToUniversalTime();
        return new DateTimeOffset(utc.Ticks - (utc.Ticks % TimeSpan.TicksPerSecond), TimeSpan.Zero);
    }

    private static string Normalize(string? value) => (value ?? string.Empty).Trim().ToLowerInvariant();

    private static ApprovalIssueRefusedException Refuse(string code, string message) => new(code, message);
}
