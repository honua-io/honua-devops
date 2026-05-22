using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.DevOps.Agent.Operations;

/// <summary>
/// Wire shape of the escalation webhook body sent by honua-support's
/// <c>WebhookEscalationNotifier</c>. JSON is camelCase on the wire.
///
/// We deliberately do not strongly-type the embedded <c>diagnosis</c> object —
/// it is honua-support's <c>TicketDiagnosis</c> shape, which evolves
/// independently of honua-devops. We keep it as raw JSON for display while
/// pulling the fields we need for triage out separately.
/// </summary>
internal sealed record EscalationWebhookPayload(
    [property: JsonPropertyName("event")] string Event,
    [property: JsonPropertyName("ticketId")] string? TicketId,
    [property: JsonPropertyName("customerId")] string? CustomerId,
    [property: JsonPropertyName("severity")] string? Severity,
    [property: JsonPropertyName("environment")] string? Environment,
    [property: JsonPropertyName("service")] string? Service,
    [property: JsonPropertyName("symptoms")] string? Symptoms,
    [property: JsonPropertyName("diagnosis")] JsonElement Diagnosis,
    [property: JsonPropertyName("escalatedAt")] DateTimeOffset? EscalatedAt)
{
    internal const string ExpectedEvent = "ticket.escalation_requested";
    internal const string EventHeader = "X-Honua-Event";
    internal const string SignatureHeader = "X-Honua-Signature";

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    internal string? DiagnosisSummary()
    {
        if (Diagnosis.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (Diagnosis.TryGetProperty("summary", out JsonElement summary)
            && summary.ValueKind == JsonValueKind.String)
        {
            return summary.GetString();
        }

        return null;
    }

    internal string? DiagnosisMode()
    {
        if (Diagnosis.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (Diagnosis.TryGetProperty("mode", out JsonElement mode)
            && mode.ValueKind == JsonValueKind.String)
        {
            return mode.GetString();
        }

        return null;
    }
}
