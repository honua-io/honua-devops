using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.DevOps.Agent.Operations.Eval;

namespace Honua.DevOps.Agent.Operations;

/// <summary>
/// The exact-plan metadata document <c>scripts/terraform-exact-plan.sh</c> writes
/// beside the saved plan (<c>terraform-exact-plan.v1.schema.json</c>).
/// </summary>
/// <remarks>
/// <c>PlanMetadataDigest</c> is the value an approval binds to. It is the SHA-256 of
/// this document's canonical JSON with the digest field removed, computed by the
/// substrate; honua-devops re-reads rather than re-derives it, and the apply wrapper
/// independently refuses with <c>metadata-tampered</c> if the document no longer
/// hashes to it.
/// </remarks>
internal sealed record ExactPlanMetadata(
    string PlanMetadataDigest,
    string Action,
    DateTimeOffset ExpiresAtUtc,
    string TerraformRoot,
    string IacRevision,
    string IacTreeDigest,
    string TerraformVersion,
    string ProviderLockDigest,
    string BackendConfigDigest,
    string BackendKind,
    bool BackendIsRemote,
    string Workspace,
    string? ObjectKey,
    string? BackendRegion,
    string AccountId,
    string Partition,
    string AssumedRoleArn,
    string? RoleId,
    string? Issuer,
    string CredentialKind,
    string EvidenceMode,
    string InputDigest,
    string? StateLineageBefore,
    long? StateSerialBefore,
    string SavedPlanSha256,
    bool ReleaseQualified)
{
    internal static bool TryRead(string json, string schemaJson, out ExactPlanMetadata? metadata, out string error)
    {
        metadata = null;
        error = string.Empty;

        IReadOnlyList<string> schemaErrors;
        try
        {
            schemaErrors = JsonSchemaValidator.Validate(json, schemaJson);
        }
        catch (JsonException exception)
        {
            error = $"The exact-plan metadata or its schema was not valid JSON: {exception.Message}";
            return false;
        }

        if (schemaErrors.Count > 0)
        {
            error = "The exact-plan metadata does not satisfy `terraform-exact-plan.v1.schema.json`: "
                + string.Join("; ", schemaErrors.Take(8));
            return false;
        }

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        JsonElement source = root.GetProperty("source");
        JsonElement toolchain = root.GetProperty("toolchain");
        JsonElement backend = root.GetProperty("backend");
        JsonElement identity = root.GetProperty("identity");
        JsonElement inputs = root.GetProperty("inputs");
        JsonElement stateBefore = root.GetProperty("state_before");
        JsonElement plan = root.GetProperty("plan");
        JsonElement posture = root.GetProperty("posture");

        metadata = new ExactPlanMetadata(
            PlanMetadataDigest: root.GetProperty("plan_metadata_digest").GetString()!,
            Action: root.GetProperty("action").GetString()!,
            ExpiresAtUtc: DateTimeOffset.Parse(
                root.GetProperty("expires_at_utc").GetString()!,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal),
            TerraformRoot: source.GetProperty("terraform_root").GetString()!,
            IacRevision: source.GetProperty("iac_revision").GetString()!,
            IacTreeDigest: source.GetProperty("iac_tree_digest").GetString()!,
            TerraformVersion: toolchain.GetProperty("terraform_version").GetString()!,
            ProviderLockDigest: toolchain.GetProperty("provider_lock_digest").GetString()!,
            BackendConfigDigest: backend.GetProperty("backend_config_digest").GetString()!,
            BackendKind: backend.GetProperty("backend_kind").GetString()!,
            BackendIsRemote: backend.GetProperty("is_remote").GetBoolean(),
            Workspace: backend.GetProperty("workspace").GetString()!,
            ObjectKey: ReadString(backend, "object_key"),
            BackendRegion: ReadString(backend, "region"),
            AccountId: identity.GetProperty("account_id").GetString()!,
            Partition: identity.GetProperty("partition").GetString()!,
            AssumedRoleArn: identity.GetProperty("assumed_role_arn").GetString()!,
            RoleId: ReadString(identity, "role_id"),
            Issuer: ReadString(identity, "issuer"),
            CredentialKind: identity.GetProperty("credential_kind").GetString()!,
            EvidenceMode: identity.GetProperty("evidence_mode").GetString()!,
            InputDigest: inputs.GetProperty("input_digest").GetString()!,
            StateLineageBefore: ReadString(stateBefore, "lineage"),
            StateSerialBefore: ReadInt64(stateBefore, "serial"),
            SavedPlanSha256: plan.GetProperty("sha256").GetString()!,
            ReleaseQualified: posture.GetProperty("release_qualified").GetBoolean());
        return true;
    }

    /// <summary>
    /// An offline run reads STS/state fixtures instead of live AWS and is stamped
    /// <c>offline-test</c>. Such a plan describes no real cloud context, so it can
    /// never back a release claim.
    /// </summary>
    internal bool IsOfflineEvidence => string.Equals(EvidenceMode, "offline-test", StringComparison.Ordinal);

    internal static string? ReadString(JsonElement element, string name)
        => element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    internal static long? ReadInt64(JsonElement element, string name)
        => element.TryGetProperty(name, out JsonElement value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out long parsed)
            ? parsed
            : null;
}

