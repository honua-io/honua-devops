using System.Text.Json;
using Honua.DevOps.Agent.Operations.Observability;

namespace Honua.DevOps.Agent.Operations.Audit;

internal sealed record AuditContext(
    string SessionId,
    string ExecutionMode,
    string ExecutionTier,
    string ApprovalMode,
    string Provider,
    IAuditSink Sink,
    AuditRecoveryStore? RecoveryStore = null);

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
        OperationResponse? operationResponse = toolResult as OperationResponse;

        if (operationResponse is not null)
        {
            status = operationResponse.Status;
            summary = Redaction.Scrub(operationResponse.Summary);
            evidence = operationResponse.Evidence;
            provisioningLineage = operationResponse.ProvisioningLineage;
            if (operationResponse.BackendSteps is { } steps)
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
                $"Honua MCP ops loop: health={opsLoop.OverallHealth ?? "unknown"}, evidence={opsLoop.EvidencePosture.Status}, findings={opsLoop.Findings.Count}, proposals={opsLoop.Findings.Count(finding => finding.Proposal is not null)}.");
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

        string? operationId = operationResponse?.Actuation?.OperationId
            ?? provisioningLineage?.ProvisioningOperationId;
        string? idempotencyKey = operationResponse?.Actuation?.IdempotencyKey;
        if (idempotencyKey is null && provisioningLineage is not null)
        {
            idempotencyKey = call.ToolName switch
            {
                "install_handoff" => $"honua-devops:install-handoff:{provisioningLineage.ProvisioningOperationId}",
                "verify_install_handoff" => $"honua-devops:verify-install-handoff:{provisioningLineage.ProvisioningOperationId}",
                _ => string.Join(
                    ":",
                    "honua-devops",
                    "terraform",
                    provisioningLineage.ProvisioningOperationId,
                    status == "infrastructure-destroyed" ? "destroy" : "apply")
            };
        }
        string route = operationResponse?.Actuation?.Action
            ?? (provisioningLineage is not null
                ? call.ToolName switch
                {
                    "install_handoff" => "install-handoff",
                    "verify_install_handoff" => "verify-install-handoff",
                    _ => $"terraform-exact:{call.ToolName}"
                }
                : call.ToolName);

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

        // Audit acknowledgement is part of the tool result commit.  Never turn
        // append/flush failure into a warning: after mutation that would expose
        // an unaudited success to the caller.  The host returns an error and the
        // stable operation/idempotency lineage is used for reconciliation.
        try
        {
            await context.Sink.WriteAsync(record, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            if (mutated)
            {
                AuditRecoveryStore recoveryStore = context.RecoveryStore ?? AuditRecoveryStore.Default;
                recoveryStore.Record(new AuditRecoveryEvidence(
                    RecoveryState: "indeterminate/reconciliation-required",
                    RecordedAtUtc: DateTimeOffset.UtcNow,
                    AuditEventId: auditEventId,
                    ToolName: call.ToolName,
                    Route: route,
                    OperationId: operationId,
                    IdempotencyKey: idempotencyKey,
                    ProvisioningOperationId: provisioningLineage?.ProvisioningOperationId,
                    ApprovalReference: operationResponse?.Actuation?.Receipt?.ReceiptId
                        ?? provisioningLineage?.ApprovalReceiptId,
                    SinkFailure: $"{exception.GetType().Name}: {Redaction.Scrub(exception.Message)}",
                    ReturnedStatus: status,
                    MutationAttempted: true,
                    BackendAcknowledged: backendSteps?.Any(step => step.MutatesState && step.Success) == true,
                    BackendSteps: backendSteps is null
                        ? []
                        : backendSteps.Select(step => new AuditRecoveryStep(
                            step.Name,
                            Redaction.Scrub(step.Endpoint),
                            step.Success,
                            Redaction.Scrub(step.Detail),
                            step.MutatesState)).ToArray()));
            }

            throw;
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
