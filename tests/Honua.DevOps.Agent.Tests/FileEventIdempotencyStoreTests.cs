using Honua.DevOps.Agent.Operations.BugReport;

namespace Honua.DevOps.Agent.Tests;

public sealed class FileEventIdempotencyStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"honua-devops-idempotency-{Guid.NewGuid():n}");

    [Fact]
    public void ProcessedEvent_SurvivesStoreRestart()
    {
        string path = StatePath();
        FileEventIdempotencyStore first = new(path, TimeSpan.FromHours(1));

        Assert.True(first.TryMarkProcessed("evt-restart"));

        FileEventIdempotencyStore restarted = new(path, TimeSpan.FromHours(1));
        Assert.True(restarted.IsProcessed("evt-restart"));
        Assert.False(restarted.TryMarkProcessed("evt-restart"));
    }

    [Fact]
    public void ConcurrentStoreInstances_ClaimEventExactlyOnce()
    {
        const int contenders = 32;
        string path = StatePath();
        FileEventIdempotencyStore[] stores = Enumerable.Range(0, contenders)
            .Select(_ => new FileEventIdempotencyStore(path, TimeSpan.FromHours(1)))
            .ToArray();
        int successfulClaims = 0;

        Parallel.ForEach(stores, store =>
        {
            if (store.TryMarkProcessed("evt-concurrent"))
            {
                Interlocked.Increment(ref successfulClaims);
            }
        });

        Assert.Equal(1, successfulClaims);
        Assert.True(new FileEventIdempotencyStore(path, TimeSpan.FromHours(1))
            .IsProcessed("evt-concurrent"));
    }

    [Fact]
    public void ExpiredEntries_AreRemovedFromDurableState()
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-07-01T12:00:00Z");
        string path = StatePath();
        FileEventIdempotencyStore store = new(
            path,
            TimeSpan.FromMinutes(5),
            now: () => now);
        Assert.True(store.TryMarkProcessed("evt-expired"));

        now = now.AddMinutes(6);

        Assert.False(new FileEventIdempotencyStore(
            path,
            TimeSpan.FromMinutes(5),
            now: () => now).IsProcessed("evt-expired"));
        Assert.DoesNotContain("evt-expired", File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Fact]
    public void CapacityCap_PersistsOnlyNewestEntries()
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-07-01T12:00:00Z");
        string path = StatePath();
        FileEventIdempotencyStore store = new(
            path,
            TimeSpan.FromHours(1),
            maxEntries: 3,
            now: () => now);

        for (int index = 0; index < 5; index++)
        {
            now = now.AddSeconds(1);
            Assert.True(store.TryMarkProcessed($"evt-{index}"));
        }

        FileEventIdempotencyStore restarted = new(
            path,
            TimeSpan.FromHours(1),
            maxEntries: 3,
            now: () => now);
        Assert.Equal(3, restarted.Count);
        Assert.False(restarted.IsProcessed("evt-0"));
        Assert.False(restarted.IsProcessed("evt-1"));
        Assert.True(restarted.IsProcessed("evt-4"));
    }

    [Fact]
    public void LoweredCapacity_CompactsExistingStateOnRestart()
    {
        string path = StatePath();
        FileEventIdempotencyStore original = new(path, TimeSpan.FromHours(1), maxEntries: 10);
        for (int index = 0; index < 10; index++)
        {
            Assert.True(original.TryMarkProcessed($"evt-{index}"));
        }

        FileEventIdempotencyStore restarted = new(path, TimeSpan.FromHours(1), maxEntries: 3);

        Assert.Equal(3, restarted.Count);
        Assert.Equal(3, new FileEventIdempotencyStore(path, TimeSpan.FromHours(1), maxEntries: 3).Count);
    }

    [Fact]
    public void CorruptState_FailsClosedWithoutReplacingEvidence()
    {
        string path = StatePath();
        Directory.CreateDirectory(_directory);
        File.WriteAllText(path, "{not-json");

        InvalidDataException exception = Assert.Throws<InvalidDataException>(
            () => new FileEventIdempotencyStore(path, TimeSpan.FromHours(1)));

        Assert.Contains("idempotency", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("{not-json", File.ReadAllText(path));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private string StatePath() => Path.Combine(_directory, "event-ids.json");
}
