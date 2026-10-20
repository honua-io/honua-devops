using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.DevOps.Agent.Operations;

/// <summary>Exact original relationships; no approval or actuator authority.</summary>
internal sealed record DestroyRecoveryContext(
    string SchemaVersion,
    string RootProvisioningOperationId,
    string ParentProvisioningOperationId,
    string ParentObligationSha256,
    string? ParentRecoveryContextSha256,
    string ParentPlanMetadataDigest,
    string ParentApprovalSha256,
    string? ParentExecutionReceiptSha256,
    string? ParentClaimSha256,
    string HostBinding,
    string Stack,
    string Environment,
    ExactPlanMetadata ParentMetadata,
    string ProvisioningOperationId,
    string PlanSha256,
    string PlanMetadataDigest,
    string? StateLineage,
    long? MinimumStateSerial,
    ProvisioningEvidenceReference[] ParentOriginals);

internal sealed record ProvisioningRecoveryEvidence(
    [property: JsonPropertyName("rootProvisioningOperationId")] string RootProvisioningOperationId,
    [property: JsonPropertyName("parentProvisioningOperationId")] string ParentProvisioningOperationId,
    [property: JsonPropertyName("provisioningOperationId")] string ProvisioningOperationId,
    [property: JsonPropertyName("contextSha256")] string ContextSha256,
    [property: JsonPropertyName("evidenceRef")] ProvisioningEvidenceReference EvidenceRef);

/// <summary>One current successor per root; mutations use the same root lock.</summary>
internal sealed record DestroyRecoveryHead(
    string RootProvisioningOperationId,
    string ParentProvisioningOperationId,
    string ProvisioningOperationId,
    string RequestDigest,
    ProvisioningEvidenceReference? Context = null,
    OperationResponse? Response = null);

internal sealed class ProvisioningRecoveryStore(string directory, ProvisioningEvidenceStore evidence)
{
    internal const string Schema = "honua.devops.destroy-recovery/v1";

    internal static string Digest(byte[] value) => Convert.ToHexStringLower(SHA256.HashData(value));

    private string PathFor(string operation)
    {
        const string prefix = "urn:honua:provisioning:";
        if (!operation.StartsWith(prefix, StringComparison.Ordinal)
            || operation.Length != prefix.Length + 32 || !operation[prefix.Length..].All(char.IsAsciiHexDigit))
            throw new InvalidDataException("Invalid retained recovery identity.");
        return Path.Combine(directory, Digest(Encoding.UTF8.GetBytes(operation)) + ".json");
    }

    internal (DestroyRecoveryHead? Head, string? Sha256) Read(string root)
    {
        if (Directory.Exists(directory)) ProvisioningCleanupStore.RejectLink(directory);
        string path = PathFor(root);
        if (!File.Exists(path))
        {
            if (new FileInfo(path).LinkTarget is not null) throw new InvalidDataException("Recovery head links are refused.");
            return (null, null);
        }
        ProvisioningCleanupStore.RejectLink(path);
        byte[] bytes = ProvisioningCleanupStore.ReadOriginal(path)!;
        DestroyRecoveryHead value = JsonSerializer.Deserialize<DestroyRecoveryHead>(bytes)
            ?? throw new InvalidDataException("Recovery head is invalid.");
        if (value.RootProvisioningOperationId != root) throw new InvalidDataException("Recovery root differs.");
        if (value.Context is not null) ReadContext(value.Context);
        return (value, Digest(bytes));
    }

