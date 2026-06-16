using System.Net.Http.Headers;
using System.Text;

namespace Honua.DevOps.Agent.Operations.WorkIntake;

/// <summary>
/// Jira Cloud work-item connector (REST v3, API-token Basic auth: email + token).
/// Modeled on <see cref="SupportGateway"/>: graceful-disabled when the base URI
/// is unset, host-allowlist enforced before any outbound call, and all I/O goes
/// through the shared <see cref="HttpJsonTransport"/>.
///
/// Write-back is a stub for this PR: <see cref="PostProvenanceStubAsync"/> posts
/// a single comment via <c>POST /rest/api/3/issue/{key}/comment</c>. No status
/// transition, no preview link, no deliverable.
/// </summary>
internal sealed class JiraConnector : IWorkItemConnector, IDisposable
{
    private const string IssuePathTemplate = "rest/api/3/issue";

    private readonly WorkIntakeConfiguration _configuration;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly HttpJsonTransport _transport;

    internal JiraConnector(WorkIntakeConfiguration configuration, HttpClient? httpClient = null)
    {
        _configuration = configuration;
        _httpClient = httpClient ?? HttpClientFactory.Create(TimeSpan.FromSeconds(20));
        _ownsHttpClient = httpClient is null;
        _transport = new HttpJsonTransport(_httpClient);
    }

    public bool IsEnabled => _configuration.JiraBaseUri is not null;

    public Task<BackendCallResult> GetIssueAsync(string issueKey, CancellationToken cancellationToken = default)
    {
        if (!TryGuard(out BackendCallResult? guard))
        {
            return Task.FromResult(guard!);
        }

        string relativePath = BuildIssuePath(issueKey);
        Uri endpoint = BackendGateway.BuildEndpoint(_configuration.JiraBaseUri!, relativePath);
        if (!HostAllowed(endpoint, out BackendCallResult? hostRejection))
        {
            return Task.FromResult(hostRejection!);
        }

        return _transport.SendAsync(HttpMethod.Get, endpoint, payload: null, ApplyAuthentication, cancellationToken);
    }

    public Task<BackendCallResult> PostProvenanceStubAsync(
        WorkItem workItem,
        string message,
        CancellationToken cancellationToken = default)
    {
        if (!TryGuard(out BackendCallResult? guard))
        {
            return Task.FromResult(guard!);
        }

        string relativePath = $"{BuildIssuePath(workItem.ExternalId)}/comment";
        Uri endpoint = BackendGateway.BuildEndpoint(_configuration.JiraBaseUri!, relativePath);
        if (!HostAllowed(endpoint, out BackendCallResult? hostRejection))
        {
            return Task.FromResult(hostRejection!);
        }

        // Jira Cloud REST v3 comment bodies are Atlassian Document Format (ADF).
        object payload = new
        {
            body = new
            {
                type = "doc",
                version = 1,
                content = new object[]
                {
                    new
                    {
                        type = "paragraph",
                        content = new object[]
                        {
                            new { type = "text", text = message }
                        }
                    }
                }
            }
        };

        return _transport.SendAsync(HttpMethod.Post, endpoint, payload, ApplyAuthentication, cancellationToken);
    }

    private bool TryGuard(out BackendCallResult? disabled)
    {
        if (_configuration.JiraBaseUri is null)
        {
            disabled = new BackendCallResult(
                IsSuccess: false,
                Endpoint: "none",
                Detail: "jira-disabled",
                PayloadPreview: "Jira base URI is not configured. Set HONUA_DEVOPS_JIRA_BASE_URL to enable.");
            return false;
        }

        disabled = null;
        return true;
    }

    private bool HostAllowed(Uri endpoint, out BackendCallResult? rejection)
    {
        // No allowlist configured means the connector refuses any outbound write,
        // matching SupportGateway's deny-by-default host posture.
        if (_configuration.AllowedHosts.Count == 0)
        {
            rejection = new BackendCallResult(
                IsSuccess: false,
                Endpoint: "none",
                Detail: "jira-host-rejected",
                PayloadPreview:
                    "Jira host allowlist is empty. Set HONUA_DEVOPS_INTAKE_ALLOWED_HOSTS to a comma-separated list of permitted hosts.");
            return false;
        }

        string host = endpoint.Host.ToLowerInvariant();
        if (!_configuration.AllowedHosts.Any(allowed => string.Equals(allowed, host, StringComparison.Ordinal)))
        {
            rejection = new BackendCallResult(
                IsSuccess: false,
                Endpoint: "none",
                Detail: "jira-host-rejected",
                PayloadPreview: $"Jira host `{host}` is not in HONUA_DEVOPS_INTAKE_ALLOWED_HOSTS.");
            return false;
        }

        rejection = null;
        return true;
    }

    private void ApplyAuthentication(HttpRequestMessage request)
    {
        // Jira Cloud API-token auth is HTTP Basic with email as the username and
        // the API token as the password (base64 of "email:token").
        if (string.IsNullOrWhiteSpace(_configuration.JiraUserEmail) ||
            string.IsNullOrWhiteSpace(_configuration.JiraApiToken))
        {
            return;
        }

        string raw = $"{_configuration.JiraUserEmail}:{_configuration.JiraApiToken}";
        string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(raw));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", encoded);
    }

    private static string BuildIssuePath(string issueKey)
    {
        string trimmed = issueKey.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            throw new ArgumentException("Issue key is required.", nameof(issueKey));
        }

        return $"{IssuePathTemplate}/{Uri.EscapeDataString(trimmed)}";
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }
}

/// <summary>
/// TODO (follow-up PR): Jira Data Center connector. Data Center exposes a
/// different REST surface and auth model (PAT/Bearer or session) than Cloud's
/// API-token Basic auth. This stub holds the provider seam open and must not be
/// selected at runtime in this PR.
/// </summary>
internal sealed class JiraDataCenterConnector : IWorkItemConnector
{
    public bool IsEnabled => false;

    public Task<BackendCallResult> GetIssueAsync(string issueKey, CancellationToken cancellationToken = default)
        => throw new NotImplementedException(
            "Jira Data Center connector is not implemented yet (tracked for a follow-up PR).");

    public Task<BackendCallResult> PostProvenanceStubAsync(
        WorkItem workItem,
        string message,
        CancellationToken cancellationToken = default)
        => throw new NotImplementedException(
            "Jira Data Center connector is not implemented yet (tracked for a follow-up PR).");
}
