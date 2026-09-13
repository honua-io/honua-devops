using System.Collections.Concurrent;
using System.Text.Json;

using Honua.DevOps.Agent.Operations.Audit;

namespace Honua.DevOps.Agent.Operations.GitOps;

internal sealed record ManifestApplyCheckpoint(
    string OperationId,
    string DesiredStateDigest,
    string AcknowledgementDigest);

internal interface IManifestApplyCheckpointStore
{
    Task<ManifestApplyCheckpoint?> ReadAsync(
        string operationId,
        string desiredStateDigest,
        CancellationToken cancellationToken);

    Task CommitAsync(ManifestApplyCheckpoint checkpoint, CancellationToken cancellationToken);
}

internal static class ManifestApplyCheckpointStoreFactory
{
    internal static IManifestApplyCheckpointStore Create(string auditHookTarget)
    {
        if (OperationJournal.TryResolveJournalPath(auditHookTarget, out string journalPath, out _))
        {
            return new FileManifestApplyCheckpointStore($"{journalPath}.manifest-apply.jsonl");
        }

        // The control plane's idempotency key is the cross-process backstop when the audit
        // sink is stdout/stderr. A file-backed audit target additionally lets the agent skip
        // the replay request entirely after a clean acknowledgement commit.
        return new InMemoryManifestApplyCheckpointStore();
    }
}

internal sealed class InMemoryManifestApplyCheckpointStore : IManifestApplyCheckpointStore
{
    private readonly ConcurrentDictionary<string, ManifestApplyCheckpoint> _checkpoints = new(StringComparer.Ordinal);

    public Task<ManifestApplyCheckpoint?> ReadAsync(
        string operationId,
        string desiredStateDigest,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _checkpoints.TryGetValue(Key(operationId, desiredStateDigest), out ManifestApplyCheckpoint? checkpoint);
        return Task.FromResult(checkpoint);
    }

    public Task CommitAsync(ManifestApplyCheckpoint checkpoint, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _checkpoints[Key(checkpoint.OperationId, checkpoint.DesiredStateDigest)] = checkpoint;
        return Task.CompletedTask;
    }

    private static string Key(string operationId, string desiredStateDigest)
        => $"{operationId}\n{desiredStateDigest}";
}

internal sealed class FileManifestApplyCheckpointStore : IManifestApplyCheckpointStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly string _path;
    private readonly SemaphoreSlim _mutex = new(1, 1);

    internal FileManifestApplyCheckpointStore(string path)
    {
        _path = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    public async Task<ManifestApplyCheckpoint?> ReadAsync(
        string operationId,
        string desiredStateDigest,
        CancellationToken cancellationToken)
    {
        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_path))
            {
                return null;
            }

            ManifestApplyCheckpoint? match = null;
            foreach (string line in File.ReadLines(_path))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                try
                {
                    ManifestApplyCheckpoint? checkpoint = JsonSerializer.Deserialize<ManifestApplyCheckpoint>(line, SerializerOptions);
                    if (checkpoint is not null
                        && string.Equals(checkpoint.OperationId, operationId, StringComparison.Ordinal)
                        && string.Equals(checkpoint.DesiredStateDigest, desiredStateDigest, StringComparison.Ordinal))
                    {
                        match = checkpoint;
                    }
                }
                catch (JsonException)
                {
                    // A torn trailing line is not an acknowledgement. Keep the durable
                    // checkpoint fail-closed and allow the server idempotency key to decide
                    // whether a replay is a mutation or an observation.
                }
            }

            return match;
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task CommitAsync(ManifestApplyCheckpoint checkpoint, CancellationToken cancellationToken)
    {
        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using FileStream stream = new(
                _path,
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 4096,
                options: FileOptions.WriteThrough);
            await using StreamWriter writer = new(stream);
            await writer.WriteLineAsync(JsonSerializer.Serialize(checkpoint, SerializerOptions).AsMemory(), cancellationToken)
                .ConfigureAwait(false);
            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }
        finally
        {
            _mutex.Release();
        }
    }
}
