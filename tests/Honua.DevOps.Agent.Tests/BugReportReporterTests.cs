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

        BugReportFilingOutcome outcome = await reporter.ReportAsync(SampleReport(), Repo, CancellationToken.None);

        Assert.Equal(BugReportFilingOutcome.ReportOnly, outcome);
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

        BugReportFilingOutcome outcome = await reporter.ReportAsync(SampleReport(), Repo, CancellationToken.None);

        Assert.Equal(BugReportFilingOutcome.Filed, outcome);
        Assert.Equal(1, tracker.SearchCount);
        Assert.Equal(1, tracker.FileCount);
        Assert.NotNull(tracker.LastFiledIssue);
        // Filed body carries the hashed dedupe marker, and the SAME hash is used as
        // the repo search term so the just-filed issue is always findable (FIX 2/6).
        string expectedHash = BugReportIssueComposer.ComputeDedupeHash(SampleReport());
        Assert.Contains($"honua-bug-fingerprint: {expectedHash}", tracker.LastFiledIssue!.Body, StringComparison.Ordinal);
        Assert.Equal(expectedHash, tracker.LastSearchToken);
        // The raw fingerprint never appears inside the dedupe marker comment.
        Assert.DoesNotContain($"honua-bug-fingerprint: fp-abc123", tracker.LastFiledIssue.Body, StringComparison.Ordinal);
        Assert.Contains("filed sanitized issue", stdout.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DuplicateFound_DoesNotFile()
    {
        FakeIssueTracker tracker = new(enabled: true, duplicateFound: true, existingUrl: "https://github.com/honua-io/honua-sdk-js/issues/7");
        StringWriter stdout = new();
        BugReportReporter reporter = new(tracker, Labels, stdout, new StringWriter());

        BugReportFilingOutcome outcome = await reporter.ReportAsync(SampleReport(), Repo, CancellationToken.None);

        Assert.Equal(BugReportFilingOutcome.DuplicateSkipped, outcome);
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

        BugReportFilingOutcome outcome = await reporter.ReportAsync(SampleReport(), Repo, CancellationToken.None);

        Assert.Equal(BugReportFilingOutcome.SearchFailed, outcome);
        Assert.Equal(1, tracker.SearchCount);
        Assert.Equal(0, tracker.FileCount);
        Assert.Contains("will retry", stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task FilingFailure_ReturnsFilingFailed()
    {
        FakeIssueTracker tracker = new(enabled: true, duplicateFound: false, fileSucceeds: false);
        StringWriter stderr = new();
        BugReportReporter reporter = new(tracker, Labels, new StringWriter(), stderr);

        BugReportFilingOutcome outcome = await reporter.ReportAsync(SampleReport(), Repo, CancellationToken.None);

        Assert.Equal(BugReportFilingOutcome.FilingFailed, outcome);
        Assert.Equal(1, tracker.SearchCount);
        Assert.Equal(1, tracker.FileCount);
        Assert.Contains("issue filing failed", stderr.ToString(), StringComparison.Ordinal);
    }

    private sealed class FakeIssueTracker : IIssueTracker
    {
        private readonly bool _enabled;
        private readonly bool _duplicateFound;
        private readonly bool _searchSucceeds;
        private readonly bool _fileSucceeds;
        private readonly string? _existingUrl;

        internal FakeIssueTracker(
            bool enabled,
            bool duplicateFound = false,
            bool searchSucceeds = true,
            bool fileSucceeds = true,
            string? existingUrl = null)
        {
            _enabled = enabled;
            _duplicateFound = duplicateFound;
            _searchSucceeds = searchSucceeds;
            _fileSucceeds = fileSucceeds;
            _existingUrl = existingUrl;
        }

        public bool IsEnabled => _enabled;

        internal int SearchCount { get; private set; }
        internal int FileCount { get; private set; }
        internal GeneratedIssue? LastFiledIssue { get; private set; }
        internal string? LastSearchToken { get; private set; }

        public Task<IssueSearchResult> FindOpenIssueAsync(RepoRef repo, string dedupeSearchToken, CancellationToken cancellationToken = default)
        {
            SearchCount++;
            LastSearchToken = dedupeSearchToken;
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
            return Task.FromResult(_fileSucceeds
                ? new IssueFilingResult(IsSuccess: true, IssueUrl: "https://github.com/honua-io/honua-sdk-js/issues/42", Detail: "201 Created")
                : new IssueFilingResult(IsSuccess: false, IssueUrl: null, Detail: "file-failed: 502"));
        }
    }
}
