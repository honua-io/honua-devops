using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Honua.DevOps.Agent.Operations;

internal sealed record ProvisioningCleanupEvidence(
    string ProvisioningOperationId, string State, string Action, string PlanMetadataDigest,
    string PlanSha256, string ApprovalSha256, string? ExecutionReceiptSha256,
    IReadOnlyList<ProvisioningEvidenceReference> EvidenceRefs,
    bool CleanupOutstanding = true, bool CandidateQualified = false);

/// <summary>Private immutable origin, written after approval and before the existing actuator.</summary>
internal sealed record ProvisioningCleanupObligation(
    string SchemaVersion, string ProvisioningOperationId, string Stack, string Environment,
    string HostBinding, string PlanPath, string IacRoot, string ApplyScript,
    string ApplyScriptSha256, string? HelperSha256, string ClaimDirectory,
    ExactPlanMetadata Metadata, IReadOnlyList<ProvisioningEvidenceReference> EvidenceRefs,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    ProvisioningEvidenceReference? Recovery = null);

internal sealed record ProvisioningCleanupTerminal(
    int? ExitCode, bool Exited, ProvisioningEvidenceReference? Receipt,
    ProvisioningEvidenceReference? ClaimStatus, bool EvidenceInvalid = false);

/// <summary>Retains execution obligations, never executes or authorizes a cloud mutation.</summary>
internal sealed class ProvisioningCleanupStore(string root, ProvisioningEvidenceStore evidence)
{
    internal const string Schema = "honua.devops.cleanup-obligation/v1";

    internal string DirectoryFor(string operation)
    {
        if (!operation.StartsWith("urn:honua:provisioning:", StringComparison.Ordinal)
            || operation.Length != "urn:honua:provisioning:".Length + 32
            || !operation["urn:honua:provisioning:".Length..].All(char.IsAsciiHexDigit))
            throw new InvalidDataException("Invalid retained provisioning identity.");
        return Path.Combine(root, Digest(Encoding.UTF8.GetBytes(operation)));
    }

    internal string ClaimFor(string operation) => Path.Combine(DirectoryFor(operation), "claim");

    internal void Create(ProvisioningCleanupObligation obligation)
    {
        ValidateOrigin(obligation);
        string directory = DirectoryFor(obligation.ProvisioningOperationId);
        ProtectDirectory(root);
        ProtectDirectory(directory);
        WriteImmutable(Path.Combine(directory, "obligation.json"), JsonSerializer.SerializeToUtf8Bytes(obligation));
    }

    internal ProvisioningCleanupObligation Read(string operation)
    {
        RejectLink(root);
        RejectLink(DirectoryFor(operation));
        string path = Path.Combine(DirectoryFor(operation), "obligation.json");
        RejectLink(path);
        ProvisioningCleanupObligation value = JsonSerializer.Deserialize<ProvisioningCleanupObligation>(File.ReadAllBytes(path))
            ?? throw new InvalidDataException("Cleanup obligation is unavailable.");
        if (value.ProvisioningOperationId != operation) throw new InvalidDataException("Cleanup identity mismatch.");
        ValidateOrigin(value);
        return value;
    }

