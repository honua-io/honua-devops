using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Honua.DevOps.Agent.Operations;

internal sealed class HttpJsonTransport(HttpClient httpClient)
{
    private const int BodyPreviewMaxLength = 400;

    internal async Task<BackendCallResult> SendAsync(
        HttpMethod method,
        Uri endpoint,
        object? payload,
        Action<HttpRequestMessage> applyAuth,
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(method, endpoint);
        if (payload is not null)
        {
            request.Content = JsonContent.Create(payload);
        }
        applyAuth(request);

        try
        {
            using HttpResponseMessage response = await httpClient.SendAsync(
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
                PayloadPreview: Redaction.Scrub(exception.Message));
        }
    }

    internal async Task<BackendJsonResult> SendJsonAsync(
        HttpMethod method,
        Uri endpoint,
        object? payload,
        Action<HttpRequestMessage> applyAuth,
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(method, endpoint);
        if (payload is not null)
        {
            request.Content = JsonContent.Create(payload);
        }
        applyAuth(request);

        try
        {
            using HttpResponseMessage response = await httpClient.SendAsync(
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
                    PayloadPreview: Redaction.Scrub(exception.Message)),
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

    private static async Task<string> ReadBodyPreviewAsync(HttpContent content, CancellationToken cancellationToken)
    {
        await using Stream stream = await content.ReadAsStreamAsync(cancellationToken);
        using StreamReader reader = new(
            stream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 1024);

        char[] buffer = new char[BodyPreviewMaxLength + 1];
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

        bool truncated = totalRead > BodyPreviewMaxLength;
        string body = new(buffer, 0, Math.Min(totalRead, BodyPreviewMaxLength));
        return SummarizeBody(body, truncated);
    }

    internal static string SummarizeBody(string body, bool alreadyTruncated = false)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return "empty response body";
        }

        string compact = Redaction.Scrub(body.ReplaceLineEndings(" ").Trim());
        if (compact.Length <= BodyPreviewMaxLength && !alreadyTruncated)
        {
            return compact;
        }

        string preview = compact.Length > BodyPreviewMaxLength
            ? compact[..BodyPreviewMaxLength]
            : compact;

        return $"{preview}...";
    }
}
