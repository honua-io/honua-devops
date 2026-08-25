using System.Text.Json;

namespace Honua.DevOps.Agent.Operations.Audit;

internal sealed record RecentOperation(
    string AuditEventId,
    string Timestamp,
    string Tool,
    string Status,
    string Summary,
    bool Mutated,
    string? ExecutionTier);

internal sealed record OperationSearchResult(
    string Status,
    string JournalPath,
    int Matched,
    int Scanned,
    IReadOnlyList<RecentOperation> Operations,
    string? Notice);

internal static class OperationJournal
{
    private const string FilePrefix = "file://";

    internal static bool TryResolveJournalPath(string auditHookTarget, out string path, out string? reason)
    {
        path = string.Empty;
        reason = null;

        string trimmed = auditHookTarget?.Trim() ?? string.Empty;
        if (!trimmed.StartsWith(FilePrefix, StringComparison.OrdinalIgnoreCase))
        {
            reason =
                "Operation journal lookup needs a file-backed audit sink. Set HONUA_DEVOPS_AUDIT_HOOK_TARGET=file:///path/to/audit.jsonl.";
            return false;
        }

        string rawPath = trimmed[FilePrefix.Length..];
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            reason = $"Audit hook target `{trimmed}` is missing a file path.";
            return false;
        }