    private void ValidateOrigin(ProvisioningCleanupObligation value)
    {
        if (value.SchemaVersion != Schema || value.ClaimDirectory != ClaimFor(value.ProvisioningOperationId)
            || value.Metadata.Action is not ("apply" or "destroy")
            || value.ApplyScript != Path.Combine(value.IacRoot, "scripts", "terraform-exact-apply.sh"))
            throw new InvalidDataException("Cleanup origin identity mismatch.");
        evidence.Validate(value.EvidenceRefs);
        if (value.Recovery is not null)
        {
            DestroyRecoveryContext recovery = new ProvisioningRecoveryStore("", evidence).ReadContext(value.Recovery);
            if (recovery.ProvisioningOperationId != value.ProvisioningOperationId
                || recovery.PlanMetadataDigest != value.Metadata.PlanMetadataDigest
                || recovery.HostBinding != value.HostBinding
                || value.EvidenceRefs.Single(row => row.Kind == "destroy-recovery") != value.Recovery)
                throw new InvalidDataException("Cleanup recovery relationship differs.");
        }
        byte[] ReadKind(string kind) => evidence.Read(value.EvidenceRefs.Single(row => row.Kind == kind));
        if (!ExactPlanMetadata.TryRead(Encoding.UTF8.GetString(ReadKind("plan-metadata")),
                Encoding.UTF8.GetString(ReadKind("cleanup-plan-schema")), out ExactPlanMetadata? metadata, out _)
            || metadata != value.Metadata || Digest(ReadKind("plan")) != metadata!.SavedPlanSha256)
            throw new InvalidDataException("Cleanup original plan binding mismatch.");
        using JsonDocument approval = JsonDocument.Parse(ReadKind("approval"));
        JsonElement doc = approval.RootElement;
        if (doc.GetProperty("schemaVersion").GetString() != "honua.devops.provision-approval/v1"
            || doc.GetProperty("decision").GetString() != "approved"
            || doc.GetProperty("stack").GetString() != value.Stack
            || doc.GetProperty("environment").GetString() != value.Environment
            || doc.GetProperty("provisioningOperationId").GetString() != value.ProvisioningOperationId
            || doc.GetProperty("action").GetString() != metadata.Action
            || doc.GetProperty("planSha256").GetString() != metadata.SavedPlanSha256
            || doc.GetProperty("planMetadataDigest").GetString() != metadata.PlanMetadataDigest)
            throw new InvalidDataException("Cleanup original approval binding mismatch.");
    }

    internal void Capture(ProvisioningCleanupObligation origin, int? exitCode, bool exited,
        byte[]? receipt, byte[]? claimStatus, bool evidenceInvalid = false)
    {
        ProvisioningCleanupTerminal terminal = new(exitCode, exited,
            receipt is null ? null : evidence.Put("cleanup-execution", receipt),
            claimStatus is null ? null : evidence.Put("cleanup-claim", claimStatus), evidenceInvalid);
        WriteImmutable(Path.Combine(DirectoryFor(origin.ProvisioningOperationId), exited ? "terminal.json" : "provisional.json"),
            JsonSerializer.SerializeToUtf8Bytes(terminal));
    }

    internal ProvisioningCleanupTerminal? ReadTerminal(ProvisioningCleanupObligation origin, bool provisional = false)
    {
        ValidateClaimPath(origin);
        string path = Path.Combine(DirectoryFor(origin.ProvisioningOperationId), provisional ? "provisional.json" : "terminal.json");
        if (!File.Exists(path)) return null;
        RejectLink(path);
        return JsonSerializer.Deserialize<ProvisioningCleanupTerminal>(File.ReadAllBytes(path))
            ?? throw new InvalidDataException("Cleanup terminal observation is invalid.");
    }

