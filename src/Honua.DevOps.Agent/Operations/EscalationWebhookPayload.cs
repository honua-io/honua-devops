using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.DevOps.Agent.Operations;

/// <summary>
/// Wire shape of the escalation webhook body sent by honua-support's
/// <c>SupportNotificationService</c>. JSON is camelCase on the wire.
///
/// The fields here mirror honua-support's <c>SupportNotificationPayload</c>
/// model. We deliberately accept <c>sla</c> as raw JSON so honua-support can
/// evolve the SLA snapshot independently — we only summarise it for display.
/// </summary>
internal sealed record EscalationWebhookPayload(
    [property: JsonPropertyName("eventId")] string? EventId,
    [property: JsonPropertyName("eventType")] string EventType,
    [property: JsonPropertyName("ticketId")] string? TicketId,
    [property: JsonPropertyName("customerId")] string? CustomerId,
    [property: JsonPropertyName("severity")] string? Severity,
    [property: JsonPropertyName("environment")] string? Environment,
    [property: JsonPropertyName("service")] string? Service,
    [property: JsonPropertyName("phase")] string? Phase,
    [property: JsonPropertyName("customerStatus")] string? CustomerStatus,
    [property: JsonPropertyName("sla")] JsonElement Sla,
    [property: JsonPropertyName("escalation")] EscalationWebhookEscalation? Escalation,
    [property: JsonPropertyName("ticketUrl")] string? TicketUrl)
{
    internal const string ExpectedEvent = "ticket.escalation_requested";
    internal const string EventHeader = "X-Honua-Event";
    internal const string SignatureHeader = "X-Honua-Signature";

    // Match honua-support's JsonSerializerDefaults.Web: camelCase naming and
    // case-insensitive matching on read. PropertyNameCaseInsensitive lets us
    // accept minor casing drift on inbound bodies without re-signing failures
    // (the HMAC is computed over raw bytes, which we never touch).
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Convenience accessor for the nested <c>escalation.escalatedAt</c>
    /// timestamp; honua-support does not put this at the top level.
    /// </summary>
    internal DateTimeOffset? EscalatedAt => Escalation?.EscalatedAt;

    /// <summary>
    /// Compact one-line summary of honua-support's SLA snapshot, suitable
    /// for the operator console. Returns null when the payload omitted the
    /// snapshot or it is not an object.
    /// </summary>
    internal string? SlaSummary()
    {
        if (Sla.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        string? tier = ReadStringProperty(Sla, "supportTier");
        string? firstResponse = ReadStringProperty(Sla, "firstResponseTarget");
        string? cadence = ReadStringProperty(Sla, "updateCadence");

        List<string> parts = new(3);
        if (!string.IsNullOrWhiteSpace(tier))
        {
            parts.Add($"tier={tier}");
        }
        if (!string.IsNullOrWhiteSpace(firstResponse))
        {
            parts.Add($"first-response: {firstResponse}");
        }
        if (!string.IsNullOrWhiteSpace(cadence))
        {
            parts.Add($"cadence: {cadence}");
        }

        return parts.Count == 0 ? null : string.Join(" | ", parts);
    }

    private static string? ReadStringProperty(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out JsonElement value))
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }
}

internal sealed record EscalationWebhookEscalation(
    [property: JsonPropertyName("tier")] int? Tier,
    [property: JsonPropertyName("accessMode")] string? AccessMode,
    [property: JsonPropertyName("escalatedAt")] DateTimeOffset? EscalatedAt);
