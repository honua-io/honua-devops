using Honua.DevOps.Agent.Operations.BugReport;

namespace Honua.DevOps.Agent.Tests;

public class BugReportReporterTests
{
    private static readonly IReadOnlyList<string> Labels = ["bug", "honua-support"];

    private static BugReport SampleReport()
        => new(
            EventId: "evt-1",
            EmittedAt: DateTimeOffset.Parse("2026-06-01T12:00:00Z"),
            TicketId: "ST-2026-0001",
            Component: "sdk-js",
            Severity: "high",
            Environment: "prod",
            Service: "tiles-api",
            Title: "Tiles fail to render",
            Summary: "See fixture references.",
            Fingerprint: "fp-abc123",
            EnvelopeRefs: ["env-ref-1"],
            FixtureRefs: ["fx-ref-9"],
            TicketUrl: "https://support.example.test/tickets/ST-2026-0001");

    private static readonly RepoRef Repo = new("honua-io", "honua-sdk-js");

    [Fact]
    public async Task DisabledTracker_DoesNotFile_StaysReportOnly()
    {
        FakeIssueTracker tracker = new(enabled: false);
        StringWriter stdout = new();
        BugReportReporter reporter = new(tracker, Labels, stdout, new StringWriter());

        await reporter.ReportAsync(SampleReport(), Repo, CancellationToken.None);

        Assert.Equal(0, tracker.FileCount);
        Assert.Equal(0, tracker.SearchCount);
        Assert.Contains("issue filing disabled", stdout.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoDuplicate_FilesSanitizedIssue()
    {
        FakeIssueTracker tracker = new(enabled: true, duplicateFound: false);
        StringWriter stdout = new();
        BugReportReporter reporter = new(tracker, Labels, stdout, new StringWriter());

        await reporter.ReportAsync(SampleReport(), Repo, CancellationToken.None);

        Assert.Equal(1, tracker.SearchCount);
        Assert.Equal(1, tracker.FileCount);
        Assert.NotNull(tracker.LastFiledIssue);
        // Filed body is reference-only and carries the dedupe marker.
        Assert.Contains("honua-bug-fingerprint: fp-abc123", tracker.LastFiledIssue!.Body, StringComparison.Ordinal);
        Assert.Contains("filed sanitized issue", stdout.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DuplicateFound_DoesNotFile()
    {
        FakeIssueTracker tracker = new(enabled: true, duplicateFound: true, existingUrl: "https://github.com/honua-io/honua-sdk-js/issues/7");
        StringWriter stdout = new();
        BugReportReporter reporter = new(tracker, Labels, stdout, new StringWriter());

        await reporter.ReportAsync(SampleReport(), Repo, CancellationToken.None);

        Assert.Equal(1, tracker.SearchCount);
        Assert.Equal(0, tracker.FileCount);
        Assert.Contains("not filing a duplicate", stdout.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SearchFailure_FailsClosed_DoesNotFile()
    {
        FakeIssueTracker tracker = new(enabled: true, searchSucceeds: false);
        StringWriter stderr = new();
        BugReportReporter reporter = new(tracker, Labels, new StringWriter(), stderr);

        await reporter.ReportAsync(SampleReport(), Repo, CancellationToken.None);

        Assert.Equal(1, tracker.SearchCount);
        Assert.Equal(0, tracker.FileCount);
        Assert.Contains("skipping filing", stderr.ToString(), StringComparison.Ordinal);
    }

    private sealed class FakeIssueTracker : IIssueTracker
    {
        private readonly bool _enabled;
        private readonly bool _duplicateFound;
        private readonly bool _searchSucceeds;
        private readonly string? _existingUrl;

        internal FakeIssueTracker(
            bool enabled,
            bool duplicateFound = false,
            bool searchSucceeds = true,
            string? existingUrl = null)
        {
            _enabled = enabled;
            _duplicateFound = duplicateFound;
            _searchSucceeds = searchSucceeds;
            _existingUrl = existingUrl;
        }

        public bool IsEnabled => _enabled;

        internal int SearchCount { get; private set; }
        internal int FileCount { get; private set; }
        internal GeneratedIssue? LastFiledIssue { get; private set; }

        public Task<IssueSearchResult> FindOpenIssueAsync(RepoRef repo, string dedupeKey, CancellationToken cancellationToken = default)
        {
            SearchCount++;
            return Task.FromResult(new IssueSearchResult(
                IsSuccess: _searchSucceeds,
                DuplicateFound: _duplicateFound,
                ExistingIssueUrl: _existingUrl,
                Detail: _searchSucceeds ? "ok" : "search-failed: 500"));
        }

        public Task<IssueFilingResult> FileIssueAsync(RepoRef repo, GeneratedIssue issue, CancellationToken cancellationToken = default)
        {
            FileCount++;
            LastFiledIssue = issue;
            return Task.FromResult(new IssueFilingResult(IsSuccess: true, IssueUrl: "https://github.com/honua-io/honua-sdk-js/issues/42", Detail: "201 Created"));
        }
    }
}
