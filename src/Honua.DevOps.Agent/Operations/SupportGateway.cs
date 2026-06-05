using System.Net.Http.Headers;
using Honua.DevOps.Agent.Operations.GuidedFix;
using Honua.DevOps.Agent.Operations.Troubleshooting;

namespace Honua.DevOps.Agent.Operations;

internal sealed class SupportGateway : IDisposable
{
    private const string OperatorRoleHeader = "X-Operator-Role";

    private readonly BackendConfiguration configuration;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly HttpJsonTransport _transport;

    internal SupportGateway(BackendConfiguration configuration, HttpClient? httpClient = null)
    {
        this.configuration = configuration;
        _httpClient = httpClient ?? HttpClientFactory.Create(configuration.RequestTimeout);
        _ownsHttpClient = httpClient is null;
        _transport = new HttpJsonTransport(_httpClient);
    }

    internal BackendConfiguration Configuration => configuration;

    internal async Task<BackendCallResult> ListPendingTicketsAsync(CancellationToken cancellationToken = default)
    {
        if (configuration.SupportApiBaseUri is null)
        {
            return new BackendCallResult(
                IsSuccess: false,
                Endpoint: "none",
                Detail: "support-api-disabled",
                PayloadPreview: "SupportApiBaseUri is not configured. Set HONUA_DEVOPS_SUPPORT_API_BASE_URL to enable.");
        }

        return await SendAsync(
            HttpMethod.Get,
            configuration.SupportApiTicketsPath,
            payload: null,
            cancellationToken);
    }

    internal async Task<BackendJsonResult> ListPendingTicketsJsonAsync(CancellationToken cancellationToken = default)
    {
        if (configuration.SupportApiBaseUri is null)
        {
            return new BackendJsonResult(
                new BackendCallResult(
                    IsSuccess: false,
                    Endpoint: "none",
                    Detail: "support-api-disabled",
                    PayloadPreview: "SupportApiBaseUri is not configured. Set HONUA_DEVOPS_SUPPORT_API_BASE_URL to enable."),
                null);
        }

        return await SendJsonAsync(
            HttpMethod.Get,
            configuration.SupportApiTicketsPath,
            payload: null,
            cancellationToken);
    }

    internal async Task<BackendCallResult> GetTicketAsync(string ticketId, CancellationToken cancellationToken = default)
    {
        if (configuration.SupportApiBaseUri is null)
        {
            return new BackendCallResult(
                IsSuccess: false,
                Endpoint: "none",
                Detail: "support-api-disabled",
                PayloadPreview: "SupportApiBaseUri is not configured. Set HONUA_DEVOPS_SUPPORT_API_BASE_URL to enable.");
        }

        string relativePath = BuildTicketPath(ticketId);
        return await SendAsync(
            HttpMethod.Get,
            relativePath,
            payload: null,
            cancellationToken);
    }

    internal async Task<BackendCallResult> PostDiagnosisAsync(
        string ticketId,
        GuidedFixResult diagnosis,
        OperationEvidence? evidence = null,
        DiagnosisScorecard? diagnosisScorecard = null,
        CancellationToken cancellationToken = default)
    {
        if (configuration.SupportApiBaseUri is null)
        {
            return new BackendCallResult(
                IsSuccess: false,
                Endpoint: "none",
                Detail: "support-api-disabled",
                PayloadPreview: "SupportApiBaseUri is not configured. Set HONUA_DEVOPS_SUPPORT_API_BASE_URL to enable.");
        }

        string relativePath = BuildTicketPath(ticketId, "diagnosis");
        object payload = new
        {
            summary = diagnosis.DiagnosisSummary,
            confidence = diagnosis.Confidence,
            mode = diagnosis.Mode.ToConfigValue(),
            guidedCommands = diagnosis.GuidedCommands,
            validationSteps = diagnosis.ValidationSteps,
            evidence,
            diagnosisScorecard,
            escalation = diagnosis.Escalation is null
                ? null
                : new
                {
                    justification = diagnosis.Escalation.Justification,
                    accessScope = diagnosis.Escalation.AccessScope,
                    ttlMinutes = diagnosis.Escalation.TtlMinutes,
                    rollbackIntent = diagnosis.Escalation.RollbackIntent,
                    // Escalation rationale: the signal/trigger that caused the hand-off, so
                    // honua-support and the console can render "why escalated" alongside the
                    // diagnosis without re-deriving it from the justification sentence.
                    trigger = diagnosis.Escalation.Trigger,
                    signal = diagnosis.Escalation.Signal
                }
        };

        return await SendAsync(
            HttpMethod.Post,
            relativePath,
            payload,
            cancellationToken);
    }

