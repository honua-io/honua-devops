namespace Honua.DevOps.Agent.Operations.Audit;

/// <summary>Verifies that a configured durable sink can append and flush before actuation.</summary>
internal static class DurableAuditGate
{
    internal static bool TryProbe(string target, out string reason)
    {
        reason = string.Empty;
        if (string.IsNullOrWhiteSpace(target))
        {
            reason = "audit sink is not configured";
            return false;
        }

        if (!target.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            // Stream/remote sinks are probed by their owning transport. Preserve the
            // existing stdout contract while making the production file route concrete.
            return true;
        }

        string path = target["file://".Length..];
        if (string.IsNullOrWhiteSpace(path))
        {
            reason = "audit file path is empty";
            return false;
        }

        try
        {
            string fullPath = Path.GetFullPath(path);
            string? parent = Path.GetDirectoryName(fullPath);
            if (string.IsNullOrWhiteSpace(parent) || !Directory.Exists(parent))
            {
                reason = "audit file parent does not exist";
                return false;
            }

            using FileStream stream = new(
                fullPath,
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 1,
                FileOptions.WriteThrough);
            stream.Flush(flushToDisk: true);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            reason = $"audit sink durability probe failed ({exception.GetType().Name})";
            return false;
        }
    }
}