    internal ProvisioningCleanupEvidence Inspect(ProvisioningCleanupObligation origin,
        byte[]? currentReceipt, byte[]? currentClaim, bool liveExecutor)
    {
        ValidateOrigin(origin);
        string? receiptDigest = null;
        ProvisioningCleanupEvidence Result(string state) => new(origin.ProvisioningOperationId, state,
            origin.Metadata.Action, origin.Metadata.PlanMetadataDigest, origin.Metadata.SavedPlanSha256,
            origin.EvidenceRefs.Single(row => row.Kind == "approval").Sha256, receiptDigest, origin.EvidenceRefs);
        if (liveExecutor) return Result("cleanup-live-executor");
        ProvisioningCleanupTerminal? terminal = ReadTerminal(origin);
        ProvisioningCleanupTerminal? provisional = ReadTerminal(origin, provisional: true);
        if (provisional?.EvidenceInvalid == true) throw new InvalidDataException("Provisional cleanup evidence was inconsistent.");
        if (provisional?.Receipt is { } earlyReceipt && currentReceipt is not null
            && !evidence.Read(earlyReceipt).AsSpan().SequenceEqual(currentReceipt))
            throw new InvalidDataException("Previously committed cleanup receipt changed.");
        if (provisional?.ClaimStatus is { } earlyClaim && currentClaim is not null)
        {
            using JsonDocument early = JsonDocument.Parse(evidence.Read(earlyClaim));
            using JsonDocument current = JsonDocument.Parse(currentClaim);
            static int Phase(JsonElement value) => value.GetProperty("phase").GetString() switch
            {
                null => 0, "preflight" => 1, "mutation-started" => 2, "terraform-acknowledged" => 3,
                "receipt-committed" => 4, _ => throw new InvalidDataException("Unknown cleanup claim phase.")
            };
            if (early.RootElement.GetProperty("claim_dir").GetString() != current.RootElement.GetProperty("claim_dir").GetString()
                || early.RootElement.GetProperty("executor_id").GetString() != current.RootElement.GetProperty("executor_id").GetString()
                || early.RootElement.GetProperty("acquired_at_utc").GetString() != current.RootElement.GetProperty("acquired_at_utc").GetString())
                throw new InvalidDataException("Cleanup claim original identity or progress changed.");
            // Canonical claim_abandon marks ambiguous mutation as reconciliation-required.
            // It supplies no ordered forward phase and cannot establish a completed execution.
            if (early.RootElement.GetProperty("phase").GetString() == "reconciliation-required"
                || current.RootElement.GetProperty("phase").GetString() == "reconciliation-required")
                return Result("cleanup-indeterminate");
            if (Phase(current.RootElement) < Phase(early.RootElement))
                throw new InvalidDataException("Cleanup claim original progress moved backwards.");
        }
        if (terminal?.EvidenceInvalid == true) throw new InvalidDataException("Cleanup evidence capture refused inconsistent originals.");
        byte[]? receipt = terminal?.Receipt is { } retainedReceipt ? evidence.Read(retainedReceipt) : currentReceipt;
        byte[]? claim = terminal?.ClaimStatus is { } retainedClaim ? evidence.Read(retainedClaim) : currentClaim;
        if (receipt is null) return Result("cleanup-indeterminate");
        receiptDigest = Digest(receipt);
        // Fresh original files must agree with captured originals; neither can silently replace the other.
        if (currentReceipt is not null && !currentReceipt.AsSpan().SequenceEqual(receipt)
            || currentClaim is not null && claim is not null && !EquivalentClaim(currentClaim, claim))
            throw new InvalidDataException("Cleanup original evidence changed.");
        string schema = Encoding.UTF8.GetString(evidence.Read(origin.EvidenceRefs.Single(row => row.Kind == "cleanup-exec-schema")));
        if (!TerraformExecReceipt.TryRead(Encoding.UTF8.GetString(receipt), schema, out TerraformExecReceipt? execution, out _)
            || execution!.FindPlanMismatches(origin.Metadata).Count != 0)
            throw new InvalidDataException("Cleanup execution differs from its approved original.");
        using (JsonDocument original = JsonDocument.Parse(receipt))
        {
            JsonElement cleanup = original.RootElement.GetProperty("cleanup");
            if (cleanup.GetProperty("claim_dir").GetString() != origin.ClaimDirectory
                || cleanup.GetProperty("saved_plan").GetString() != origin.PlanPath)
                throw new InvalidDataException("Cleanup execution claim/plan path differs from its origin.");
        }
        if (claim is null) return Result("cleanup-indeterminate");
        if (terminal?.Exited == false && (currentReceipt is null || currentClaim is null))
            return Result("cleanup-indeterminate");
        if (terminal?.Exited == true && terminal.ExitCode is { } code && code != execution.ExitStatus)
            throw new InvalidDataException("Cleanup process result differs from its original receipt.");
        using JsonDocument status = JsonDocument.Parse(claim);
        JsonElement doc = status.RootElement;
        string[] claimFields = ["schema_version", "kind", "claim_dir", "state", "acquired_at_utc", "completed_at_utc", "phase", "executor_id", "receipt_digest"];
        if (doc.ValueKind != JsonValueKind.Object
            || !doc.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal)
                .SequenceEqual(claimFields.Order(StringComparer.Ordinal), StringComparer.Ordinal)
            || string.IsNullOrWhiteSpace(doc.GetProperty("executor_id").GetString())
            || doc.GetProperty("schema_version").GetString() != "v1"
            || doc.GetProperty("kind").GetString() != "honua.iac.claim-status"
            || doc.GetProperty("claim_dir").GetString() != origin.ClaimDirectory
            || doc.GetProperty("receipt_digest").GetString() != receiptDigest)
            throw new InvalidDataException("Cleanup original claim binding mismatch.");
        if (doc.GetProperty("phase").GetString() != "receipt-committed"
            || doc.GetProperty("state").GetString() is not ("completed" or "reconciliation-required")
            || !doc.GetProperty("completed_at_utc").TryGetDateTimeOffset(out DateTimeOffset completed)
            || !doc.GetProperty("acquired_at_utc").TryGetDateTimeOffset(out DateTimeOffset acquired)
            || completed < acquired)
            return Result("cleanup-indeterminate");
        if (origin.Metadata.StateLineageBefore is { } previous && execution.StateLineageAfter != previous
            || origin.Metadata.StateSerialBefore is { } serial && execution.StateSerialAfter < serial)
            throw new InvalidDataException("Cleanup resulting state lineage mismatch.");
        return Result(execution.Succeeded ? "cleanup-execution-acknowledged" : "cleanup-execution-failed");
    }

    private static bool EquivalentClaim(byte[] left, byte[] right)
    {
        using JsonDocument a = JsonDocument.Parse(left);
        using JsonDocument b = JsonDocument.Parse(right);
        return JsonElement.DeepEquals(a.RootElement, b.RootElement);
    }

    internal void ValidateClaimPath(ProvisioningCleanupObligation origin)
    {
        RejectLink(root);
        RejectLink(DirectoryFor(origin.ProvisioningOperationId));
        if (Directory.Exists(origin.ClaimDirectory) || new DirectoryInfo(origin.ClaimDirectory).LinkTarget is not null)
            RejectLink(origin.ClaimDirectory);
    }

    internal static byte[]? ReadOriginal(string path)
    {
        if (!File.Exists(path)) return null;
        RejectLink(path);
        if (new FileInfo(path).Length > 1024 * 1024) throw new InvalidDataException("Cleanup original is oversized.");
        return File.ReadAllBytes(path);
    }

    internal static bool ExecutorIsLive(string claimDirectory)
    {
        string path = Path.Combine(claimDirectory, "executor_pid");
        byte[]? bytes = ReadOriginal(path);
        if (bytes is null) throw new InvalidDataException("Cleanup executor identity is missing.");
        if (!int.TryParse(Encoding.UTF8.GetString(bytes).Trim(), out int pid) || pid <= 0)
            throw new InvalidDataException("Cleanup executor identity is invalid.");
        try { using Process process = Process.GetProcessById(pid); return !process.HasExited; }
        catch (ArgumentException) { return false; }
    }

    internal static string Digest(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    internal static void RejectLink(string path)
    {
        FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
        if (info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Cleanup evidence links are refused.");
    }

    private static void ProtectDirectory(string path)
    {
        Directory.CreateDirectory(path);
        RejectLink(path);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static void WriteImmutable(string path, byte[] bytes)
    {
        FileStreamOptions options = new() { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, Options = FileOptions.WriteThrough };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        string temporary = Path.Combine(Path.GetDirectoryName(path)!, $".{Guid.NewGuid():n}.tmp");
        try
        {
            using (FileStream stream = new(temporary, options))
            {
                stream.Write(bytes);
                stream.Flush(true);
            }
            File.Move(temporary, path); // Never overwrite an immutable origin/observation.
        }
        finally { File.Delete(temporary); }
    }
}
