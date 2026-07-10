using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.DevOps.Agent.Operations.BugReport;

/// <summary>
/// Wire shape of the <c>ticket.bug_report.v1</c> event emitted (signed) by
/// honua-support once an operator has previewed and approved routing a confirmed
/// bug into a product repo (honua-support#44). JSON is camelCase on the wire,
/// matching honua-support's <c>JsonSerializerDefaults.Web</c> and the sibling
/// <see cref="EscalationWebhookPayload"/> contract.
///
/// This is a <em>sanitized</em>, reference-only contract by construction: it
/// carries a ticket id, a stable fingerprint, and lists of opaque
/// <em>reference</em> ids (sanitized envelope refs, fixture refs) — never raw
/// customer payloads or bytes. honua-devops additionally scrubs every free-text
/// field before it reaches a generated issue (see
/// <see cref="BugReportIssueComposer"/>), so a mis-emitted secret cannot leak
/// into a product repo.
///
/// The <c>component</c> is the support/customer-side classification. It is used
/// ONLY as a lookup key into the server-owned
/// <see cref="ComponentRepoAllowlist"/>; it never selects a destination repo
/// directly.
/// </summary>
internal sealed record BugReportWebhookPayload(
    [property: JsonPropertyName("eventId")] string? EventId,
    [property: JsonPropertyName("eventType")] string? EventType,
    [property: JsonPropertyName("emittedAt")] DateTimeOffset? EmittedAt,
    [property: JsonPropertyName("ticketId")] string? TicketId,
    [property: JsonPropertyName("component")] string? Component,
    [property: JsonPropertyName("severity")] string? Severity,
    [property: JsonPropertyName("environment")] string? Environment,
    [property: JsonPropertyName("service")] string? Service,
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("summary")] string? Summary,
    [property: JsonPropertyName("fingerprint")] string? Fingerprint,
    [property: JsonPropertyName("envelopeRefs")] IReadOnlyList<string>? EnvelopeRefs,
    [property: JsonPropertyName("fixtureRefs")] IReadOnlyList<string>? FixtureRefs,
    [property: JsonPropertyName("ticketUrl")] string? TicketUrl)
{
    /// <summary>The only event type this adapter accepts. Versioned on purpose.</summary>
    internal const string ExpectedEvent = "ticket.bug_report.v1";

    internal const string EventHeader = "X-Honua-Event";
    internal const string SignatureHeader = "X-Honua-Signature";

    // Match honua-support's JsonSerializerDefaults.Web (camelCase + case-insensitive
    // read). The HMAC is verified over the raw bytes, which we never touch.
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
}
