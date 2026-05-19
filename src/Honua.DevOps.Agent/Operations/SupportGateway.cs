using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Honua.DevOps.Agent.Operations.GuidedFix;
using Honua.DevOps.Agent.Operations.Troubleshooting;

namespace Honua.DevOps.Agent.Operations;

internal sealed class SupportGateway(BackendConfiguration configuration, HttpClient? httpClient = null) : IDisposable
{
    private const string OperatorRoleHeader = "X-Operator-Role";

    private readonly HttpClient _httpClient = httpClient ?? new HttpClient
    {
        Timeout = configuration.RequestTimeout
    };
    private readonly bool _ownsHttpClient = httpClient is null;

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
                    rollbackIntent = diagnosis.Escalation.RollbackIntent
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

        string relativePath = BuildTicketPath(ticketId, "auto-bundle");
        return await SendAsync(
            HttpMethod.Post,
            relativePath,
            payload: new { instanceUrl, apiKey },
            cancellationToken);
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

    private async Task<BackendCallResult> SendAsync(
        HttpMethod method,
        string relativePath,
        object? payload,
        CancellationToken cancellationToken)
    {
        Uri endpoint = BackendGateway.BuildEndpoint(configuration.SupportApiBaseUri!, relativePath);
        using HttpRequestMessage request = new(method, endpoint);
        if (payload is not null)
        {
            request.Content = JsonContent.Create(payload);
        }

        ApplySupportAuthentication(request);

        try
        {
            using HttpResponseMessage response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            bool isSuccess = response.IsSuccessStatusCode;
            string detail = $"{(int)response.StatusCode} {response.ReasonPhrase}";
            string preview = await ReadBodyPreviewAsync(response.Content, cancellationToken);

            return new BackendCallResult(isSuccess, endpoint.ToString(), detail, preview);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new BackendCallResult(
                IsSuccess: false,
                Endpoint: endpoint.ToString(),
                Detail: $"request-failed: {exception.GetType().Name}",
                PayloadPreview: exception.Message);
        }
    }

    private async Task<BackendJsonResult> SendJsonAsync(
        HttpMethod method,
        string relativePath,
        object? payload,
        CancellationToken cancellationToken)
    {
        Uri endpoint = BackendGateway.BuildEndpoint(configuration.SupportApiBaseUri!, relativePath);
        using HttpRequestMessage request = new(method, endpoint);
        if (payload is not null)
        {
            request.Content = JsonContent.Create(payload);
        }

        ApplySupportAuthentication(request);

        try
        {
            using HttpResponseMessage response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            string body = response.Content is null
                ? string.Empty
                : await response.Content.ReadAsStringAsync(cancellationToken);
            bool isSuccess = response.IsSuccessStatusCode;
            string detail = $"{(int)response.StatusCode} {response.ReasonPhrase}";
            JsonDocument? payloadDocument = TryParseJsonDocument(body);

            return new BackendJsonResult(
                new BackendCallResult(
                    isSuccess,
                    endpoint.ToString(),
                    detail,
                    SummarizeBody(body)),
                payloadDocument);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new BackendJsonResult(
                new BackendCallResult(
                    IsSuccess: false,
                    Endpoint: endpoint.ToString(),
                    Detail: $"request-failed: {exception.GetType().Name}",
                    PayloadPreview: exception.Message),
                null);
        }
    }

    private static JsonDocument? TryParseJsonDocument(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return null;
        }
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

    private static async Task<string> ReadBodyPreviewAsync(HttpContent content, CancellationToken cancellationToken)
    {
        await using Stream stream = await content.ReadAsStreamAsync(cancellationToken);
        using StreamReader reader = new(
            stream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 1024);

        const int maxLength = 400;
        char[] buffer = new char[maxLength + 1];
        int totalRead = 0;

        while (totalRead < buffer.Length)
        {
            int read = await reader.ReadAsync(
                buffer.AsMemory(totalRead, buffer.Length - totalRead),
                cancellationToken);
            if (read == 0)
            {
                break;
            }

            totalRead += read;
        }

        if (totalRead == 0)
        {
            return "empty response body";
        }

        bool truncated = totalRead > maxLength;
        string body = new(buffer, 0, Math.Min(totalRead, maxLength));
        return SummarizeBody(body, truncated);
    }

    private static string SummarizeBody(string body, bool alreadyTruncated = false)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return "empty response body";
        }

        string compact = body.ReplaceLineEndings(" ").Trim();
        const int maxLength = 400;
        if (compact.Length <= maxLength && !alreadyTruncated)
        {
            return compact;
        }

        string preview = compact.Length > maxLength
            ? compact[..maxLength]
            : compact;

        return $"{preview}...";
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }
}
