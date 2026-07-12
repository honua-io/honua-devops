using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace Honua.DevOps.Agent.Operations.BugReport;

/// <summary>
/// Durable, process-safe event idempotency store backed by one bounded JSON file.
/// Each operation takes an exclusive sidecar-file lock, reloads current state,
/// compacts expired/over-capacity entries, and replaces the state file atomically.
/// Bug-report traffic is intentionally low volume, making this simpler to operate
/// and recover than an external database while remaining safe across restarts.
/// </summary>
internal sealed class FileEventIdempotencyStore : IEventIdempotencyStore
{
    private const int DefaultMaxEntries = 100_000;
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(10);
    private static readonly ConcurrentDictionary<string, object> LocalLocks = new(StringComparer.Ordinal);

    private readonly string _path;
    private readonly string _lockPath;
    private readonly TimeSpan _retention;
    private readonly int _maxEntries;
    private readonly Func<DateTimeOffset> _now;
    private readonly object _localLock;

    internal FileEventIdempotencyStore(
        string path,
        TimeSpan retention,
        int maxEntries = DefaultMaxEntries,
        Func<DateTimeOffset>? now = null)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("A durable idempotency state path is required.", nameof(path));
        }

        _path = Path.GetFullPath(path);
        _lockPath = $"{_path}.lock";
        _retention = retention > TimeSpan.Zero
            ? retention
            : throw new ArgumentOutOfRangeException(nameof(retention), "Retention must be positive.");
        _maxEntries = maxEntries > 0
            ? maxEntries
            : throw new ArgumentOutOfRangeException(nameof(maxEntries), "Capacity must be positive.");
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _localLock = LocalLocks.GetOrAdd(_path, static _ => new object());

        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        // Normalize/compact existing state and prove atomic writes work before
        // the listener binds. Durable mode must never degrade silently at runtime.
        Execute(static _ => true);
    }

    /// <summary>Current retained entry count, exposed for diagnostics and tests.</summary>
    internal int Count => Execute(entries => (false, entries.Count));

    public bool IsProcessed(string eventId)
    {
        if (string.IsNullOrWhiteSpace(eventId))
        {
            return false;
        }

        return Execute(entries => (false, entries.ContainsKey(eventId)));
    }

    public bool TryMarkProcessed(string eventId)
    {
        if (string.IsNullOrWhiteSpace(eventId))
        {
            return false;
        }

        return Execute(entries =>
        {
            if (entries.ContainsKey(eventId))
            {
                return (false, false);
            }

            entries.Add(eventId, _now());
            EnforceCapacity(entries);
            return (true, true);
        });
    }

    private void Execute(Func<Dictionary<string, DateTimeOffset>, bool> operation)
        => Execute(entries => (operation(entries), true));

    private TResult Execute<TResult>(Func<Dictionary<string, DateTimeOffset>, (bool Changed, TResult Result)> operation)
    {
        lock (_localLock)
        {
            using FileStream fileLock = AcquireFileLock();
            Dictionary<string, DateTimeOffset> entries = Load();
            bool compacted = PruneExpired(entries) | EnforceCapacity(entries);
            (bool changed, TResult result) = operation(entries);
            if (compacted || changed)
            {
                Persist(entries);
            }

            return result;
        }
    }

    private FileStream AcquireFileLock()
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                return new FileStream(
                    _lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.None);
            }
            catch (IOException) when (stopwatch.Elapsed < LockTimeout)
            {
                Thread.Sleep(10);
            }
        }
    }

    private Dictionary<string, DateTimeOffset> Load()
    {
        if (!File.Exists(_path))
        {
            return new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        }

        try
        {
            using FileStream stream = new(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using JsonDocument document = JsonDocument.Parse(stream);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("version", out JsonElement version)
                || version.GetInt32() != 1
                || !root.TryGetProperty("entries", out JsonElement serializedEntries)
                || serializedEntries.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException("Durable idempotency state has an unsupported shape or version.");
            }

            Dictionary<string, DateTimeOffset> entries = new(StringComparer.Ordinal);
            foreach (JsonElement serializedEntry in serializedEntries.EnumerateArray())
            {
                string? eventId = serializedEntry.GetProperty("eventId").GetString();
                long processedAtUnixMilliseconds = serializedEntry.GetProperty("processedAtUnixMs").GetInt64();
                if (string.IsNullOrWhiteSpace(eventId))
                {
                    throw new InvalidDataException("Durable idempotency state contains a blank eventId.");
                }

                DateTimeOffset processedAt = DateTimeOffset.FromUnixTimeMilliseconds(processedAtUnixMilliseconds);
                if (!entries.TryGetValue(eventId, out DateTimeOffset existing) || processedAt > existing)
                {
                    entries[eventId] = processedAt;
                }
            }

            return entries;
        }
        catch (Exception exception) when (exception is JsonException
            or InvalidOperationException
            or FormatException
            or ArgumentOutOfRangeException
            or KeyNotFoundException)
        {
            throw new InvalidDataException(
                $"Durable bug-report idempotency state `{_path}` is invalid; preserve it for recovery and repair or move it aside explicitly.",
                exception);
        }
    }

    private bool PruneExpired(Dictionary<string, DateTimeOffset> entries)
    {
        DateTimeOffset cutoff = _now() - _retention;
        string[] expired = entries
            .Where(entry => entry.Value < cutoff)
            .Select(entry => entry.Key)
            .ToArray();
        foreach (string eventId in expired)
        {
            entries.Remove(eventId);
        }

        return expired.Length > 0;
    }

    private bool EnforceCapacity(Dictionary<string, DateTimeOffset> entries)
    {
        int excess = entries.Count - _maxEntries;
        if (excess <= 0)
        {
            return false;
        }

        foreach (string eventId in entries
                     .OrderBy(entry => entry.Value)
                     .ThenBy(entry => entry.Key, StringComparer.Ordinal)
                     .Take(excess)
                     .Select(entry => entry.Key)
                     .ToArray())
        {
            entries.Remove(eventId);
        }

        return true;
    }

    private void Persist(Dictionary<string, DateTimeOffset> entries)
    {
        string temporaryPath = $"{_path}.{Environment.ProcessId}.{Guid.NewGuid():n}.tmp";
        try
        {
            using (FileStream stream = new(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 4096,
                       FileOptions.WriteThrough))
            {
                using (Utf8JsonWriter writer = new(stream, new JsonWriterOptions { Indented = true }))
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("version", 1);
                    writer.WriteStartArray("entries");
                    foreach (KeyValuePair<string, DateTimeOffset> entry in entries
                                 .OrderBy(entry => entry.Value)
                                 .ThenBy(entry => entry.Key, StringComparer.Ordinal))
                    {
                        writer.WriteStartObject();
                        writer.WriteString("eventId", entry.Key);
                        writer.WriteNumber("processedAtUnixMs", entry.Value.ToUnixTimeMilliseconds());
                        writer.WriteEndObject();
                    }

                    writer.WriteEndArray();
                    writer.WriteEndObject();
                    writer.Flush();
                }

                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}
