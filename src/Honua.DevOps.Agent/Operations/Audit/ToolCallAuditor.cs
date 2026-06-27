using System.Text.Json;

namespace Honua.DevOps.Agent.Operations.Audit;

internal sealed record AuditContext(
    string SessionId,
    string ExecutionMode,
    string ExecutionTier,
    string ApprovalMode,
    string Provider,
    IAuditSink Sink);

internal sealed record ToolCallRecord(string ToolName, IDictionary<string, object?>? Arguments);

/// <summary>
/// Shared host-side audit emission for tool calls. Both the interactive agent host and the
/// MCP stdio server emit one JSONL audit record per tool call through this type so the
/// audit contract stays identical regardless of how the toolset is invoked.
/// </summary>
internal static class ToolCallAuditor
{
    internal static async Task EmitAsync(
        AuditContext context,
        ToolCallRecord call,
        object? toolResult,
        CancellationToken cancellationToken)
    {
        if (context.Sink is NullAuditSink)
        {
            return;
        }

        string operationId = Guid.NewGuid().ToString("n");
        Dictionary<string, string> arguments = new(StringComparer.Ordinal);
        if (call.Arguments is not null)
        {
            foreach (KeyValuePair<string, object?> kvp in call.Arguments)
            {
                string raw = kvp.Value?.ToString() ?? "null";
                // Redact by argument key as well as content: a value passed under a
                // sensitive key (e.g. apiKey/token/secret) carries no inline `key=`
                // prefix for the content scrubber to latch onto, so scrubbing the
                // value alone would leak the bare secret into the audit journal.
                arguments[kvp.Key] = Redaction.ScrubValue(kvp.Key, raw);
            }
        }

        string status = "unknown";
        string summary = string.Empty;
        bool mutated = false;
        IReadOnlyList<OperationBackendStep>? backendSteps = null;
        OperationEvidence? evidence = null;

        if (toolResult is OperationResponse response)
        {
            status = response.Status;
            summary = Redaction.Scrub(response.Summary);
            evidence = response.Evidence;
            if (response.BackendSteps is { } steps)
            {
                List<OperationBackendStep> scrubbedSteps = new(steps.Count);
                foreach (OperationBackendStep step in steps)
                {
                    scrubbedSteps.Add(step with
                    {
                        Detail = Redaction.Scrub(step.Detail),
                        PayloadPreview = Redaction.Scrub(step.PayloadPreview)
                    });
                    if (step.MutatesState)
                    {
                        mutated = true;
                    }
                }
                backendSteps = scrubbedSteps;
            }
        }
        else if (toolResult is not null)
        {
            try
            {
                string json = toolResult is string s ? s : JsonSerializer.Serialize(toolResult);
                using JsonDocument document = JsonDocument.Parse(json);
                JsonElement root = document.RootElement;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    if (TryGetProperty(root, "Status", out JsonElement statusElement) && statusElement.ValueKind == JsonValueKind.String)
                    {
                        status = statusElement.GetString() ?? status;
                    }

                    if (TryGetProperty(root, "Summary", out JsonElement summaryElement) && summaryElement.ValueKind == JsonValueKind.String)
                    {
                        summary = Redaction.Scrub(summaryElement.GetString() ?? string.Empty);
                    }

                    if (TryGetProperty(root, "BackendSteps", out JsonElement stepsElement) && stepsElement.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement step in stepsElement.EnumerateArray())
                        {
                            if (step.ValueKind == JsonValueKind.Object
                                && TryGetProperty(step, "MutatesState", out JsonElement mutatesElement)
                                && mutatesElement.ValueKind == JsonValueKind.True)
                            {
                                mutated = true;
                                break;
                            }
                        }
                    }
                }
            }
            catch (JsonException)
            {
                // leave defaults
            }
        }

        AuditRecord record = new(
            Timestamp: DateTimeOffset.UtcNow,
            SessionId: context.SessionId,
            OperationId: operationId,
            ToolName: call.ToolName,
            Arguments: arguments,
            Status: status,
            Summary: summary,
            Mutated: mutated,
            ExecutionMode: context.ExecutionMode,
            ExecutionTier: context.ExecutionTier,
            ApprovalMode: context.ApprovalMode,
            Provider: context.Provider,
            BackendSteps: backendSteps,
            Evidence: evidence);

        try
        {
            await context.Sink.WriteAsync(record, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"warn: audit sink write failed: {exception.Message}");
        }
    }

    private static bool TryGetProperty(JsonElement element, string pascalCaseName, out JsonElement value)
    {
        if (element.TryGetProperty(pascalCaseName, out value))
        {
            return true;
        }

        string camelCaseName = char.ToLowerInvariant(pascalCaseName[0]) + pascalCaseName[1..];
        return element.TryGetProperty(camelCaseName, out value);
    }
}
