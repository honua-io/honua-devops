using System.Text;
using System.Text.Json;

using Honua.DevOps.Agent.Operations.Audit;

namespace Honua.DevOps.Agent.Operations.GitOps;

// Durable desired-intent ledger for protected deployment recovery (issue #191).
//
// One append-only version chain per target. Every approved sync/submit appends an `approved`
// record; a verified server recovery appends a `restored` record that names the prior revision
// as the desired revision and adds the rejected candidate to the quarantine set. The next
// DevOps reconcile reads the latest record and refuses a quarantined revision, so the recovered
// service stays on the prior revision until a corrected revision is approved.
//
// Every write is a compare-and-set against the version the caller observed. A newer approved
// intent makes a recovery write conflict instead of overwriting it, and a conflict or write
// failure is returned explicitly so no caller can report a converged outcome it did not record.
// This fences DevOps-originated intent only; the server must still fence its own mutation.
internal static class DesiredIntentKind
{
    internal const string Approved = "approved";
    internal const string Restored = "restored";

    // The server recovered the approved candidate, but DevOps could not verify which revision
    // it restored (a settled server RolledBack clears its protection window). The candidate is
    // quarantined; DesiredRevision is empty and no restoration is claimed.
    internal const string Rejected = "rejected";
}

internal sealed record DesiredIntentRecord(
    string Target,
    long Version,
    string Kind,
    string DesiredRevision,
    IReadOnlyList<string> RejectedRevisions,
    string OperationId,
    string? ApprovalReference,
    string Actor,
    string? CommitSha,
    DateTimeOffset RecordedAtUtc)
{
    internal bool IsQuarantined(string revision)
        => RejectedRevisions.Contains(revision, StringComparer.Ordinal);

    // The same logical write replayed after a lost acknowledgement or restart. Version and
    // timestamps are assigned per attempt and do not distinguish intent.
    internal bool SameIntentAs(DesiredIntentRecord other)
        => string.Equals(Target, other.Target, StringComparison.Ordinal)
            && string.Equals(Kind, other.Kind, StringComparison.Ordinal)
            && string.Equals(DesiredRevision, other.DesiredRevision, StringComparison.Ordinal)
            && string.Equals(OperationId, other.OperationId, StringComparison.Ordinal)
            && RejectedRevisions.Order(StringComparer.Ordinal).SequenceEqual(
                other.RejectedRevisions.Order(StringComparer.Ordinal), StringComparer.Ordinal);
}

internal sealed record DesiredIntentSnapshot(bool Readable, DesiredIntentRecord? Latest, string Detail);

internal enum DesiredIntentCommitStatus
{
    Committed,
    AlreadyRecorded,
    Conflict,
    WriteFailed
}

internal sealed record DesiredIntentCommitResult(
    DesiredIntentCommitStatus Status,
    DesiredIntentRecord? Latest,
    string Detail)
{
    internal bool Recorded => Status is DesiredIntentCommitStatus.Committed or DesiredIntentCommitStatus.AlreadyRecorded;
}

internal interface IDesiredIntentLedger
{
    Task<DesiredIntentSnapshot> ReadLatestAsync(string target, CancellationToken cancellationToken);

    // Appends `next` as version `expectedVersion + 1` only while the target's latest version is
    // still `expectedVersion` (0 when the target has no record).
    Task<DesiredIntentCommitResult> TryCommitAsync(
        long expectedVersion,
        DesiredIntentRecord next,
        CancellationToken cancellationToken);
}

// Server-reported recovery of an approved candidate becomes durable intent only through a
// compare-and-set against the record it recovers. A verified prior revision is recorded as
// restored; without one the candidate is still quarantined, but nothing claims restoration.
internal static class DesiredIntentRecovery
{
    internal static Task<DesiredIntentCommitResult> RecordAsync(
        IDesiredIntentLedger ledger,
        DesiredIntentRecord basis,
        string candidateRevision,
        string? restoredRevision,
        string actor,
        string? commitSha,
        DateTimeOffset recordedAtUtc,
        CancellationToken cancellationToken)
        => ledger.TryCommitAsync(
            basis.Version,
            new DesiredIntentRecord(
                basis.Target,
                Version: 0,
                restoredRevision is null ? DesiredIntentKind.Rejected : DesiredIntentKind.Restored,
                restoredRevision ?? string.Empty,
                [.. basis.RejectedRevisions.Union([candidateRevision], StringComparer.Ordinal)],
                basis.OperationId,
                basis.ApprovalReference,
                actor,
                commitSha,
                recordedAtUtc),
            cancellationToken);
}

internal static class DesiredIntentLedgerFactory
{
    // Null when the audit sink is not file-backed. An in-memory ledger would forget the
    // quarantine on restart, so protected recovery refuses instead of pretending durability.
    internal static IDesiredIntentLedger? Create(string auditHookTarget)
        => OperationJournal.TryResolveJournalPath(auditHookTarget, out string journalPath, out _)
            ? new FileDesiredIntentLedger($"{journalPath}.desired-intent.jsonl")
            : null;
}

