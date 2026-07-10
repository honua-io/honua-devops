using Honua.DevOps.Agent.Operations.BugReport;

namespace Honua.DevOps.Agent.Tests;

public class BugReportIssueComposerTests
{
    private static BugReport SampleReport(
        string? title = "Tiles fail to render",
        string? summary = "See attached fixture references.",
        string? fingerprint = "fp-abc123",
        IReadOnlyList<string>? envelopeRefs = null,
        IReadOnlyList<string>? fixtureRefs = null)
        => new(
            EventId: "evt-1",
            EmittedAt: DateTimeOffset.Parse("2026-06-01T12:00:00Z"),
            TicketId: "ST-2026-0001",
            Component: "sdk-js",
            Severity: "high",
            Environment: "prod",
            Service: "tiles-api",
            Title: title,
            Summary: summary,
            Fingerprint: fingerprint,
            EnvelopeRefs: envelopeRefs ?? new[] { "env-ref-1", "env-ref-2" },
            FixtureRefs: fixtureRefs ?? new[] { "fx-ref-9" },
            TicketUrl: "https://support.example.test/tickets/ST-2026-0001");

    private static readonly IReadOnlyList<string> Labels = ["bug", "honua-support"];

    [Fact]
    public void Compose_RendersReferencesOnly_AndEmbedsMarker()
    {
        GeneratedIssue issue = BugReportIssueComposer.Compose(SampleReport(), new RepoRef("honua-io", "honua-sdk-js"), Labels);

        Assert.StartsWith("[support-bug]", issue.Title, StringComparison.Ordinal);
        Assert.Equal(Labels, issue.Labels);

        Assert.Contains("ST-2026-0001", issue.Body, StringComparison.Ordinal);
        Assert.Contains("honua-io/honua-sdk-js", issue.Body, StringComparison.Ordinal);
        Assert.Contains("env-ref-1", issue.Body, StringComparison.Ordinal);
        Assert.Contains("fx-ref-9", issue.Body, StringComparison.Ordinal);
        Assert.Contains("evidence references only", issue.Body, StringComparison.OrdinalIgnoreCase);

        // The dedupe marker carries the fingerprint and is present in the body.
        Assert.Contains("honua-bug-fingerprint: fp-abc123", issue.Body, StringComparison.Ordinal);
        Assert.Contains(issue.DedupeMarker, issue.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void Compose_ScrubsSecretsFromFreeText()
    {
        // A secret smuggled into a free-text field must never survive into a product
        // repo. Title and summary are scrubbed exactly like the GitOps PR body.
        BugReport report = SampleReport(
            title: "crash when token=supersecrettoken is set",
            summary: "config had api_key=leakedvalue in it");

        GeneratedIssue issue = BugReportIssueComposer.Compose(report, new RepoRef("honua-io", "honua-sdk-js"), Labels);

        Assert.DoesNotContain("supersecrettoken", issue.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("supersecrettoken", issue.Title, StringComparison.Ordinal);
        Assert.DoesNotContain("leakedvalue", issue.Body, StringComparison.Ordinal);
        Assert.Contains("<redacted>", issue.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void Compose_DedupeMarker_FallsBackToTicketWhenNoFingerprint()
    {
        BugReport report = SampleReport(fingerprint: null);

        string marker = BugReportIssueComposer.BuildDedupeMarker(report);

        Assert.Contains("ST-2026-0001", marker, StringComparison.Ordinal);
        Assert.Equal("ST-2026-0001", report.DedupeKey);
    }

    [Fact]
    public void Compose_NoRefs_RendersNonePlaceholder()
    {
        BugReport report = SampleReport(envelopeRefs: Array.Empty<string>(), fixtureRefs: Array.Empty<string>());

        GeneratedIssue issue = BugReportIssueComposer.Compose(report, new RepoRef("honua-io", "honua-sdk-js"), Labels);

        Assert.Contains("(none provided)", issue.Body, StringComparison.Ordinal);
    }
}
