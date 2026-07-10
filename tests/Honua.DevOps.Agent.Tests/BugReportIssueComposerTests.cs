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

        // The dedupe marker carries a SHA-256 hash of the dedupe key (not the raw
        // fingerprint), and that same hash is the repo search token.
        string hash = BugReportIssueComposer.ComputeDedupeHash(SampleReport());
        Assert.Contains($"honua-bug-fingerprint: {hash}", issue.Body, StringComparison.Ordinal);
        Assert.Contains(issue.DedupeMarker, issue.Body, StringComparison.Ordinal);
        Assert.Equal(hash, issue.DedupeSearchToken);
        // The raw fingerprint is never embedded inside the marker comment.
        Assert.DoesNotContain("fp-abc123", issue.DedupeMarker, StringComparison.Ordinal);
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
        // The redaction placeholder survives (HTML-escaped by the sanitizer so it
        // renders as literal text rather than a stray tag).
        Assert.Contains("redacted", issue.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void Compose_DedupeMarker_FallsBackToTicketWhenNoFingerprint()
    {
        BugReport report = SampleReport(fingerprint: null);

        string marker = BugReportIssueComposer.BuildDedupeMarker(report);
        string hash = BugReportIssueComposer.ComputeDedupeHash("ST-2026-0001");

        Assert.Equal("ST-2026-0001", report.DedupeKey);
        Assert.Contains(hash, marker, StringComparison.Ordinal);
        // The raw ticket id is not embedded in the marker comment.
        Assert.DoesNotContain("ST-2026-0001", marker, StringComparison.Ordinal);
    }

    [Fact]
    public void Compose_NeutralizesMentionAndBacklinkInjection_InTitleAndBody()
    {
        // A crafted summary/title must not ping a team or backlink another issue in
        // the destination product repo (FIX 1).
        BugReport report = SampleReport(
            title: "cc @honua-io/team see owner/repo#1 <b>boom</b>",
            summary: "Fixed — cc @honua-io/security see honua-io/honua-server-private#1 | <img src=x>");

        GeneratedIssue issue = BugReportIssueComposer.Compose(report, new RepoRef("honua-io", "honua-sdk-js"), Labels);

        // No live @mention (a zero-width space is inserted after '@').
        Assert.DoesNotContain("@honua-io/team", issue.Title, StringComparison.Ordinal);
        Assert.DoesNotContain("@honua-io/security", issue.Body, StringComparison.Ordinal);
        Assert.Contains("@\u200B", issue.Body, StringComparison.Ordinal);
        // No live issue back-reference (#<digit> is broken with a zero-width space).
        Assert.DoesNotContain("repo#1", issue.Title, StringComparison.Ordinal);
        Assert.DoesNotContain("private#1", issue.Body, StringComparison.Ordinal);
        Assert.Contains("#\u200B", issue.Body, StringComparison.Ordinal);
        // Raw HTML is escaped, not emitted.
        Assert.DoesNotContain("<b>", issue.Title, StringComparison.Ordinal);
        Assert.DoesNotContain("<img", issue.Body, StringComparison.Ordinal);
        Assert.Contains("&lt;img", issue.Body, StringComparison.Ordinal);
        // The pipe in the summary is entity-escaped so it cannot break a table.
        Assert.Contains("&#124;", issue.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void Compose_Title_IsSingleLine_AndLengthCapped()
    {
        string longTitle = "line one\nline two " + new string('x', 300);
        BugReport report = SampleReport(title: longTitle);

        GeneratedIssue issue = BugReportIssueComposer.Compose(report, new RepoRef("honua-io", "honua-sdk-js"), Labels);

        Assert.DoesNotContain("\n", issue.Title, StringComparison.Ordinal);
        // "[support-bug] " prefix + capped subject (MaxTitleLength + ellipsis).
        Assert.True(issue.Title.Length <= "[support-bug] ".Length + BugReportSanitizer.MaxTitleLength + 1);
    }

    [Fact]
    public void Compose_DedupeMarker_CannotBeBrokenByCommentTerminatorInFingerprint()
    {
        // A fingerprint containing '-->' must not break out of the marker comment
        // and must still be matchable via a stable hash on redelivery (FIX 2/6).
        BugReport report = SampleReport(fingerprint: "evil--><script>alert(1)</script>");

        GeneratedIssue issue = BugReportIssueComposer.Compose(report, new RepoRef("honua-io", "honua-sdk-js"), Labels);

        string hash = BugReportIssueComposer.ComputeDedupeHash(report);
        Assert.Equal($"<!-- honua-bug-fingerprint: {hash} -->", issue.DedupeMarker);
        Assert.DoesNotContain("-->", hash, StringComparison.Ordinal);
        Assert.DoesNotContain("<script>", issue.DedupeMarker, StringComparison.Ordinal);
        // Stable hash: the same key hashes identically across redeliveries.
        Assert.Equal(hash, BugReportIssueComposer.ComputeDedupeHash(report));
        Assert.Equal(hash, issue.DedupeSearchToken);
    }

    [Fact]
    public void Compose_CodeSpanValues_StripBacktickBreakout()
    {
        // A backtick in a code-span value must be stripped so it cannot close the
        // span and escape into live Markdown.
        BugReport report = SampleReport(
            envelopeRefs: new[] { "ref`</code>@honua-io/team" },
            fixtureRefs: Array.Empty<string>());

        GeneratedIssue issue = BugReportIssueComposer.Compose(report, new RepoRef("honua-io", "honua-sdk-js"), Labels);

        Assert.DoesNotContain("ref`", issue.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void Compose_NoRefs_RendersNonePlaceholder()
    {
        BugReport report = SampleReport(envelopeRefs: Array.Empty<string>(), fixtureRefs: Array.Empty<string>());

        GeneratedIssue issue = BugReportIssueComposer.Compose(report, new RepoRef("honua-io", "honua-sdk-js"), Labels);

        Assert.Contains("(none provided)", issue.Body, StringComparison.Ordinal);
    }
}
