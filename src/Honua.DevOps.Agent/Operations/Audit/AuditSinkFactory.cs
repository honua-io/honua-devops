namespace Honua.DevOps.Agent.Operations.Audit;

internal static class AuditSinkFactory
{
    private const string FilePrefix = "file://";

    internal static IAuditSink Create(string auditHookTarget, bool reserveStdout = false)
    {
        string trimmed = auditHookTarget?.Trim() ?? string.Empty;
        if (string.Equals(trimmed, "none", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, "disabled", StringComparison.OrdinalIgnoreCase))
        {
            return new NullAuditSink();
        }

        if (trimmed.StartsWith(FilePrefix, StringComparison.OrdinalIgnoreCase))
        {
            string path = trimmed[FilePrefix.Length..];
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new InvalidOperationException(
                    "Audit hook target `file://` must include a file path.");
            }

            return JsonlAuditSink.ForFile(path);
        }

        if (string.Equals(trimmed, "stderr-evidence", StringComparison.OrdinalIgnoreCase))
        {
            return JsonlAuditSink.ForStderr();
        }

        // "stdout-evidence", "jsonl-stdout", anything else → JSONL on stdout.
        // When stdout is reserved for a wire protocol (MCP stdio), evidence is
        // re-routed to stderr instead of being dropped so every tool call still
        // emits exactly one JSONL audit record.
        return reserveStdout ? JsonlAuditSink.ForStderr() : JsonlAuditSink.ForStdout();
    }
}