    internal async Task<BackendCallResult> TriggerAutoBundleAsync(
        string ticketId,
        string? instanceUrl = null,
        string? apiKey = null,
        CancellationToken cancellationToken = default)
    {
        if (configuration.SupportApiBaseUri is null)
        {
            return new BackendCallResult(
                IsSuccess: false,
                Endpoint: "none",
                Detail: "support-api-disabled",
                PayloadPreview: "SupportApiBaseUri is not configured. Set HONUA_DEVOPS_SUPPORT_API_BASE_URL to enable.");
        }

        if (!configuration.SupportAutoBundleEnabled)
        {
            return new BackendCallResult(
                IsSuccess: false,
                Endpoint: "none",
                Detail: "auto-bundle-disabled",
                PayloadPreview: "Auto-bundle is disabled. Set HONUA_DEVOPS_SUPPORT_AUTOBUNDLE_ENABLED=true to opt in.");
        }

        if (!TryValidateInstanceHost(instanceUrl, configuration.SupportAutoBundleAllowedHosts, out string hostRejection))
        {
            return new BackendCallResult(
                IsSuccess: false,
                Endpoint: "none",
                Detail: "auto-bundle-host-rejected",
                PayloadPreview: hostRejection);
        }

        string relativePath = BuildTicketPath(ticketId, "auto-bundle");
        return await SendAsync(
            HttpMethod.Post,
            relativePath,
            payload: new { instanceUrl, apiKey },
            cancellationToken);
    }

    private static bool TryValidateInstanceHost(
        string? instanceUrl,
        IReadOnlyList<string>? allowedHosts,
        out string rejection)
    {
        rejection = string.Empty;

        if (string.IsNullOrWhiteSpace(instanceUrl))
        {
            rejection = "Auto-bundle requires a non-empty instanceUrl.";
            return false;
        }

        if (!Uri.TryCreate(instanceUrl.Trim(), UriKind.Absolute, out Uri? parsed) ||
            (!parsed.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
             !parsed.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            rejection = "Auto-bundle instanceUrl must be an absolute http(s) URL.";
            return false;
        }

        if (allowedHosts is null || allowedHosts.Count == 0)
        {
            rejection =
                "Auto-bundle host allowlist is empty. Set HONUA_DEVOPS_SUPPORT_AUTOBUNDLE_ALLOWED_HOSTS to a comma-separated list of permitted hosts.";
            return false;
        }

        string host = parsed.Host.ToLowerInvariant();
        if (!allowedHosts.Any(allowed => string.Equals(allowed, host, StringComparison.Ordinal)))
        {
            rejection =
                $"Auto-bundle instanceUrl host `{host}` is not in HONUA_DEVOPS_SUPPORT_AUTOBUNDLE_ALLOWED_HOSTS.";
            return false;
        }

        return true;
    }

    internal async Task<BackendCallResult> CloseTicketAsync(
        string ticketId,
        string resolutionSummary,
        CancellationToken cancellationToken = default)
    {
        if (configuration.SupportApiBaseUri is null)
        {
            return new BackendCallResult(
                IsSuccess: false,
                Endpoint: "none",
                Detail: "support-api-disabled",
                PayloadPreview: "SupportApiBaseUri is not configured. Set HONUA_DEVOPS_SUPPORT_API_BASE_URL to enable.");
        }

        string relativePath = BuildTicketPath(ticketId, "close");
        object payload = new { resolutionSummary };
        return await SendAsync(
            HttpMethod.Post,
            relativePath,
            payload,
            cancellationToken);
    }

    private string BuildTicketPath(string ticketId, string? childPath = null)
    {
        string trimmedTicketId = ticketId.Trim();
        if (string.IsNullOrWhiteSpace(trimmedTicketId))
        {
            throw new ArgumentException("Ticket id is required.", nameof(ticketId));
        }

        string encodedTicketId = Uri.EscapeDataString(trimmedTicketId);
        return string.IsNullOrWhiteSpace(childPath)
            ? $"{configuration.SupportApiTicketsPath}/{encodedTicketId}"
            : $"{configuration.SupportApiTicketsPath}/{encodedTicketId}/{childPath}";
    }

    private Task<BackendCallResult> SendAsync(
        HttpMethod method,
        string relativePath,
        object? payload,
        CancellationToken cancellationToken)
    {
        Uri endpoint = BackendGateway.BuildEndpoint(configuration.SupportApiBaseUri!, relativePath);
        return _transport.SendAsync(method, endpoint, payload, ApplySupportAuthentication, cancellationToken);
    }

    private Task<BackendJsonResult> SendJsonAsync(
        HttpMethod method,
        string relativePath,
        object? payload,
        CancellationToken cancellationToken)
    {
        Uri endpoint = BackendGateway.BuildEndpoint(configuration.SupportApiBaseUri!, relativePath);
        return _transport.SendJsonAsync(method, endpoint, payload, ApplySupportAuthentication, cancellationToken);
    }

    private void ApplySupportAuthentication(HttpRequestMessage request)
    {
        if (!string.IsNullOrWhiteSpace(configuration.SupportApiBearerToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", configuration.SupportApiBearerToken);
            return;
        }

        request.Headers.TryAddWithoutValidation(OperatorRoleHeader, "true");
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }
}
