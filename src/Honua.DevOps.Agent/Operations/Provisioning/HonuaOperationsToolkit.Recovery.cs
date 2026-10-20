using System.Text;
using System.Text.Json;

namespace Honua.DevOps.Agent.Operations;

internal sealed partial class HonuaOperationsToolkit
{
    private static ProvisioningRecoveryStore GetRecoveryStore() =>
        new(Path.Combine(GetProvisioningStateRoot(), "destroy-recovery"), GetEvidenceStore());

    private sealed record DestroyRecoverySeed(ProvisioningCleanupObligation Parent, string Root,
        string ParentObligationSha256, string? ReceiptSha256, string? ClaimSha256, long? MinimumSerial,
        ProvisioningEvidenceReference[] ParentOriginals);

    private static ProvisioningRecoveryEvidence RecoveryProjection(ProvisioningEvidenceReference reference)
    {
        DestroyRecoveryContext value = GetRecoveryStore().ReadContext(reference);
        return new(value.RootProvisioningOperationId, value.ParentProvisioningOperationId,
            value.ProvisioningOperationId, reference.Sha256, reference);
    }

    private async Task<DestroyRecoverySeed> ValidateRecoveryParentAsync(string operation)
    {
        ProvisioningCleanupStore store = GetCleanupStore();
        ProvisioningCleanupObligation origin = store.Read(operation);
        if (origin.Metadata.Action != "destroy" || origin.HostBinding != CleanupHostBinding()
            || origin.Stack != ProvisioningRuntimeProfile.Resolve(runtime.RuntimeKind).StackId
            || !runtime.AllowedEnvironments.Contains(origin.Environment, StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException("Retained destroy is outside this operator scope.");
        byte[] obligation = ProvisioningCleanupStore.ReadOriginal(Path.Combine(store.DirectoryFor(operation), "obligation.json"))!;
        byte[]? claim = await ReadCleanupClaimAsync(origin);
        if (!Directory.Exists(origin.ClaimDirectory) || claim is null)
            throw new InvalidDataException("Original executor custody is unavailable.");
        bool live = ProvisioningCleanupStore.ExecutorIsLive(origin.ClaimDirectory);
        using (JsonDocument status = JsonDocument.Parse(claim))
        {
            JsonElement value = status.RootElement;
            if (value.GetProperty("schema_version").GetString() != "v1"
                || value.GetProperty("kind").GetString() != "honua.iac.claim-status"
                || value.GetProperty("claim_dir").GetString() != origin.ClaimDirectory
                || string.IsNullOrWhiteSpace(value.GetProperty("executor_id").GetString())
                || !value.GetProperty("acquired_at_utc").TryGetDateTimeOffset(out _)
                || value.GetProperty("state").GetString() is not ("held" or "completed" or "reconciliation-required")
                || value.GetProperty("phase").GetString() is not ("preflight" or "mutation-started" or "terraform-acknowledged" or "receipt-committed" or "reconciliation-required"))
                throw new InvalidDataException("Original executor custody differs.");
        }
        byte[]? receipt = ProvisioningCleanupStore.ReadOriginal(Path.Combine(origin.ClaimDirectory, "receipt.json"));
        ProvisioningCleanupEvidence inspected = store.Inspect(origin, receipt, claim, live);
        if (inspected.State is "cleanup-live-executor" or "cleanup-execution-acknowledged")
            throw new InvalidDataException("Retained destroy is live or already completed.");
        // A cancelled execution may have no final receipt. Preserve that fact;
        // the fresh remote plan will reconcile state without fabricating one.
        ProvisioningCleanupTerminal? terminal = store.ReadTerminal(origin);
        if (receipt is null && terminal?.Receipt is { } retained) receipt = GetEvidenceStore().Read(retained);
        byte[] approvalBytes = GetEvidenceStore().Read(origin.EvidenceRefs.Single(row => row.Kind == "approval"));
        ProvisionApprovalReceipt approval = JsonSerializer.Deserialize<ProvisionApprovalReceipt>(approvalBytes,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("Original destroy approval is unavailable.");
        IApprovalSignatureProvider verifier = ApprovalSignatureProvider;
        if (approval.SigningMode != verifier.SigningMode || approval.KeyId != verifier.ResolveKeyId(approval.Issuer)
            || approval.RecoveryContextSha256 != origin.Recovery?.Sha256
            || approval.ExpiresAtUtc <= approval.IssuedAtUtc || approval.ExpiresAtUtc > approval.IssuedAtUtc.AddHours(1)
            || approval.SigningMode == ApprovalSigningModes.LocalHmacDev && origin.Environment != ApprovalReceiptIssuer.LocalHmacDevEnvironment)
            throw new InvalidDataException("Original destroy approval identity differs.");
        ApprovalVerificationResult verified = await verifier.VerifyAsync(approval.Issuer,
            CanonicalApprovalPayload(approval), approval.Signature);
        if (!verified.Verified) throw new InvalidDataException("Original destroy approval signature differs.");
        long? serial = origin.Metadata.StateSerialBefore;
        if (receipt is not null)
        {
            using JsonDocument parsed = JsonDocument.Parse(receipt);
            JsonElement stateAfter = parsed.RootElement.GetProperty("state_after");
            string? afterLineage = ExactPlanMetadata.ReadString(stateAfter, "lineage");
            if (origin.Metadata.StateLineageBefore is { } knownLineage
                && afterLineage is not null && afterLineage != knownLineage)
                throw new InvalidDataException("Original destroy state lineage changed.");
            if (stateAfter.TryGetProperty("serial", out JsonElement afterSerial)
                && afterSerial.ValueKind == JsonValueKind.Number && afterSerial.TryGetInt64(out long after))
                serial = serial is null ? after : Math.Max(serial.Value, after);
        }
        string root = origin.Recovery is null ? operation
            : GetRecoveryStore().ReadContext(origin.Recovery).RootProvisioningOperationId;
        List<ProvisioningEvidenceReference> originals = [.. origin.EvidenceRefs,
            GetEvidenceStore().Put("recovery-parent-obligation", obligation)];
        if (receipt is not null) originals.Add(GetEvidenceStore().Put("recovery-parent-execution", receipt));
        if (claim is not null) originals.Add(GetEvidenceStore().Put("recovery-parent-claim", claim));
        return new(origin, root, ProvisioningRecoveryStore.Digest(obligation),
            receipt is null ? null : ProvisioningRecoveryStore.Digest(receipt),
            claim is null ? null : ProvisioningRecoveryStore.Digest(claim), serial, originals.ToArray());
    }

    private async Task<OperationResponse> PrepareDestroyRecoveryAsync(string stack, string size, string variablesJson,
        string parentOperation, string idempotencyKey, CancellationToken cancellationToken)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(idempotencyKey))
                return ProvisioningRefusal("recovery-key-required", "Destroy recovery requires an explicit plan idempotency key.", [], []);
            DestroyRecoverySeed initial = await ValidateRecoveryParentAsync(parentOperation);
            using FileStream rootLock = await AcquireLineageLockAsync("destroy-recovery:" + initial.Root, cancellationToken);
            ProvisioningRecoveryStore store = GetRecoveryStore();
            (DestroyRecoveryHead? head, string? previous) = store.Read(initial.Root);
            string requestPath = GetProvisioningStatePath("destroy-recovery-request:" + initial.Root + ":" + idempotencyKey);
            string requestDigest = ComputeSha256(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
                { stack = NormalizeToken(stack), size = NormalizeToken(size),
                    variables = ComputeVariablesDigest(ParseProvisioningVariables(NormalizeToken(stack), variablesJson)), root = initial.Root })));
            if (File.Exists(requestPath))
            {
                DestroyRecoveryRequest retained = JsonSerializer.Deserialize<DestroyRecoveryRequest>(
                    ProvisioningCleanupStore.ReadOriginal(requestPath)!)!;
                if (retained.Root != initial.Root || retained.RequestDigest != requestDigest)
                    return ProvisioningRefusal("idempotency-conflict", "This recovery plan key is bound to different inputs.", [], []);
                if (retained.Response is not null) return retained.Response;
                return ProvisioningRefusal("recovery-plan-indeterminate",
                    "The reserved recovery plan result is unavailable. An explicit new key may reconcile this root; no spent approval is reusable.", [], []);
            }
            DestroyRecoverySeed parent = initial;
            if (head is not null)
            {
                // A pending read-only plan retains its actual parent even when
                // interrupted before context exposure. Never backslide to the
                // caller's older root after a failed successor.
                string originPath = Path.Combine(GetCleanupStore().DirectoryFor(head.ProvisioningOperationId), "obligation.json");
                string retainedParent = head.Context is not null && File.Exists(originPath)
                    ? head.ProvisioningOperationId : head.ParentProvisioningOperationId;
                parent = await ValidateRecoveryParentAsync(retainedParent);
            }
            if (parent.Root != initial.Root) throw new InvalidDataException("Recovery root changed.");
            string token = Guid.NewGuid().ToString("n"), operation = "urn:honua:provisioning:" + token;
            DestroyRecoveryHead pending = new(parent.Root, parent.Parent.ProvisioningOperationId, operation, requestDigest);
            DestroyRecoveryRequest request = new(parent.Root, requestDigest, operation, null);
            await WriteEvidenceCopyAsync(requestPath, JsonSerializer.SerializeToUtf8Bytes(request), cancellationToken);
            await store.CompareExchangeAsync(parent.Root, previous, pending, cancellationToken);
            // An abandoned planning reservation is not an executor claim. An
            // explicit NEW key may supersede it; old attempts fail the root CAS
            // before mutation and every fresh plan requires a fresh approval.
            OperationResponse response = await ProvisionInfrastructureCoreAsync(stack, size, "destroy", variablesJson,
                false, "", "", cancellationToken, token, parent);
            string pendingSha = store.Read(parent.Root).Sha256!;
            await store.CompareExchangeAsync(parent.Root, pendingSha,
                pending with { Context = response.ProvisioningRecovery?.EvidenceRef, Response = response }, cancellationToken);
            await WriteEvidenceCopyAsync(requestPath, JsonSerializer.SerializeToUtf8Bytes(request with { Response = response }), cancellationToken);
            return response;
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException
            or JsonException or InvalidOperationException or ArgumentException)
        {
            return ProvisioningRefusal("destroy-recovery-refused",
                "The retained destroy relationship is live, changed, invalid or outside scope; no new mutation was authorized.", [], []);
        }
    }

    private async Task<OperationResponse> ExecuteDestroyRecoveryIfBoundAsync(
        string stack, string size, string action, string variablesJson, bool confirmed,
        string confirmation, string approvalReceiptJson, string operation, CancellationToken cancellationToken)
    {
        FileStream? rootLock = null;
        try
        {
            string token = operation["urn:honua:provisioning:".Length..];
            string manifestPath = Path.Combine(GetSavedPlanRoot(), token, "manifest.json");
            SavedTerraformPlanManifest? manifest = File.Exists(manifestPath)
                ? JsonSerializer.Deserialize<SavedTerraformPlanManifest>(File.ReadAllBytes(manifestPath)) : null;
            ProvisioningEvidenceReference? indexed = GetRecoveryStore().FindAttempt(operation);
            bool recoveryActor = manifest?.Metadata.ActorId?.StartsWith("honua-devops:destroy-recovery:", StringComparison.Ordinal) == true;
            if (manifest is not null && File.Exists(manifest.PlanMetadataFile))
            {
                using JsonDocument original = JsonDocument.Parse(ProvisioningCleanupStore.ReadOriginal(manifest.PlanMetadataFile)!);
                string? actor = ExactPlanMetadata.ReadString(original.RootElement, "actor_id");
                recoveryActor |= actor?.StartsWith("honua-devops:destroy-recovery:", StringComparison.Ordinal) == true;
                if (actor != manifest.Metadata.ActorId
                    || ExactPlanMetadata.ReadString(original.RootElement, "plan_metadata_digest") != manifest.PlanMetadataDigest)
                    throw new InvalidDataException("Original saved metadata differs from its retained projection.");
            }
            bool recoveryApproval = false;
            try
            {
                using JsonDocument approval = JsonDocument.Parse(approvalReceiptJson);
                recoveryApproval = ExactPlanMetadata.ReadString(approval.RootElement, "recoveryContextSha256") is not null;
            }
            catch (JsonException) { /* The ordinary approval verifier retains its existing refusal contract. */ }
            if (manifest?.Recovery is null && (indexed is not null || recoveryActor || recoveryApproval)
                || manifest?.Recovery is not null && indexed != manifest.Recovery)
                throw new InvalidDataException("The signed recovery identity, durable index and saved plan differ.");
            if (manifest?.Recovery is not null)
            {
                DestroyRecoveryContext context = GetRecoveryStore().ReadContext(manifest.Recovery);
                rootLock = await AcquireLineageLockAsync("destroy-recovery:" + context.RootProvisioningOperationId, cancellationToken);
                (DestroyRecoveryHead? head, _) = GetRecoveryStore().Read(context.RootProvisioningOperationId);
                if (NormalizeToken(action) != "destroy" || context.ProvisioningOperationId != operation
                    || head?.ProvisioningOperationId != operation || head.Context != manifest.Recovery
                    || context.HostBinding != CleanupHostBinding() || context.PlanSha256 != manifest.PlanSha256
                    || context.PlanMetadataDigest != manifest.Metadata.PlanMetadataDigest)
                    throw new InvalidDataException("Destroy recovery reservation differs.");
                DestroyRecoverySeed parent = await ValidateRecoveryParentAsync(context.ParentProvisioningOperationId);
                if (parent.Root != context.RootProvisioningOperationId
                    || parent.ParentObligationSha256 != context.ParentObligationSha256
                    || parent.Parent.Metadata != context.ParentMetadata
                    || parent.Parent.Metadata.PlanMetadataDigest != context.ParentPlanMetadataDigest
                    || parent.Parent.Recovery?.Sha256 != context.ParentRecoveryContextSha256
                    || parent.Parent.EvidenceRefs.Single(row => row.Kind == "approval").Sha256 != context.ParentApprovalSha256
                    || parent.ReceiptSha256 != context.ParentExecutionReceiptSha256
                    || parent.ClaimSha256 != context.ParentClaimSha256
                    || parent.MinimumSerial != context.MinimumStateSerial
                    || !parent.ParentOriginals.OrderBy(row => row.Kind, StringComparer.Ordinal)
                        .SequenceEqual(context.ParentOriginals.OrderBy(row => row.Kind, StringComparer.Ordinal)))
                    throw new InvalidDataException("Destroy recovery original changed.");
                ValidateRecoveryMetadata(parent, manifest.Metadata);
                if (manifest.Metadata.ActorId != RecoveryActor(parent, operation))
                    throw new InvalidDataException("Signed destroy recovery actor differs.");
            }
        }
        catch (OperationCanceledException)
        {
            rootLock?.Dispose();
            throw;
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException
            or JsonException or InvalidOperationException or ArgumentException)
        {
            rootLock?.Dispose();
            return ProvisioningRefusal("destroy-recovery-refused",
                "The original destroy relationship is unavailable, live or superseded; no mutation was started.", [], []);
        }
        // Hold the root lock through approval validation, local one-use reservation
        // and executor completion. Do not relabel post-admission exceptions as refusal.
        using (rootLock)
            return await ProvisionInfrastructureCoreAsync(stack, size, action, variablesJson, confirmed,
                confirmation, approvalReceiptJson, cancellationToken);
    }

    private sealed record DestroyRecoveryRequest(string Root, string RequestDigest, string Operation, OperationResponse? Response);

    private static string RecoveryActor(DestroyRecoverySeed seed, string operation)
        => $"honua-devops:destroy-recovery:{seed.Root.Split(':')[^1]}:{seed.Parent.ProvisioningOperationId.Split(':')[^1]}:{operation.Split(':')[^1]}";

    private static string CanonicalRole(string arn)
    {
        int marker = arn.IndexOf(":assumed-role/", StringComparison.Ordinal);
        int final = arn.LastIndexOf('/');
        if (marker < 0 || final <= marker + ":assumed-role/".Length)
            throw new InvalidDataException("Recovery role is not an assumed role.");
        return arn[..final];
    }

    private static void ValidateRecoveryMetadata(DestroyRecoverySeed seed, ExactPlanMetadata next)
    {
        ExactPlanMetadata old = seed.Parent.Metadata;
        if (next.Action != "destroy" || next.TargetId != old.TargetId || next.AccountId != old.AccountId
            || next.Partition != old.Partition || CanonicalRole(next.AssumedRoleArn) != CanonicalRole(old.AssumedRoleArn)
            || next.RoleId != old.RoleId || next.Issuer != old.Issuer || next.CredentialKind != old.CredentialKind
            || next.EvidenceMode != old.EvidenceMode || next.ReleaseQualified != old.ReleaseQualified
            || next.WorkloadIdentityContractDigest != old.WorkloadIdentityContractDigest
            || next.TerraformRoot != old.TerraformRoot
            || next.BackendConfigDigest != old.BackendConfigDigest || next.BackendKind != old.BackendKind
            || next.Workspace != old.Workspace || next.ObjectKey != old.ObjectKey || next.BackendRegion != old.BackendRegion
            || next.BackendBucketArn != old.BackendBucketArn || next.BackendLockingKind != old.BackendLockingKind
            || next.BackendLockingDetail != old.BackendLockingDetail || next.BackendIsRemote != old.BackendIsRemote
            || next.StateLineageBefore != old.StateLineageBefore
            || seed.MinimumSerial is { } minimum && (next.StateSerialBefore is null || next.StateSerialBefore < minimum)
            || next.IacRevision != old.IacRevision || next.IacTreeDigest != old.IacTreeDigest
            || next.ProviderLockDigest != old.ProviderLockDigest || next.TerraformVersion != old.TerraformVersion
            || next.CandidateDigest != old.CandidateDigest)
            throw new InvalidDataException("Recovery plan changes original target, backend, owner or state lineage.");
    }

    private static void ValidateRecoveryInputs(DestroyRecoverySeed seed, string newVariables, string newMetadata)
    {
        byte[] original = GetEvidenceStore().Read(seed.Parent.EvidenceRefs.Single(row => row.Kind == "plan-metadata"));
        using JsonDocument old = JsonDocument.Parse(original);
        using JsonDocument next = JsonDocument.Parse(File.ReadAllBytes(newMetadata));
        string[] oldRefs = old.RootElement.GetProperty("inputs").GetProperty("input_refs").EnumerateArray()
            .Select(value => value.GetString()!).ToArray();
        string oldVariables = Path.Combine(Path.GetDirectoryName(seed.Parent.PlanPath)!, "small.auto.tfvars.json");
        string[] freshRefs = next.RootElement.GetProperty("inputs").GetProperty("input_refs").EnumerateArray()
            .Select(value => value.GetString()!).ToArray();
        List<string> oldEntries = [], freshEntries = [];
        foreach (string reference in oldRefs)
        {
            int separator = reference.IndexOf(':');
            if (separator < 1) throw new InvalidDataException("Recovery input reference differs.");
            string kind = reference[..separator], name = reference[(separator + 1)..];
            byte[] bytes;
            string freshRef = reference;
            if (kind == "env")
            {
                if (string.IsNullOrEmpty(name) || name.Any(value => !char.IsAsciiLetterOrDigit(value) && value != '_'))
                    throw new InvalidDataException("Recovery input environment differs.");
                bytes = Encoding.UTF8.GetBytes(Environment.GetEnvironmentVariable("TF_VAR_" + name)
                    ?? throw new InvalidDataException("Original input environment is missing."));
            }
            else if (kind is "varfile" or "autofile")
            {
                string originalRoot = Path.GetFullPath(Path.Combine(seed.Parent.IacRoot, seed.Parent.Metadata.TerraformRoot));
                string path = name == oldVariables ? newVariables : Path.GetFullPath(name, originalRoot);
                if (name == oldVariables) freshRef = kind + ":" + newVariables;
                ProvisioningCleanupStore.RejectLink(path);
                if (new FileInfo(path).Length > 8 * 1024 * 1024) throw new InvalidDataException("Recovery input is oversized.");
                bytes = File.ReadAllBytes(path);
            }
            else throw new InvalidDataException("Recovery input kind differs.");
            string digest = ComputeSha256(bytes);
            oldEntries.Add(reference + "=" + digest); freshEntries.Add(freshRef + "=" + digest);
        }
        if (!oldRefs.Contains("varfile:" + oldVariables, StringComparer.Ordinal))
            throw new InvalidDataException("Recovery input closure differs.");
        static string InputDigest(List<string> entries)
        {
            entries.Sort((left, right) => Encoding.UTF8.GetBytes(left).AsSpan().SequenceCompareTo(Encoding.UTF8.GetBytes(right)));
            return ComputeSha256(Encoding.UTF8.GetBytes(string.Join('\n', entries)));
        }
        string oldDigest = InputDigest(oldEntries), freshDigest = InputDigest(freshEntries);
        // The maintained helper sorts full reference=hash entries BEFORE
        // projecting refs with the first '=' split; prefix-overlapping names
        // therefore cannot be ordered by references alone.
        if (oldDigest != seed.Parent.Metadata.InputDigest
            || freshDigest != next.RootElement.GetProperty("inputs").GetProperty("input_digest").GetString()
            || !oldRefs.SequenceEqual(oldEntries.Select(entry => entry[..entry.IndexOf('=')]), StringComparer.Ordinal)
            || !freshRefs.SequenceEqual(freshEntries.Select(entry => entry[..entry.IndexOf('=')]), StringComparer.Ordinal))
            throw new InvalidDataException("Recovery input original bytes differ.");
    }

    private static ProvisioningEvidenceReference BuildRecoveryContext(DestroyRecoverySeed seed,
        string operation, ExactPlanMetadata metadata, string variables, string metadataPath)
    {
        ValidateRecoveryMetadata(seed, metadata);
        if (metadata.ActorId != RecoveryActor(seed, operation))
            throw new InvalidDataException("Fresh destroy plan lacks its signed recovery actor.");
        ValidateRecoveryInputs(seed, variables, metadataPath);
        return GetRecoveryStore().PutContext(new(ProvisioningRecoveryStore.Schema, seed.Root,
            seed.Parent.ProvisioningOperationId, seed.ParentObligationSha256, seed.Parent.Recovery?.Sha256,
            seed.Parent.Metadata.PlanMetadataDigest, seed.Parent.EvidenceRefs.Single(row => row.Kind == "approval").Sha256,
            seed.ReceiptSha256, seed.ClaimSha256, seed.Parent.HostBinding, seed.Parent.Stack, seed.Parent.Environment,
            seed.Parent.Metadata, operation, metadata.SavedPlanSha256, metadata.PlanMetadataDigest,
            seed.Parent.Metadata.StateLineageBefore, seed.MinimumSerial, seed.ParentOriginals));
    }
}
