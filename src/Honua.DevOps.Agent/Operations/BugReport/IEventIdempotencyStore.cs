using System.Collections.Concurrent;

namespace Honua.DevOps.Agent.Operations.BugReport;

/// <summary>
/// Records which <c>eventId</c>s have already been filed so a replayed or
/// redelivered event never files a second issue. A seam so a durable store
/// (file/Redis) can replace the in-process default without touching the handler.
/// </summary>
internal interface IEventIdempotencyStore
{
    /// <summary>
    /// True when <paramref name="eventId"/> has already been claimed (a prior
    /// delivery filed successfully). A read-only fast path so a redelivery can be
    /// short-circuited without re-hitting the destination repo.
    /// </summary>
    bool IsProcessed(string eventId);

    /// <summary>
    /// Atomically records <paramref name="eventId"/> as processed. Returns
    /// <c>true</c> when this call was the first to claim the id, <c>false</c> when
    /// it was already claimed. Claimed ONLY after a confirmed successful file so a
    /// transient failure leaves the id free for the sender to retry.
    /// </summary>
    bool TryMarkProcessed(string eventId);
}

/// <summary>
/// Process-lifetime, thread-safe fallback store bounded by a retention window
/// and a hard capacity cap so it cannot grow for the process lifetime. An event
/// older than the replay window is rejected upstream on freshness grounds, so an
/// entry only needs to outlive that window; expired entries are swept on write
/// and a capacity cap evicts the oldest as a safety net. Cross-restart duplicate
/// protection is additionally provided by the connector's duplicate-issue
/// detection against the destination repo. Production listener startup uses the
/// durable file backend unless memory mode is explicitly configured.
/// </summary>
internal sealed class InMemoryEventIdempotencyStore : IEventIdempotencyStore
{
    private const int DefaultMaxEntries = 100_000;
    private static readonly TimeSpan DefaultRetention = TimeSpan.FromHours(1);

    private readonly ConcurrentDictionary<string, DateTimeOffset> _seen = new(StringComparer.Ordinal);
    private readonly TimeSpan _retention;
    private readonly int _maxEntries;
    private readonly Func<DateTimeOffset> _now;

    internal InMemoryEventIdempotencyStore()
        : this(DefaultRetention)
    {
    }

    internal InMemoryEventIdempotencyStore(TimeSpan retention, Func<DateTimeOffset>? now = null, int maxEntries = DefaultMaxEntries)
    {
        _retention = retention > TimeSpan.Zero ? retention : DefaultRetention;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _maxEntries = maxEntries > 0 ? maxEntries : DefaultMaxEntries;
    }

    /// <summary>Live entry count — for eviction tests and diagnostics.</summary>
    internal int Count => _seen.Count;

    public bool IsProcessed(string eventId)
    {
        if (string.IsNullOrWhiteSpace(eventId))
        {
            return false;
        }

        if (!_seen.TryGetValue(eventId, out DateTimeOffset stamp))
        {
            return false;
        }

        if (_now() - stamp <= _retention)
        {
            return true;
        }

        // Expired: drop it so a later delivery is treated as first-seen.
        _seen.TryRemove(eventId, out _);
        return false;
    }

    public bool TryMarkProcessed(string eventId)
    {
        if (string.IsNullOrWhiteSpace(eventId))
        {
            return false;
        }

        EvictExpired();

        if (!_seen.TryAdd(eventId, _now()))
        {
            return false;
        }

        EnforceCapacity();
        return true;
    }

    private void EvictExpired()
    {
        DateTimeOffset cutoff = _now() - _retention;
        foreach (KeyValuePair<string, DateTimeOffset> entry in _seen)
        {
            if (entry.Value < cutoff)
            {
                _seen.TryRemove(entry.Key, out _);
            }
        }
    }

    private void EnforceCapacity()
    {
        if (_seen.Count <= _maxEntries)
        {
            return;
        }

        // Best-effort under concurrency: drop the oldest entries until back under
        // the cap so the store can never grow without bound.
        foreach (KeyValuePair<string, DateTimeOffset> entry in _seen.OrderBy(pair => pair.Value))
        {
            if (_seen.Count <= _maxEntries)
            {
                break;
            }

            _seen.TryRemove(entry.Key, out _);
        }
    }
}
