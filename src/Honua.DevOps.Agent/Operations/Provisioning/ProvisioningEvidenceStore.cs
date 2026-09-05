using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace Honua.DevOps.Agent.Operations;

/// <summary>A reference to exact evidence bytes, not a serialized-object digest or an approval.</summary>
internal sealed record ProvisioningEvidenceReference(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("reference")] string Reference,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("byteLength")] long ByteLength);

/// <summary>
/// Protected immutable evidence retained independently of the diagnostic JSONL replica.
/// A release consumer resolves urn:sha256 references from this directory; no URL is fetched.
/// Terraform plans and signed approvals may be sensitive and must not be published wholesale.
/// </summary>
internal sealed class ProvisioningEvidenceStore(string directory)
{
    internal ProvisioningEvidenceReference Put(string kind, byte[] bytes)
    {
        string digest = Convert.ToHexStringLower(SHA256.HashData(bytes));
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        string destination = Path.Combine(directory, digest);
        string temporary = Path.Combine(directory, $".{Guid.NewGuid():n}.tmp");
        try
        {
            using (FileStream stream = new(temporary, new FileStreamOptions
            {
                Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None,
                UnixCreateMode = OperatingSystem.IsWindows() ? null : UnixFileMode.UserRead | UnixFileMode.UserWrite
            }))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            try { File.Move(temporary, destination); }
            catch (IOException) when (File.Exists(destination)) { /* Idempotent duplicate; verify below. */ }
        }
        finally { File.Delete(temporary); }
        ProvisioningEvidenceReference reference = new(kind, $"urn:sha256:{digest}", digest, bytes.LongLength);
        Read(reference);
        return reference;
    }

    internal byte[] Read(ProvisioningEvidenceReference reference)
    {
        if (reference.Sha256.Length != 64 || reference.Sha256.Any(c => !char.IsAsciiHexDigit(c))
            || reference.Reference != $"urn:sha256:{reference.Sha256}")
            throw new InvalidDataException("Evidence reference does not match its SHA-256 identity.");
        byte[] bytes = File.ReadAllBytes(Path.Combine(directory, reference.Sha256));
        if (bytes.LongLength != reference.ByteLength
            || Convert.ToHexStringLower(SHA256.HashData(bytes)) != reference.Sha256)
            throw new InvalidDataException("Evidence bytes do not match their retained digest and length.");
        return bytes;
    }

    internal void Validate(IReadOnlyList<ProvisioningEvidenceReference> references)
    {
        HashSet<string> kinds = new(StringComparer.Ordinal);
        foreach (ProvisioningEvidenceReference reference in references)
        {
            if (!kinds.Add(reference.Kind))
                throw new InvalidDataException($"Duplicate evidence kind: {reference.Kind}.");
            Read(reference);
        }
    }
}
