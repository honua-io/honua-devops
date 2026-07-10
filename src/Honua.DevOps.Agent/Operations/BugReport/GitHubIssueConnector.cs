using System.Net.Http.Headers;
using System.Text.Json;

namespace Honua.DevOps.Agent.Operations.BugReport;

/// <summary>
/// GitHub issue tracker connector (REST v3, Bearer token auth). Modeled on
/// <see cref="Honua.DevOps.Agent.Operations.WorkIntake.JiraConnector"/>:
/// graceful-disabled when the API base URI or token is unset, host-allowlist
/// enforced before any outbound call, and all I/O through the shared
/// <see cref="HttpJsonTransport"/>.
///
/// honua-devops — not the support service — is the boundary that may hold a
/// GitHub token (it already actuates GitOps). When no token is configured the
/// connector is disabled and the adapter falls back to a plan/report posture
/// (the sanitized issue is prepared and logged, not filed), mirroring the
/// escalation receiver's report-only default.
/// </summary>
internal sealed class GitHubIssueConnector : IIssueTracker, IDisposable
{
    private readonly BugReportConfiguration _configuration;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly HttpJsonTransport _transport;

    internal GitHubIssueConnector(BugReportConfiguration configuration, HttpClient? httpClient = null)
    {
        _configuration = configuration;
        _httpClient = httpClient ?? HttpClientFactory.Create(TimeSpan.FromSeconds(20));
        _ownsHttpClient = httpClient is null;
        _transport = new HttpJsonTransport(_httpClient);
    }

    public bool IsEnabled =>
        _configuration.GitHubApiBaseUri is not null
        && !string.IsNullOrWhiteSpace(_configuration.GitHubToken)
        && _configuration.AllowedHosts.Count > 0;

    public async Task<IssueSearchResult> FindOpenIssueAsync(
        RepoRef repo,
        string dedupeSearchToken,
        CancellationToken cancellationToken = default)
    {
        if (!TryGuard(out string? guard))
        {
            return new IssueSearchResult(IsSuccess: false, DuplicateFound: false, ExistingIssueUrl: null, Detail: guard!);
        }

        // GitHub code-search-style issue query. The search term is the SHA-256
        // dedupe hash embedded in the filed issue's marker; quoting keeps it an
        // exact phrase match, scoping to the repo + open issues bounds the results.
        string query = $"repo:{repo.FullName} is:issue is:open \"{dedupeSearchToken}\"";
        string relativePath = $"search/issues?q={Uri.EscapeDataString(query)}&per_page=1";
        Uri endpoint = BackendGateway.BuildEndpoint(_configuration.GitHubApiBaseUri!, relativePath);
        if (!HostAllowed(endpoint, out string? hostRejection))
        {
            return new IssueSearchResult(IsSuccess: false, DuplicateFound: false, ExistingIssueUrl: null, Detail: hostRejection!);
        }

        using BackendJsonResult result = await _transport.SendJsonAsync(
            HttpMethod.Get, endpoint, payload: null, ApplyAuthentication, cancellationToken);

        if (!result.CallResult.IsSuccess || result.Payload is null)
        {
            return new IssueSearchResult(
                IsSuccess: false,
                DuplicateFound: false,
                ExistingIssueUrl: null,
                Detail: $"search-failed: {result.CallResult.Detail}");
        }

        JsonElement root = result.Payload.RootElement;
        int totalCount = root.TryGetProperty("total_count", out JsonElement countElement)
            && countElement.ValueKind == JsonValueKind.Number
                ? countElement.GetInt32()
                : 0;

        string? existingUrl = null;
        if (totalCount > 0
            && root.TryGetProperty("items", out JsonElement items)
            && items.ValueKind == JsonValueKind.Array
            && items.GetArrayLength() > 0
            && items[0].TryGetProperty("html_url", out JsonElement urlElement))
        {
            existingUrl = urlElement.GetString();
        }

        return new IssueSearchResult(
            IsSuccess: true,
            DuplicateFound: totalCount > 0,
            ExistingIssueUrl: existingUrl,
            Detail: $"matches={totalCount}");
    }

    public async Task<IssueFilingResult> FileIssueAsync(
        RepoRef repo,
        GeneratedIssue issue,
        CancellationToken cancellationToken = default)
    {
        if (!TryGuard(out string? guard))
        {
            return new IssueFilingResult(IsSuccess: false, IssueUrl: null, Detail: guard!);
        }

        string relativePath = $"repos/{repo.Owner}/{repo.Name}/issues";
        Uri endpoint = BackendGateway.BuildEndpoint(_configuration.GitHubApiBaseUri!, relativePath);
        if (!HostAllowed(endpoint, out string? hostRejection))
        {
            return new IssueFilingResult(IsSuccess: false, IssueUrl: null, Detail: hostRejection!);
        }

        object payload = new
        {
            title = issue.Title,
            body = issue.Body,
            labels = issue.Labels
        };

        using BackendJsonResult result = await _transport.SendJsonAsync(
            HttpMethod.Post, endpoint, payload, ApplyAuthentication, cancellationToken);

        if (!result.CallResult.IsSuccess)
        {
            return new IssueFilingResult(IsSuccess: false, IssueUrl: null, Detail: $"file-failed: {result.CallResult.Detail}");
        }

        string? issueUrl = null;
        if (result.Payload is not null
            && result.Payload.RootElement.TryGetProperty("html_url", out JsonElement urlElement))
        {
            issueUrl = urlElement.GetString();
        }

        return new IssueFilingResult(IsSuccess: true, IssueUrl: issueUrl, Detail: result.CallResult.Detail);
    }

    private bool TryGuard(out string? disabledReason)
    {
        if (_configuration.GitHubApiBaseUri is null || string.IsNullOrWhiteSpace(_configuration.GitHubToken))
        {
            disabledReason =
                "github-disabled: set HONUA_DEVOPS_GITHUB_TOKEN (and HONUA_DEVOPS_GITHUB_API_BASE_URL) to enable issue filing.";
            return false;
        }

        disabledReason = null;
        return true;
    }

    private bool HostAllowed(Uri endpoint, out string? rejection)
    {
        // Deny-by-default host posture, matching SupportGateway / JiraConnector.
        if (_configuration.AllowedHosts.Count == 0)
        {
            rejection =
                "github-host-rejected: HONUA_DEVOPS_BUGREPORT_ALLOWED_HOSTS is empty.";
            return false;
        }

        string host = endpoint.Host.ToLowerInvariant();
        if (!_configuration.AllowedHosts.Any(allowed => string.Equals(allowed, host, StringComparison.Ordinal)))
        {
            rejection = $"github-host-rejected: `{host}` is not in HONUA_DEVOPS_BUGREPORT_ALLOWED_HOSTS.";
            return false;
        }

        rejection = null;
        return true;
    }

    private void ApplyAuthentication(HttpRequestMessage request)
    {
        // GitHub REST v3 accepts a fine-grained/classic token as a Bearer credential.
        // The User-Agent header is required by the GitHub API or it rejects the call.
        request.Headers.UserAgent.TryParseAdd("honua-devops");
        request.Headers.Accept.TryParseAdd("application/vnd.github+json");
        if (!string.IsNullOrWhiteSpace(_configuration.GitHubToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _configuration.GitHubToken);
        }
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }
}