        path = Path.GetFullPath(rawPath);
        return true;
    }

    internal static int ListRecent(string auditHookTarget, int limit, TextWriter writer)
    {
        if (!TryResolveJournalPath(auditHookTarget, out string path, out string? reason))
        {
            writer.WriteLine(reason);
            return 64;
        }

        if (!File.Exists(path))
        {
            writer.WriteLine($"No audit journal at `{path}` yet.");
            return 0;
        }

        IEnumerable<string> lines = File.ReadLines(path);
        List<string> tail = TakeLast(lines, limit);

        if (tail.Count == 0)
        {
            writer.WriteLine($"Audit journal at `{path}` is empty.");
            return 0;
        }

        writer.WriteLine($"audit-journal={path}  showing={tail.Count}/{limit}");
        foreach (string line in tail)
        {
            writer.WriteLine(SummarizeLine(line));
        }
        return 0;
    }

    internal static OperationSearchResult Find(
        string auditHookTarget,
        string? toolFilter,
        bool? mutatedOnly,
        string? statusContains,
        int limit)
    {
        if (limit < 1)
        {
            limit = 1;
        }

        if (!TryResolveJournalPath(auditHookTarget, out string path, out string? reason))
        {
            return new OperationSearchResult("audit-journal-unavailable", string.Empty, 0, 0, [], reason);
        }

        if (!File.Exists(path))
        {
            return new OperationSearchResult("audit-journal-empty", path, 0, 0, [], $"No audit journal at `{path}` yet.");
        }

        Queue<RecentOperation> window = new(limit);
        int scanned = 0;
        int matched = 0;
        string? toolNeedle = string.IsNullOrWhiteSpace(toolFilter) ? null : toolFilter.Trim();
        string? statusNeedle = string.IsNullOrWhiteSpace(statusContains) ? null : statusContains.Trim();

        foreach (string line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            scanned++;
            RecentOperation? parsed = TryParseRecord(line);
            if (parsed is null)
            {
                continue;
            }

            if (toolNeedle is not null && !parsed.Tool.Equals(toolNeedle, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (mutatedOnly == true && !parsed.Mutated)
            {
                continue;
            }

            if (statusNeedle is not null && parsed.Status.IndexOf(statusNeedle, StringComparison.OrdinalIgnoreCase) < 0)
            {
                continue;
            }

            matched++;
            if (window.Count == limit)
            {
                window.Dequeue();
            }
            window.Enqueue(parsed);
        }

        List<RecentOperation> tail = [.. window];
        tail.Reverse();
        return new OperationSearchResult(
            tail.Count == 0 ? "no-matching-operations" : "operations-found",
            path,
            matched,
            scanned,
            tail,
            tail.Count == 0 ? "No operations matched the filters." : null);
    }

    private static RecentOperation? TryParseRecord(string line)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            return new RecentOperation(
                AuditEventId: ReadAuditEventId(root),
                Timestamp: root.TryGetProperty("Timestamp", out JsonElement ts) && ts.ValueKind == JsonValueKind.String ? ts.GetString() ?? string.Empty : string.Empty,
                Tool: root.TryGetProperty("ToolName", out JsonElement tool) && tool.ValueKind == JsonValueKind.String ? tool.GetString() ?? string.Empty : string.Empty,
                Status: root.TryGetProperty("Status", out JsonElement st) && st.ValueKind == JsonValueKind.String ? st.GetString() ?? string.Empty : string.Empty,
                Summary: root.TryGetProperty("Summary", out JsonElement sm) && sm.ValueKind == JsonValueKind.String ? sm.GetString() ?? string.Empty : string.Empty,
                Mutated: root.TryGetProperty("Mutated", out JsonElement mu) && mu.ValueKind == JsonValueKind.True,
                ExecutionTier: root.TryGetProperty("ExecutionTier", out JsonElement tier) && tier.ValueKind == JsonValueKind.String ? tier.GetString() : null);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string ReadAuditEventId(JsonElement root)
    {
        // OperationId is accepted only as a read-compatibility alias for journals written before
        // honua-devops#150. New records serialize AuditEventId and never present this per-call GUID
        // as a canonical runtime operation identity.
        foreach (string name in new[] { "AuditEventId", "auditEventId", "OperationId", "operationId" })
        {
            if (root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String)
            {
                return value.GetString() ?? string.Empty;
            }
        }
        return string.Empty;
    }

    internal static int ShowOperation(string auditHookTarget, string operationId, TextWriter writer)
    {
        if (!TryResolveJournalPath(auditHookTarget, out string path, out string? reason))
        {
            writer.WriteLine(reason);
            return 64;
        }

        if (!File.Exists(path))
        {
            writer.WriteLine($"No audit journal at `{path}` yet.");
            return 65;
        }

        string needle = operationId.Trim();
        foreach (string line in File.ReadLines(path))
        {
            // Match on the parsed AuditEventId field, not a raw substring of the JSONL
            // line. A 32-char id can appear inside other fields (e.g. Summary) of an
            // earlier record, which would otherwise return the wrong record.
            RecentOperation? parsed = TryParseRecord(line);
            if (parsed is not null && string.Equals(parsed.AuditEventId, needle, StringComparison.Ordinal))
            {
                writer.WriteLine(line);
                return 0;
            }
        }

        writer.WriteLine($"No record with auditEventId `{needle}` found in `{path}`.");
        return 66;
    }

    private static List<string> TakeLast(IEnumerable<string> source, int count)
    {
        Queue<string> window = new(count);
        foreach (string line in source)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (window.Count == count)
            {
                window.Dequeue();
            }
            window.Enqueue(line);
        }

        return [.. window];
    }

    private static string SummarizeLine(string line)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement root = document.RootElement;
            string timestamp = TryGet(root, "Timestamp");
            string auditEventId = ReadAuditEventId(root);
            string toolName = TryGet(root, "ToolName");
            string status = TryGet(root, "Status");
            string mutated = root.TryGetProperty("Mutated", out JsonElement mutatedElement) && mutatedElement.ValueKind == JsonValueKind.True
                ? "mutated"
                : "read";
            string tier = TryGet(root, "ExecutionTier");
            string summary = TryGet(root, "Summary");
            if (summary.Length > 80)
            {
                summary = summary[..80] + "...";
            }
            return $"{timestamp}  {auditEventId}  {tool(toolName)}  [{mutated}/{tier}]  {status}  {summary}";
        }
        catch (JsonException)
        {
            return line;
        }

        static string TryGet(JsonElement root, string name) =>
            root.TryGetProperty(name, out JsonElement element) && element.ValueKind == JsonValueKind.String
                ? element.GetString() ?? string.Empty
                : string.Empty;

        static string tool(string name) => name.Length > 28 ? name[..28] : name.PadRight(28);
    }
}