/// <summary>
/// The execution receipt <c>scripts/terraform-exact-apply.sh</c> writes after it
/// consumes a saved plan (<c>terraform-exec-receipt.v1.schema.json</c>).
/// </summary>
/// <remarks>
/// This is the resolvable referent that <c>actuatorReceiptReference</c> previously
/// lacked: a real document, on disk, content-addressed, naming the state lineage and
/// serial the apply produced and the teardown root it can be undone from.
/// </remarks>
internal sealed record TerraformExecReceipt(
    string Action,
    int ExitStatus,
    string Status,
    string PlanMetadataDigest,
    string? ApprovedDigest,
    string SavedPlanSha256,
    string AssumedRoleArn,
    string? RoleId,
    string AccountId,
    string? Partition,
    string CredentialKind,
    string BackendConfigDigest,
    string BackendKind,
    string Workspace,
    string? ObjectKey,
    string? StateLineageBefore,
    long? StateSerialBefore,
    string? StateLineageAfter,
    long? StateSerialAfter,
    string OutputContractName,
    string? OutputContractDigest,
    string TeardownRoot,
    string TeardownAction)
{
    internal static bool TryRead(string json, string schemaJson, out TerraformExecReceipt? receipt, out string error)
    {
        receipt = null;
        error = string.Empty;

        IReadOnlyList<string> schemaErrors;
        try
        {
            schemaErrors = JsonSchemaValidator.Validate(json, schemaJson);
        }
        catch (JsonException exception)
        {
            error = $"The execution receipt or its schema was not valid JSON: {exception.Message}";
            return false;
        }

        if (schemaErrors.Count > 0)
        {
            error = "The execution receipt does not satisfy `terraform-exec-receipt.v1.schema.json`: "
                + string.Join("; ", schemaErrors.Take(8));
            return false;
        }

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        JsonElement workload = root.GetProperty("workload_identity");
        JsonElement backendStep = root.GetProperty("backend_step");
        JsonElement stateBefore = root.GetProperty("state_before");
        JsonElement stateAfter = root.GetProperty("state_after");
        JsonElement outputContract = root.GetProperty("output_contract");
        JsonElement cleanup = root.GetProperty("cleanup");

        receipt = new TerraformExecReceipt(
            Action: root.GetProperty("action").GetString()!,
            ExitStatus: root.GetProperty("exit_status").GetInt32(),
            Status: root.GetProperty("status").GetString()!,
            PlanMetadataDigest: root.GetProperty("plan_metadata_digest").GetString()!,
            ApprovedDigest: ExactPlanMetadata.ReadString(root, "approved_digest"),
            SavedPlanSha256: root.GetProperty("saved_plan_sha256").GetString()!,
            AssumedRoleArn: workload.GetProperty("assumed_role_arn").GetString()!,
            RoleId: ExactPlanMetadata.ReadString(workload, "role_id"),
            AccountId: workload.GetProperty("account_id").GetString()!,
            Partition: ExactPlanMetadata.ReadString(workload, "partition"),
            CredentialKind: workload.GetProperty("credential_kind").GetString()!,
            BackendConfigDigest: backendStep.GetProperty("backend_config_digest").GetString()!,
            BackendKind: backendStep.GetProperty("backend_kind").GetString()!,
            Workspace: backendStep.GetProperty("workspace").GetString()!,
            ObjectKey: ExactPlanMetadata.ReadString(backendStep, "object_key"),
            StateLineageBefore: ExactPlanMetadata.ReadString(stateBefore, "lineage"),
            StateSerialBefore: ExactPlanMetadata.ReadInt64(stateBefore, "serial"),
            StateLineageAfter: ExactPlanMetadata.ReadString(stateAfter, "lineage"),
            StateSerialAfter: ExactPlanMetadata.ReadInt64(stateAfter, "serial"),
            OutputContractName: outputContract.GetProperty("output_name").GetString()!,
            OutputContractDigest: ExactPlanMetadata.ReadString(outputContract, "digest"),
            TeardownRoot: cleanup.GetProperty("teardown_root").GetString()!,
            TeardownAction: cleanup.GetProperty("teardown_action").GetString()!);
        return true;
    }

    internal bool Succeeded => string.Equals(Status, "succeeded", StringComparison.Ordinal) && ExitStatus == 0;
}

