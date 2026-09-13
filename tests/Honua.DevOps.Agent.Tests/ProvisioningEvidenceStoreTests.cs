using Honua.DevOps.Agent.Operations;

namespace Honua.DevOps.Agent.Tests;

public sealed class ProvisioningEvidenceStoreTests
{
    [Fact]
    public void References_RejectMissingMismatchedSubstitutedAndDuplicateEvidence()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"lineage-evidence-{Guid.NewGuid():n}");
        try
        {
            ProvisioningEvidenceStore store = new(directory);
            ProvisioningEvidenceReference reference = store.Put("plan", "abc"u8.ToArray());
            // Published SHA-256 test vector, independent of the store's hash implementation.
            Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", reference.Sha256);
            Assert.Equal(3, reference.ByteLength);
            Assert.Equal(reference, store.Put("plan", "abc"u8.ToArray()));
            Assert.Single(Directory.GetFiles(directory));
            Assert.Throws<InvalidDataException>(() => store.Read(reference with { Reference = "urn:sha256:" + new string('0', 64) }));
            Assert.Throws<InvalidDataException>(() => store.Read(reference with { ByteLength = 4 }));
            Assert.Throws<InvalidDataException>(() => store.Validate([reference, reference]));
            File.WriteAllBytes(Path.Combine(directory, reference.Sha256), "abd"u8.ToArray());
            Assert.Throws<InvalidDataException>(() => store.Read(reference));
            Assert.Throws<InvalidDataException>(() => store.Put("plan", "abc"u8.ToArray()));
            File.Delete(Path.Combine(directory, reference.Sha256));
            Assert.Throws<FileNotFoundException>(() => store.Read(reference));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
