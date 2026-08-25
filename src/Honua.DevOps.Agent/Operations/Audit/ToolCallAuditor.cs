using System.Text.Json;
using Honua.DevOps.Agent.Operations.Observability;

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

        // This identifies one audit emission only. It is deliberately not called operationId:
        // canonical runtime operations come from their owning control plane, while provisioning
        // carries its separate stable provisioningOperationId across plan/apply/handoff.
        string auditEventId = Guid.NewGuid().ToString("n");
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
        ProvisioningLineage? provisioningLineage = null;

        if (toolResult is OperationResponse response)
        {
            status = response.Status;
            summary = Redaction.Scrub(response.Summary);
            evidence = response.Evidence;
            provisioningLineage = response.ProvisioningLineage;
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
        else if (toolResult is OpsLoopReport opsLoop)
        {
            status = opsLoop.Status;
            summary = Redaction.Scrub(
                $"Honua MCP ops loop: health={opsLoop.OverallHealth ?? "unknown"}, findings={opsLoop.Findings.Count}, proposals={opsLoop.Findings.Count(finding => finding.Proposal is not null)}.");
            mutated = opsLoop.Findings.Any(finding =>
                finding.Proposal?.GatewayStatus is
                    "ProposalCreated" or
                    "Executed" or
                    "Failed" or
                    "RolledBack" or
                    "Indeterminate" or
                    "Canceled");
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

        if (string.Equals(call.ToolName, "provision_infrastructure", StringComparison.Ordinal)
            && status is "infrastructure-provisioned" or "infrastructure-destroyed"
            && !string.IsNullOrWhiteSpace(provisioningLineage?.ApplyAuditEventId))
        {
            auditEventId = provisioningLineage.ApplyAuditEventId;
        }

        AuditRecord record = new(
            Timestamp: DateTimeOffset.UtcNow,
            SessionId: context.SessionId,
            AuditEventId: auditEventId,
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
            Evidence: evidence,
            ProvisioningLineage: provisioningLineage);

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
