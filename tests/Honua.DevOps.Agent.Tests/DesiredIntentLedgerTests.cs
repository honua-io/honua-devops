using Honua.DevOps.Agent.Operations;
using Honua.DevOps.Agent.Operations.GitOps;

namespace Honua.DevOps.Agent.Tests;

// The durable desired-intent ledger behind protected recovery (issue #191): compare-and-set
// appends, restart durability, cross-writer exclusion and explicit write failure.
public sealed class DesiredIntentLedgerTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("honua-devops-intent-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task TryCommitAsync_AppendsOnlyAtTheObservedVersionAndSurvivesRestart()
    {
        string path = Path.Combine(_directory, "ledger.jsonl");
        FileDesiredIntentLedger ledger = new(path);

        DesiredIntentCommitResult first = await ledger.TryCommitAsync(0, Intent("release/2026.03", "op-1"), CancellationToken.None);
        DesiredIntentCommitResult stale = await ledger.TryCommitAsync(0, Intent("release/2026.04", "op-2"), CancellationToken.None);
        DesiredIntentCommitResult replay = await ledger.TryCommitAsync(0, Intent("release/2026.03", "op-1"), CancellationToken.None);
        DesiredIntentCommitResult next = await ledger.TryCommitAsync(1, Intent("release/2026.04", "op-2"), CancellationToken.None);

        Assert.Equal((DesiredIntentCommitStatus.Committed, 1L), (first.Status, first.Latest!.Version));
        Assert.Equal((DesiredIntentCommitStatus.Conflict, "release/2026.03"), (stale.Status, stale.Latest!.DesiredRevision));
        Assert.Equal((DesiredIntentCommitStatus.AlreadyRecorded, 1L), (replay.Status, replay.Latest!.Version));
        Assert.Equal((DesiredIntentCommitStatus.Committed, 2L), (next.Status, next.Latest!.Version));

        DesiredIntentSnapshot restarted = await new FileDesiredIntentLedger(path).ReadLatestAsync("prod-api", CancellationToken.None);
        Assert.True(restarted.Readable);
        Assert.Equal((2L, "release/2026.04", "op-2"), (restarted.Latest!.Version, restarted.Latest.DesiredRevision, restarted.Latest.OperationId));
        Assert.Null((await new FileDesiredIntentLedger(path).ReadLatestAsync("other-target", CancellationToken.None)).Latest);
        Assert.Equal(2, File.ReadAllLines(path).Length);
    }

    [Fact]
    public async Task TryCommitAsync_ConcurrentWritersAtTheSameVersion_ExactlyOneCommits()
    {
        string path = Path.Combine(_directory, "ledger.jsonl");
        const int writers = 16;

        DesiredIntentCommitResult[] results = await Task.WhenAll(Enumerable.Range(0, writers).Select(index => Task.Run(() =>
            new FileDesiredIntentLedger(path, TimeSpan.FromSeconds(30))
                .TryCommitAsync(0, Intent($"release/2026.{index + 10}", $"op-{index}"), CancellationToken.None))));

        Assert.Equal(1, results.Count(result => result.Status == DesiredIntentCommitStatus.Committed));
        Assert.Equal(writers - 1, results.Count(result => result.Status == DesiredIntentCommitStatus.Conflict));
        Assert.Single(File.ReadAllLines(path));
    }

    [Fact]
    public async Task TryCommitAsync_TornTrailingLineIsIgnoredAndTheNextRecordStartsOnItsOwnLine()
    {
        string path = Path.Combine(_directory, "ledger.jsonl");
        FileDesiredIntentLedger ledger = new(path);
        await ledger.TryCommitAsync(0, Intent("release/2026.03", "op-1"), CancellationToken.None);
        await File.AppendAllTextAsync(path, "{\"target\":\"prod-api\",\"version\":2,\"kind\":\"appr");

        DesiredIntentCommitResult next = await ledger.TryCommitAsync(1, Intent("release/2026.04", "op-2"), CancellationToken.None);

        Assert.Equal((DesiredIntentCommitStatus.Committed, 2L), (next.Status, next.Latest!.Version));
        Assert.Equal((2L, "op-2"), await LatestAsync(new FileDesiredIntentLedger(path)));
        Assert.Equal(3, File.ReadAllLines(path).Length);
    }

    [Fact]
    public async Task UnwritableLedger_ReportsWriteFailureAndUnreadableSnapshot()
    {
        // The ledger path is an existing directory: no handle can be opened for it.
        FileDesiredIntentLedger ledger = new(_directory, TimeSpan.FromMilliseconds(100));

        DesiredIntentCommitResult commit = await ledger.TryCommitAsync(0, Intent("release/2026.03", "op-1"), CancellationToken.None);
        DesiredIntentSnapshot snapshot = await ledger.ReadLatestAsync("prod-api", CancellationToken.None);

        Assert.Equal(DesiredIntentCommitStatus.WriteFailed, commit.Status);
        Assert.False(commit.Recorded);
        Assert.False(snapshot.Readable);
    }

    [Fact]
    public void Factory_RequiresAFileBackedAuditTarget()
    {
        Assert.Null(DesiredIntentLedgerFactory.Create("stdout-evidence"));
        IDesiredIntentLedger? ledger = DesiredIntentLedgerFactory.Create($"file://{Path.Combine(_directory, "audit.jsonl")}");
        Assert.IsType<FileDesiredIntentLedger>(ledger);
        Assert.NotNull(ReleaseCapabilityGate.GetProtectedRecoveryIntentLedgerRefusal(null));
        Assert.Null(ReleaseCapabilityGate.GetProtectedRecoveryIntentLedgerRefusal(ledger));
    }

    [Fact]
    public void QuarantinedRevisionRefusal_AppliesOnlyToRejectedRevisions()
    {
        DesiredIntentRecord restored = Intent("release/2026.02", "op-recover-1") with
        {
            Version = 2,
            Kind = DesiredIntentKind.Restored,
            RejectedRevisions = ["release/2026.03"]
        };

        string? refusal = ReleaseCapabilityGate.GetQuarantinedRevisionRefusal(restored, "release/2026.03");

        Assert.NotNull(refusal);
        Assert.Contains("op-recover-1", refusal, StringComparison.Ordinal);
        Assert.Null(ReleaseCapabilityGate.GetQuarantinedRevisionRefusal(restored, "release/2026.04"));
        Assert.Null(ReleaseCapabilityGate.GetQuarantinedRevisionRefusal(restored, "release/2026.02"));
        Assert.Null(ReleaseCapabilityGate.GetQuarantinedRevisionRefusal(null, "release/2026.03"));
    }

    private static async Task<(long Version, string OperationId)> LatestAsync(FileDesiredIntentLedger ledger)
    {
        DesiredIntentRecord latest = (await ledger.ReadLatestAsync("prod-api", CancellationToken.None)).Latest!;
        return (latest.Version, latest.OperationId);
    }

    private static DesiredIntentRecord Intent(string revision, string operationId)
        => new("prod-api", 0, DesiredIntentKind.Approved, revision, [], operationId,
            $"approval-{operationId}", "operator@honua.io", null, DateTimeOffset.UtcNow);
}
