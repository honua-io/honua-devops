using System.Collections.Concurrent;

namespace Honua.DevOps.Agent.Operations.BugReport;

/// <summary>
/// Records which <c>eventId</c>s have already been accepted for filing so a
/// replayed or redelivered event never files a second issue. A seam so a durable
/// store (file/Redis) can replace the in-process default without touching the
/// handler.
/// </summary>
internal interface IEventIdempotencyStore
{
    /// <summary>
    /// Atomically records <paramref name="eventId"/> as processed. Returns
    /// <c>true</c> when this call was the first to claim the id (caller should
    /// proceed), <c>false</c> when it was already claimed (duplicate — caller must
    /// not file again).
    /// </summary>
    bool TryMarkProcessed(string eventId);
}

/// <summary>
/// Process-lifetime, thread-safe idempotency store. Good enough for a single
/// listener process; cross-restart duplicate protection is additionally provided
/// by the connector's duplicate-issue detection against the destination repo.
/// </summary>
internal sealed class InMemoryEventIdempotencyStore : IEventIdempotencyStore
{
    private readonly ConcurrentDictionary<string, byte> _seen = new(StringComparer.Ordinal);

    public bool TryMarkProcessed(string eventId)
        => !string.IsNullOrWhiteSpace(eventId) && _seen.TryAdd(eventId, 0);
}
