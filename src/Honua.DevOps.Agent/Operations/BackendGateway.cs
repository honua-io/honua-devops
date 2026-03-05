using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace Honua.DevOps.Agent.Operations;

internal sealed class BackendGateway(BackendConfiguration configuration) : IDisposable
{
    private readonly HttpClient _httpClient = new()
    {
        Timeout = configuration.RequestTimeout
    };

    internal Task<BackendCallResult> QueryLogsAsync(
        string service,
        string environment,
        string timeframe,
        string symptoms,
        string logSample,
        CancellationToken cancellationToken)
    {
        return PostToOtelAsync(
            configuration.OTelLogsPath,
            new
            {
                service,
                environment,
                timeframe,
                symptoms,
                logSample
            },
            cancellationToken);
    }

    internal Task<BackendCallResult> QueryMetricsAsync(
        string service,
        string environment,
        string timeframe,
        string objective,
        string metricSnapshot,
        CancellationToken cancellationToken)
    {
        return PostToOtelAsync(
            configuration.OTelMetricsPath,
            new
            {
                service,
                environment,
                timeframe,
                objective,
                metricSnapshot
            },
            cancellationToken);
    }

    internal Task<BackendCallResult> RequestTroubleshootAsync(
        string service,
        string environment,
        string incidentSummary,
        string suspectedComponent,
        string businessImpact,
        CancellationToken cancellationToken)
    {
        return PostToHonuaAsync(
            configuration.HonuaTroubleshootPath,
            new
            {
                service,
                environment,
                incidentSummary,
                suspectedComponent,
                businessImpact
            },
            cancellationToken);
    }

    internal Task<BackendCallResult> RequestTuneAsync(
        string service,
        string environment,
        string workloadProfile,
        string bottleneck,
        string targetSlo,
        CancellationToken cancellationToken)
    {
        return PostToHonuaAsync(
            configuration.HonuaTunePath,
            new
            {
                service,
                environment,
                workloadProfile,
                bottleneck,
                targetSlo
            },
            cancellationToken);
    }

    internal Task<BackendCallResult> RequestUpgradeAsync(
        string environment,
        string currentVersion,
        string targetVersion,
        string maintenanceWindow,
        string constraints,
        CancellationToken cancellationToken)
    {
        return PostToHonuaAsync(
            configuration.HonuaUpgradePath,
            new
            {
                environment,
                currentVersion,
                targetVersion,
                maintenanceWindow,
                constraints
            },
            cancellationToken);
    }

    internal Task<BackendCallResult> RequestGitOpsDeployAsync(
        string service,
        string[] environments,
        string revision,
        string action,
        string changeSummary,
        string gitOpsTool,
        string terraformRepository,
        string terraformRef,
        string[] deploymentTargets,
        CancellationToken cancellationToken)
    {
        return PostToHonuaAsync(
            configuration.HonuaDeployPath,
            new
            {
                service,
                environments,
                revision,
                action,
                changeSummary,
                gitOpsTool,
                terraformRepository,
                terraformRef,
                deploymentTargets
            },
            cancellationToken);
    }

    internal Task<BackendCallResult> RequestRequirementsAnalysisAsync(
        string customerRequirements,
        string scaleProfile,
        string complianceNeeds,
        string budgetProfile,
        string preferredCloud,
        string terraformRepository,
        string terraformRef,
        string[] deploymentTargets,
        CancellationToken cancellationToken)
    {
        return PostToHonuaAsync(
            configuration.HonuaRequirementsPath,
            new
            {
                customerRequirements,
                scaleProfile,
                complianceNeeds,
                budgetProfile,
                preferredCloud,
                terraformRepository,
                terraformRef,
                deploymentTargets
            },
            cancellationToken);
    }

    internal Task<BackendCallResult> RequestTopologyRecommendationAsync(
        string environment,
        bool enableWaf,
        bool useNginxProxy,
        bool enableEdgeRateLimiting,
        string trafficProfile,
        string riskTolerance,
        string terraformRepository,
        string terraformRef,
        CancellationToken cancellationToken)
    {
        return PostToHonuaAsync(
            configuration.HonuaTopologyPath,
            new
            {
                environment,
                enableWaf,
                useNginxProxy,
                enableEdgeRateLimiting,
                trafficProfile,
                riskTolerance,
                terraformRepository,
                terraformRef
            },
            cancellationToken);
    }

    internal Task<BackendCallResult> ProbeHonuaAsync(CancellationToken cancellationToken)
    {
        return ProbeAsync(
            configuration.HonuaApiBaseUri,
            configuration.HonuaHealthPath,
            configuration.HonuaApiKey,
            cancellationToken);
    }

    internal Task<BackendCallResult> ProbeOtelAsync(CancellationToken cancellationToken)
    {
        return ProbeAsync(
            configuration.OTelBaseUri,
            configuration.OTelHealthPath,
            configuration.OTelApiKey,
            cancellationToken);
    }

    private Task<BackendCallResult> PostToHonuaAsync(string relativePath, object payload, CancellationToken cancellationToken)
    {
        return SendAsync(
            configuration.HonuaApiBaseUri,
            relativePath,
            payload,
            configuration.HonuaApiKey,
            cancellationToken);
    }

    private Task<BackendCallResult> PostToOtelAsync(string relativePath, object payload, CancellationToken cancellationToken)
    {
        return SendAsync(
            configuration.OTelBaseUri,
            relativePath,
            payload,
            configuration.OTelApiKey,
            cancellationToken);
    }

    private async Task<BackendCallResult> SendAsync(
        Uri baseUri,
        string relativePath,
        object payload,
        string? apiKey,
        CancellationToken cancellationToken)
    {
        Uri endpoint = BuildEndpoint(baseUri, relativePath);
        using HttpRequestMessage request = new(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(payload)
        };

        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        try
        {
            using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken);
            string body = await response.Content.ReadAsStringAsync(cancellationToken);
            bool isSuccess = response.IsSuccessStatusCode;
            string detail = $"{(int)response.StatusCode} {response.ReasonPhrase}";
            string preview = SummarizeBody(body);

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

    private async Task<BackendCallResult> ProbeAsync(
        Uri baseUri,
        string relativePath,
        string? apiKey,
        CancellationToken cancellationToken)
    {
        Uri endpoint = BuildEndpoint(baseUri, relativePath);
        using HttpRequestMessage request = new(HttpMethod.Get, endpoint);

        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        try
        {
            using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken);
            string body = await response.Content.ReadAsStringAsync(cancellationToken);
            return new BackendCallResult(
                IsSuccess: response.IsSuccessStatusCode,
                Endpoint: endpoint.ToString(),
                Detail: $"{(int)response.StatusCode} {response.ReasonPhrase}",
                PayloadPreview: SummarizeBody(body));
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

    private static Uri BuildEndpoint(Uri baseUri, string relativePath)
    {
        string cleanedPath = relativePath.StartsWith('/') ? relativePath[1..] : relativePath;
        return new Uri(baseUri, cleanedPath);
    }

    private static string SummarizeBody(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return "empty response body";
        }

        string compact = body.ReplaceLineEndings(" ").Trim();
        const int maxLength = 400;
        if (compact.Length <= maxLength)
        {
            return compact;
        }

        return $"{compact[..maxLength]}...";
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}