// Thrown when a committed (non-trailing) ledger line fails to parse, distinguishing that from
// an unterminated final write so the ledger can fail closed instead of silently reading a
// stale version.
internal sealed class MalformedDesiredIntentLedgerException(string message) : Exception(message);

internal sealed class FileDesiredIntentLedger : IDesiredIntentLedger
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly string _path;
    private readonly TimeSpan _lockTimeout;

    internal FileDesiredIntentLedger(string path, TimeSpan? lockTimeout = null)
    {
        _path = Path.GetFullPath(path);
        _lockTimeout = lockTimeout ?? TimeSpan.FromSeconds(5);
    }

    public async Task<DesiredIntentSnapshot> ReadLatestAsync(string target, CancellationToken cancellationToken)
    {
        try
        {
            await using FileStream stream = await OpenExclusiveAsync(cancellationToken).ConfigureAwait(false);
            return new DesiredIntentSnapshot(true, ReadLatest(stream, target, _path), string.Empty);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or MalformedDesiredIntentLedgerException)
        {
            return new DesiredIntentSnapshot(false, null, $"Desired-intent ledger `{_path}` is unreadable: {exception.Message}");
        }
    }

    public async Task<DesiredIntentCommitResult> TryCommitAsync(
        long expectedVersion,
        DesiredIntentRecord next,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(next);
        try
        {
            // The exclusive handle spans read -> compare -> append, so two writers (in this
            // process or another) cannot both observe the same version and both append.
            await using FileStream stream = await OpenExclusiveAsync(cancellationToken).ConfigureAwait(false);
            DesiredIntentRecord? latest = ReadLatest(stream, next.Target, _path);
            if (latest is not null && latest.SameIntentAs(next))
            {
                return new DesiredIntentCommitResult(DesiredIntentCommitStatus.AlreadyRecorded, latest,
                    $"Desired intent for `{next.Target}` is already recorded at version {latest.Version}.");
            }

            long currentVersion = latest?.Version ?? 0;
            if (currentVersion != expectedVersion)
            {
                return new DesiredIntentCommitResult(DesiredIntentCommitStatus.Conflict, latest,
                    $"Desired intent for `{next.Target}` is at version {currentVersion} ({latest?.Kind} `{latest?.DesiredRevision}` " +
                    $"from operation `{latest?.OperationId}`), not the expected version {expectedVersion}.");
            }

            DesiredIntentRecord committed = next with { Version = expectedVersion + 1 };
            stream.Seek(0, SeekOrigin.End);
            if (stream.Length > 0)
            {
                // Start a torn trailing line's successor on a fresh line.
                stream.Seek(-1, SeekOrigin.End);
                int last = stream.ReadByte();
                if (last != '\n')
                {
                    stream.WriteByte((byte)'\n');
                }
            }

            byte[] line = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(committed, SerializerOptions) + "\n");
            await stream.WriteAsync(line, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
            return new DesiredIntentCommitResult(DesiredIntentCommitStatus.Committed, committed,
                $"Desired intent for `{next.Target}` recorded at version {committed.Version}.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or MalformedDesiredIntentLedgerException)
        {
            return new DesiredIntentCommitResult(DesiredIntentCommitStatus.WriteFailed, null,
                $"Desired-intent ledger `{_path}` write failed: {exception.Message}");
        }
    }

    private async Task<FileStream> OpenExclusiveAsync(CancellationToken cancellationToken)
    {
        string? directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        DateTimeOffset deadline = DateTimeOffset.UtcNow + _lockTimeout;
        while (true)
        {
            try
            {
                return new FileStream(_path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
                    bufferSize: 4096, FileOptions.WriteThrough);
            }
            catch (IOException) when (DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static DesiredIntentRecord? ReadLatest(FileStream stream, string target, string path)
    {
        stream.Position = 0;
        using StreamReader reader = new(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
        DesiredIntentRecord? latest = null;
        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                DesiredIntentRecord? record = JsonSerializer.Deserialize<DesiredIntentRecord>(line, SerializerOptions);
                if (record is not null
                    && string.Equals(record.Target, target, StringComparison.Ordinal)
                    && (latest is null || record.Version > latest.Version))
                {
                    latest = record;
                }
            }
            catch (JsonException)
            {
                if (reader.Peek() != -1)
                {
                    // A malformed line before the final one is real corruption, not an
                    // unterminated trailing write; fail closed rather than falling back to a
                    // stale version that could silently discard a restored/quarantine record.
                    throw new MalformedDesiredIntentLedgerException(
                        $"Desired-intent ledger `{path}` contains a malformed record before the final line for target `{target}`.");
                }

                // A torn trailing line was never acknowledged as committed; ignore it.
            }
        }

        return latest;
    }
}