    internal async Task CompareExchangeAsync(string root, string? expectedSha256, DestroyRecoveryHead next,
        CancellationToken cancellationToken)
    {
        if (Read(root).Sha256 != expectedSha256 || next.RootProvisioningOperationId != root)
            throw new InvalidDataException("Recovery reservation changed.");
        Directory.CreateDirectory(directory);
        ProvisioningCleanupStore.RejectLink(directory);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        string path = PathFor(root), temporary = path + "." + Guid.NewGuid().ToString("n") + ".tmp";
        try
        {
            FileStreamOptions options = new() { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (FileStream stream = new(temporary, options))
            {
                await stream.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(next), cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            // The caller holds the existing root lineage lock throughout CAS.
            if (Read(root).Sha256 != expectedSha256) throw new InvalidDataException("Recovery reservation changed.");
            File.Move(temporary, path, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }

    internal ProvisioningEvidenceReference PutContext(DestroyRecoveryContext context)
    {
        Validate(context);
        ProvisioningEvidenceReference reference = evidence.Put("destroy-recovery", JsonSerializer.SerializeToUtf8Bytes(context));
        string attempts = Path.Combine(directory, "attempts");
        Directory.CreateDirectory(attempts);
        ProvisioningCleanupStore.RejectLink(directory);
        ProvisioningCleanupStore.RejectLink(attempts);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.SetUnixFileMode(attempts, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        string path = Path.Combine(attempts, Path.GetFileName(PathFor(context.ProvisioningOperationId)));
        FileStreamOptions options = new() { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, Options = FileOptions.WriteThrough };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (FileStream stream = new(path, options))
        {
            stream.Write(JsonSerializer.SerializeToUtf8Bytes(reference));
            stream.Flush(true);
        }
        return reference;
    }

    internal ProvisioningEvidenceReference? FindAttempt(string operation)
    {
        string attempts = Path.Combine(directory, "attempts");
        string path = Path.Combine(attempts, Path.GetFileName(PathFor(operation)));
        if (!Directory.Exists(attempts)) return null;
        ProvisioningCleanupStore.RejectLink(directory);
        ProvisioningCleanupStore.RejectLink(attempts);
        byte[]? original = ProvisioningCleanupStore.ReadOriginal(path);
        if (original is null) return null;
        ProvisioningEvidenceReference reference = JsonSerializer.Deserialize<ProvisioningEvidenceReference>(original)
            ?? throw new InvalidDataException("Recovery attempt index differs.");
        if (ReadContext(reference).ProvisioningOperationId != operation)
            throw new InvalidDataException("Recovery attempt identity differs.");
        return reference;
    }

    internal DestroyRecoveryContext ReadContext(ProvisioningEvidenceReference reference)
    {
        if (reference.Kind != "destroy-recovery") throw new InvalidDataException("Recovery evidence kind differs.");
        DestroyRecoveryContext value = JsonSerializer.Deserialize<DestroyRecoveryContext>(evidence.Read(reference))
            ?? throw new InvalidDataException("Recovery context is invalid.");
        Validate(value);
        foreach (ProvisioningEvidenceReference original in value.ParentOriginals) evidence.Read(original);
        return value;
    }

    private static void Validate(DestroyRecoveryContext value)
    {
        static bool Sha(string? text) => text is { Length: 64 } && text.All(char.IsAsciiHexDigit);
        if (value.SchemaVersion != Schema || !Sha(value.ParentObligationSha256)
            || !Sha(value.ParentPlanMetadataDigest) || !Sha(value.ParentApprovalSha256)
            || !Sha(value.PlanSha256) || !Sha(value.PlanMetadataDigest) || !Sha(value.HostBinding)
            || value.ParentOriginals is null || value.ParentOriginals.Length is < 4 or > 12
            || value.ParentOriginals.Select(row => row.Kind).Distinct(StringComparer.Ordinal).Count() != value.ParentOriginals.Length
            || value.ParentOriginals.SingleOrDefault(row => row.Kind == "recovery-parent-obligation")?.Sha256 != value.ParentObligationSha256
            || value.ParentOriginals.SingleOrDefault(row => row.Kind == "approval")?.Sha256 != value.ParentApprovalSha256
            || value.ParentOriginals.SingleOrDefault(row => row.Kind == "recovery-parent-execution")?.Sha256 != value.ParentExecutionReceiptSha256
            || value.ParentOriginals.SingleOrDefault(row => row.Kind == "recovery-parent-claim")?.Sha256 != value.ParentClaimSha256
            || value.ParentMetadata.Action != "destroy"
            || value.ParentMetadata.PlanMetadataDigest != value.ParentPlanMetadataDigest
            || value.ParentMetadata.StateLineageBefore != value.StateLineage
            || value.MinimumStateSerial < value.ParentMetadata.StateSerialBefore
            || value.ParentRecoveryContextSha256 is not null && !Sha(value.ParentRecoveryContextSha256)
            || value.ParentExecutionReceiptSha256 is not null && !Sha(value.ParentExecutionReceiptSha256)
            || value.ParentClaimSha256 is not null && !Sha(value.ParentClaimSha256))
            throw new InvalidDataException("Recovery original context differs.");
    }
}
