namespace Honua.DevOps.Agent.Operations.BugReport;

/// <summary>
/// Validated, normalized projection of a <see cref="BugReportWebhookPayload"/>.
/// Required fields are guaranteed non-empty and trimmed; reference lists are
/// non-null. The rest of honua-devops depends on this shape rather than the raw
/// wire payload.
/// </summary>
internal sealed record BugReport(
    string EventId,
    DateTimeOffset EmittedAt,
    string TicketId,
    string Component,
    string? Severity,
    string? Environment,
    string? Service,
    string? Title,
    string? Summary,
    string? Fingerprint,
    IReadOnlyList<string> EnvelopeRefs,
    IReadOnlyList<string> FixtureRefs,
    string? TicketUrl)
{
    /// <summary>
    /// Stable key for duplicate-issue detection: prefer the emitter-supplied
    /// fingerprint, fall back to the ticket id. Two emissions describing the same
    /// underlying bug collapse onto the same key so we never file twice.
    /// </summary>
    internal string DedupeKey => string.IsNullOrWhiteSpace(Fingerprint) ? TicketId : Fingerprint!;
}
