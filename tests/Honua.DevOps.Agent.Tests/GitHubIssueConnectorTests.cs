using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using Honua.DevOps.Agent.Operations.BugReport;

namespace Honua.DevOps.Agent.Tests;

public class GitHubIssueConnectorTests
{
    private static BugReportConfiguration Configuration(
        string? token = "ghp_test",
        IReadOnlyList<string>? allowedHosts = null,
        Uri? baseUri = null)
        => new(
            WebhookSecret: "secret",
            Port: BugReportConfiguration.DefaultPort,
            Path: BugReportConfiguration.DefaultPath,
            ReplayWindow: TimeSpan.FromSeconds(300),
            Allowlist: ComponentRepoAllowlist.Parse("sdk-js=honua-io/honua-sdk-js"),
            Labels: BugReportConfiguration.DefaultLabels,
            GitHubApiBaseUri: baseUri ?? new Uri("https://api.github.com"),
            GitHubToken: token,
            AllowedHosts: allowedHosts ?? ["api.github.com"]);

    private static readonly RepoRef Repo = new("honua-io", "honua-sdk-js");

    private static GeneratedIssue SampleIssue()
        => new(
            "[support-bug] Tiles fail",
            "body with references only",
            ["bug"],
            "<!-- honua-bug-fingerprint: deadbeef -->",
            "deadbeef");

    [Fact]
    public async Task FindOpenIssue_ReportsDuplicate_WhenSearchReturnsMatch()
    {
        TestHttpMessageHandler handler = new(_ => TestHttpMessageHandler.JsonOk(new
        {
            total_count = 1,
            items = new[] { new { html_url = "https://github.com/honua-io/honua-sdk-js/issues/7" } }
        }));
        using HttpClient httpClient = new(handler) { Timeout = TimeSpan.FromSeconds(5) };
        using GitHubIssueConnector connector = new(Configuration(), httpClient);

        IssueSearchResult result = await connector.FindOpenIssueAsync(Repo, "abc123hash");

        Assert.True(result.IsSuccess);
        Assert.True(result.DuplicateFound);
        Assert.Equal("https://github.com/honua-io/honua-sdk-js/issues/7", result.ExistingIssueUrl);

        CapturedRequest captured = Assert.Single(handler.CapturedRequests);
        Assert.Equal("GET", captured.Method);
        Assert.Contains("/search/issues", captured.Uri, StringComparison.Ordinal);
        Assert.Contains("repo%3Ahonua-io%2Fhonua-sdk-js", captured.Uri, StringComparison.Ordinal);
        // The dedupe hash is used as the exact search term (FIX 6).
        Assert.Contains("abc123hash", captured.Uri, StringComparison.Ordinal);
        Assert.Equal("Bearer", captured.AuthorizationScheme);
        Assert.Equal("ghp_test", captured.AuthorizationParameter);
    }

    [Fact]
    public async Task FindOpenIssue_NoMatch_ReportsNoDuplicate()
    {
        TestHttpMessageHandler handler = new(_ => TestHttpMessageHandler.JsonOk(new { total_count = 0, items = Array.Empty<object>() }));
        using HttpClient httpClient = new(handler) { Timeout = TimeSpan.FromSeconds(5) };
        using GitHubIssueConnector connector = new(Configuration(), httpClient);

        IssueSearchResult result = await connector.FindOpenIssueAsync(Repo, "fp-1");

        Assert.True(result.IsSuccess);
        Assert.False(result.DuplicateFound);
    }

    [Fact]
    public async Task FileIssue_PostsToRepoIssuesEndpoint()
    {
        TestHttpMessageHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = JsonContent.Create(new { html_url = "https://github.com/honua-io/honua-sdk-js/issues/42" })
        });
        using HttpClient httpClient = new(handler) { Timeout = TimeSpan.FromSeconds(5) };
        using GitHubIssueConnector connector = new(Configuration(), httpClient);

        IssueFilingResult result = await connector.FileIssueAsync(Repo, SampleIssue());

        Assert.True(result.IsSuccess);
        Assert.Equal("https://github.com/honua-io/honua-sdk-js/issues/42", result.IssueUrl);

        CapturedRequest captured = Assert.Single(handler.CapturedRequests);
        Assert.Equal("POST", captured.Method);
        Assert.Contains("/repos/honua-io/honua-sdk-js/issues", captured.Uri, StringComparison.Ordinal);
        Assert.Equal("Bearer", captured.AuthorizationScheme);
        Assert.NotNull(captured.Body);
        Assert.Contains("references only", captured.Body!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Disabled_WhenTokenMissing_MakesNoCall()
    {
        TestHttpMessageHandler handler = new(_ => TestHttpMessageHandler.JsonOk(new { }));
        using HttpClient httpClient = new(handler) { Timeout = TimeSpan.FromSeconds(5) };
        using GitHubIssueConnector connector = new(Configuration(token: null), httpClient);

        Assert.False(connector.IsEnabled);
        IssueFilingResult result = await connector.FileIssueAsync(Repo, SampleIssue());

        Assert.False(result.IsSuccess);
        Assert.Contains("github-disabled", result.Detail, StringComparison.Ordinal);
        Assert.Empty(handler.CapturedRequests);
    }

    [Fact]
    public async Task HostNotAllowed_RejectsBeforeCall()
    {
        TestHttpMessageHandler handler = new(_ => TestHttpMessageHandler.JsonOk(new { }));
        using HttpClient httpClient = new(handler) { Timeout = TimeSpan.FromSeconds(5) };
        using GitHubIssueConnector connector = new(Configuration(allowedHosts: ["ghe.internal.example"]), httpClient);

        IssueFilingResult result = await connector.FileIssueAsync(Repo, SampleIssue());

        Assert.False(result.IsSuccess);
        Assert.Contains("github-host-rejected", result.Detail, StringComparison.Ordinal);
        Assert.Empty(handler.CapturedRequests);
    }
}
