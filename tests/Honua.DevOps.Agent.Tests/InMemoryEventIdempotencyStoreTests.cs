using Honua.DevOps.Agent.Operations.BugReport;

namespace Honua.DevOps.Agent.Tests;

public class InMemoryEventIdempotencyStoreTests
{
    [Fact]
    public void FirstClaim_Succeeds_SecondClaim_IsDuplicate()
    {
        InMemoryEventIdempotencyStore store = new();

        Assert.False(store.IsProcessed("evt-1"));
        Assert.True(store.TryMarkProcessed("evt-1"));
        Assert.True(store.IsProcessed("evt-1"));
        Assert.False(store.TryMarkProcessed("evt-1"));
    }

    [Fact]
    public void BlankEventId_IsNeverClaimed()
    {
        InMemoryEventIdempotencyStore store = new();

        Assert.False(store.IsProcessed(" "));
        Assert.False(store.TryMarkProcessed(""));
        Assert.False(store.TryMarkProcessed(null!));
    }

    [Fact]
    public void Entry_ExpiresAfterRetentionWindow()
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-06-01T12:00:00Z");
        InMemoryEventIdempotencyStore store = new(TimeSpan.FromMinutes(5), () => now);

        Assert.True(store.TryMarkProcessed("evt-ttl"));
        Assert.True(store.IsProcessed("evt-ttl"));

        // Still inside the window.
        now = now.AddMinutes(4);
        Assert.True(store.IsProcessed("evt-ttl"));

        // Past the window: the entry is evicted and a re-claim is treated as new.
        now = now.AddMinutes(2);
        Assert.False(store.IsProcessed("evt-ttl"));
        Assert.True(store.TryMarkProcessed("evt-ttl"));
    }

    [Fact]
    public void ExpiredEntries_AreSweptOnWrite_SoTheStoreDoesNotGrowUnbounded()
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-06-01T12:00:00Z");
        InMemoryEventIdempotencyStore store = new(TimeSpan.FromMinutes(1), () => now);

        for (int i = 0; i < 50; i++)
        {
            Assert.True(store.TryMarkProcessed($"evt-{i}"));
        }
        Assert.Equal(50, store.Count);

        // Advance past the retention window and write once more: the sweep drops all
        // expired entries, leaving only the fresh one.
        now = now.AddMinutes(5);
        Assert.True(store.TryMarkProcessed("evt-fresh"));
        Assert.Equal(1, store.Count);
    }

    [Fact]
    public void CapacityCap_EvictsOldestEntries()
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-06-01T12:00:00Z");
        InMemoryEventIdempotencyStore store = new(TimeSpan.FromHours(1), () => now, maxEntries: 10);

        for (int i = 0; i < 25; i++)
        {
            now = now.AddSeconds(1);
            Assert.True(store.TryMarkProcessed($"evt-{i}"));
        }

        Assert.True(store.Count <= 10);
        // The most recent id is retained; the oldest were evicted by the cap.
        Assert.True(store.IsProcessed("evt-24"));
        Assert.False(store.IsProcessed("evt-0"));
    }
}