/// <summary>
/// The substrate-sourced execution facts carried into the provision binding, so the
/// binding names which state, under which identity, produced the claim.
/// </summary>
/// <remarks>
/// Every field here is read from a document the substrate produced and validated
/// against its published schema. None of it is caller-supplied, and none of it is
/// secret: the backend is identified by a config digest and an object key, the
/// identity by an assumed-role ARN, and the state only by lineage and serial.
/// </remarks>
internal sealed record IacExecutionEvidence(
    [property: JsonPropertyName("planMetadataDigest")] string PlanMetadataDigest,
    [property: JsonPropertyName("savedPlanSha256")] string SavedPlanSha256,
    [property: JsonPropertyName("terraformRoot")] string TerraformRoot,
    [property: JsonPropertyName("iacRevision")] string IacRevision,
    [property: JsonPropertyName("iacTreeDigest")] string IacTreeDigest,
    [property: JsonPropertyName("terraformVersion")] string TerraformVersion,
    [property: JsonPropertyName("providerLockDigest")] string ProviderLockDigest,
    [property: JsonPropertyName("inputDigest")] string InputDigest,
    [property: JsonPropertyName("backend")] IacBackendIdentity Backend,
    [property: JsonPropertyName("executionIdentity")] IacExecutionIdentity ExecutionIdentity,
    [property: JsonPropertyName("state")] IacStateLineage State,
    [property: JsonPropertyName("operatorContractDigest")] string? OperatorContractDigest,
    [property: JsonPropertyName("evidenceMode")] string EvidenceMode,
    [property: JsonPropertyName("releaseQualified")] bool ReleaseQualified);

internal sealed record IacBackendIdentity(
    [property: JsonPropertyName("backendConfigDigest")] string BackendConfigDigest,
    [property: JsonPropertyName("backendKind")] string BackendKind,
    [property: JsonPropertyName("isRemote")] bool IsRemote,
    [property: JsonPropertyName("workspace")] string Workspace,
    [property: JsonPropertyName("objectKey")] string? ObjectKey,
    [property: JsonPropertyName("region")] string? Region);

internal sealed record IacExecutionIdentity(
    [property: JsonPropertyName("assumedRoleArn")] string AssumedRoleArn,
    [property: JsonPropertyName("roleId")] string? RoleId,
    [property: JsonPropertyName("accountId")] string AccountId,
    [property: JsonPropertyName("partition")] string Partition,
    [property: JsonPropertyName("issuer")] string? Issuer,
    [property: JsonPropertyName("credentialKind")] string CredentialKind);

internal sealed record IacStateLineage(
    [property: JsonPropertyName("lineageBefore")] string? LineageBefore,
    [property: JsonPropertyName("serialBefore")] long? SerialBefore,
    [property: JsonPropertyName("lineageAfter")] string? LineageAfter,
    [property: JsonPropertyName("serialAfter")] long? SerialAfter);

/// <summary>
/// A teardown handle that a holder can actually dereference: the exact root,
/// workspace, backend and state the apply wrote, plus the action that undoes it.
/// </summary>
/// <remarks>
/// The previous handle was a synthesized <c>terraform://stack/environment/opid</c>
/// string with no resolvable referent — it named nothing a holder could point
/// Terraform at. This one is read from the execution receipt's <c>cleanup</c> block
/// and identifies the state object to destroy from.
/// </remarks>
internal sealed record TeardownHandle(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("terraformRoot")] string TerraformRoot,
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("workspace")] string Workspace,
    [property: JsonPropertyName("backendConfigDigest")] string BackendConfigDigest,
    [property: JsonPropertyName("objectKey")] string? ObjectKey,
    [property: JsonPropertyName("stateLineage")] string? StateLineage,
    [property: JsonPropertyName("stateSerial")] long? StateSerial,
    [property: JsonPropertyName("accountId")] string AccountId,
    [property: JsonPropertyName("region")] string? Region)
{
    internal const string TeardownKind = "honua.iac.terraform-teardown/v1";

    internal static TeardownHandle FromReceipt(TerraformExecReceipt receipt, string? region)
        => new(
            TeardownKind,
            receipt.TeardownRoot,
            receipt.TeardownAction,
            receipt.Workspace,
            receipt.BackendConfigDigest,
            receipt.ObjectKey,
            receipt.StateLineageAfter,
            receipt.StateSerialAfter,
            receipt.AccountId,
            region);
}
