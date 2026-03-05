using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;

namespace Honua.DevOps.Agent.Operations;

internal sealed class BackendGateway(BackendConfiguration configuration, HttpClient? httpClient = null) : IDisposable
{
    private const string HonuaApiKeyHeader = "X-API-Key";
    private const string HonuaMetadataApiVersion = "honua.io/v1alpha1";

    private readonly HttpClient _httpClient = httpClient ?? new HttpClient
    {
        Timeout = configuration.RequestTimeout
    };
    private readonly bool _ownsHttpClient = httpClient is null;

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

    internal async Task<BackendCallResult> RequestTroubleshootAsync(
        string service,
        string environment,
        string incidentSummary,
        string suspectedComponent,
        string businessImpact,
        CancellationToken cancellationToken)
    {
        _ = incidentSummary;
        _ = suspectedComponent;
        _ = businessImpact;
        string operation =
            $"troubleshoot:{NormalizeResourceToken(service, "service")}:{NormalizeResourceToken(environment, "env")}";
        BackendCallResult[] calls =
        [
            await GetFromHonuaAsync(configuration.HonuaAdminErrorsPath, cancellationToken),
            await GetFromHonuaAsync(configuration.HonuaMetricsHealthPath, cancellationToken),
            await GetFromHonuaAsync(configuration.HonuaMetricsPerformancePath, cancellationToken)
        ];

        return CombineResults(operation, calls);
    }

    internal async Task<BackendCallResult> RequestTuneAsync(
        string service,
        string environment,
        string workloadProfile,
        string bottleneck,
        string targetSlo,
        CancellationToken cancellationToken)
    {
        _ = workloadProfile;
        _ = bottleneck;
        _ = targetSlo;
        string operation =
            $"tune:{NormalizeResourceToken(service, "service")}:{NormalizeResourceToken(environment, "env")}";
        BackendCallResult[] calls =
        [
            await GetFromHonuaAsync(configuration.HonuaMetricsPerformancePath, cancellationToken),
            await GetFromHonuaAsync(configuration.HonuaMetricsDatabasePath, cancellationToken),
            await GetFromHonuaAsync(configuration.HonuaMetricsCachePath, cancellationToken),
            await GetFromHonuaAsync(configuration.HonuaMetricsMemoryPath, cancellationToken),
            await GetFromHonuaAsync(configuration.HonuaQueryCacheStatisticsPath, cancellationToken)
        ];

        return CombineResults(operation, calls);
    }

    internal async Task<BackendCallResult> RequestUpgradeAsync(
        string environment,
        string currentVersion,
        string targetVersion,
        string maintenanceWindow,
        string constraints,
        CancellationToken cancellationToken)
    {
        _ = maintenanceWindow;
        _ = constraints;
        string operation = $"upgrade:{NormalizeResourceToken(environment, "env")}:{currentVersion}->{targetVersion}";
        BackendCallResult[] calls =
        [
            await GetFromHonuaAsync(configuration.HonuaAdminVersionPath, cancellationToken),
            await GetFromHonuaAsync(configuration.HonuaAdminCapabilitiesPath, cancellationToken),
            await GetFromHonuaAsync(configuration.HonuaReadinessPath, cancellationToken)
        ];

        return CombineResults(operation, calls);
    }

    internal async Task<BackendCallResult> RequestGitOpsDeployAsync(
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
        return await RequestGitOpsDeployAsync(
            service,
            environments,
            revision,
            action,
            changeSummary,
            gitOpsTool,
            terraformRepository,
            terraformRef,
            deploymentTargets,
            dryRun: true,
            cancellationToken);
    }

    internal async Task<BackendCallResult> RequestGitOpsDeployAsync(
        string service,
        string[] environments,
        string revision,
        string action,
        string changeSummary,
        string gitOpsTool,
        string terraformRepository,
        string terraformRef,
        string[] deploymentTargets,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        object requestBody = BuildManifestApplyRequest(
            service,
            environments,
            revision,
            action,
            changeSummary,
            gitOpsTool,
            terraformRepository,
            terraformRef,
            deploymentTargets,
            dryRun);

        BackendCallResult applyResult = await PostToHonuaAsync(
            configuration.HonuaManifestApplyPath,
            requestBody,
            cancellationToken);

        BackendCallResult manifestResult = await GetFromHonuaAsync(configuration.HonuaManifestExportPath, cancellationToken);

        return CombineResults("gitops-deploy", [applyResult, manifestResult]);
    }

    internal async Task<BackendCallResult> RequestRequirementsAnalysisAsync(
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
        _ = customerRequirements;
        _ = scaleProfile;
        _ = complianceNeeds;
        _ = budgetProfile;
        _ = terraformRepository;
        _ = terraformRef;
        string operation =
            $"requirements-analysis:{NormalizeResourceToken(preferredCloud, "cloud")}:{deploymentTargets.Length}-targets";
        BackendCallResult[] calls =
        [
            await GetFromHonuaAsync(configuration.HonuaAdminCapabilitiesPath, cancellationToken),
            await GetFromHonuaAsync(configuration.HonuaManifestExportPath, cancellationToken),
            await GetFromHonuaAsync(configuration.HonuaMetricsPerformancePath, cancellationToken)
        ];

        return CombineResults(operation, calls);
    }

    internal async Task<BackendCallResult> RequestTopologyRecommendationAsync(
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
        _ = trafficProfile;
        _ = riskTolerance;
        _ = terraformRepository;
        _ = terraformRef;
        string operation =
            $"topology-recommendation:{NormalizeResourceToken(environment, "env")}:waf={enableWaf}:nginx={useNginxProxy}:edge-rl={enableEdgeRateLimiting}";
        BackendCallResult[] calls =
        [
            await GetFromHonuaAsync(configuration.HonuaAdminTelemetryPath, cancellationToken),
            await GetFromHonuaAsync(configuration.HonuaMetricsPerformancePath, cancellationToken),
            await GetFromHonuaAsync(configuration.HonuaMetricsCachePath, cancellationToken)
        ];

        return CombineResults(operation, calls);
    }

    internal Task<BackendCallResult> ProbeHonuaAsync(CancellationToken cancellationToken)
    {
        return GetFromHonuaAsync(configuration.HonuaReadinessPath, cancellationToken);
    }

    internal Task<BackendCallResult> ProbeOtelAsync(CancellationToken cancellationToken)
    {
        return SendAsync(
            configuration.OTelBaseUri,
            HttpMethod.Get,
            configuration.OTelHealthPath,
            payload: null,
            configuration.OTelApiKey,
            ApiKeyTransport.Bearer,
            cancellationToken);
    }

    private Task<BackendCallResult> GetFromHonuaAsync(string relativePath, CancellationToken cancellationToken)
    {
        return SendAsync(
            configuration.HonuaApiBaseUri,
            HttpMethod.Get,
            relativePath,
            payload: null,
            configuration.HonuaApiKey,
            ApiKeyTransport.XApiKey,
            cancellationToken);
    }

    private Task<BackendCallResult> PostToHonuaAsync(string relativePath, object payload, CancellationToken cancellationToken)
    {
        return SendAsync(
            configuration.HonuaApiBaseUri,
            HttpMethod.Post,
            relativePath,
            payload,
            configuration.HonuaApiKey,
            ApiKeyTransport.XApiKey,
            cancellationToken);
    }

    private Task<BackendCallResult> PostToOtelAsync(string relativePath, object payload, CancellationToken cancellationToken)
    {
        return SendAsync(
            configuration.OTelBaseUri,
            HttpMethod.Post,
            relativePath,
            payload,
            configuration.OTelApiKey,
            ApiKeyTransport.Bearer,
            cancellationToken);
    }

    private async Task<BackendCallResult> SendAsync(
        Uri baseUri,
        HttpMethod method,
        string relativePath,
        object? payload,
        string? apiKey,
        ApiKeyTransport apiKeyTransport,
        CancellationToken cancellationToken)
    {
        Uri endpoint = BuildEndpoint(baseUri, relativePath);
        using HttpRequestMessage request = new(method, endpoint);
        if (payload is not null)
        {
            request.Content = JsonContent.Create(payload);
        }

        ApplyApiKey(request, apiKey, apiKeyTransport);

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

    private static void ApplyApiKey(HttpRequestMessage request, string? apiKey, ApiKeyTransport apiKeyTransport)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return;
        }

        switch (apiKeyTransport)
        {
            case ApiKeyTransport.Bearer:
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                break;
            case ApiKeyTransport.XApiKey:
                request.Headers.TryAddWithoutValidation(HonuaApiKeyHeader, apiKey);
                break;
            case ApiKeyTransport.None:
            default:
                break;
        }
    }

    private static BackendCallResult CombineResults(string operation, IReadOnlyList<BackendCallResult> calls)
    {
        if (calls.Count == 0)
        {
            return new BackendCallResult(
                IsSuccess: false,
                Endpoint: "none",
                Detail: $"{operation}: no endpoint calls were executed",
                PayloadPreview: "No backend response captured.");
        }

        int successCount = calls.Count(call => call.IsSuccess);
        string endpoints = string.Join(", ", calls.Select(call => call.Endpoint));
        string detail = $"{operation}: {successCount}/{calls.Count} endpoint calls succeeded";
        string preview = string.Join(
            " | ",
            calls.Select(call => $"{call.Detail} :: {call.PayloadPreview}"));

        return new BackendCallResult(
            IsSuccess: successCount > 0,
            Endpoint: endpoints,
            Detail: detail,
            PayloadPreview: SummarizeBody(preview));
    }

    private static object BuildManifestApplyRequest(
        string service,
        IReadOnlyList<string> environments,
        string revision,
        string action,
        string changeSummary,
        string gitOpsTool,
        string terraformRepository,
        string terraformRef,
        IReadOnlyList<string> deploymentTargets,
        bool dryRun)
    {
        string normalizedService = NormalizeResourceToken(service, "honua-service");
        string normalizedChangeSummary = NormalizeText(changeSummary, "not provided");

        var resources = environments
            .Select(environment =>
            {
                string normalizedEnvironment = NormalizeResourceToken(environment, "default");
                return new
                {
                    apiVersion = HonuaMetadataApiVersion,
                    kind = "Service",
                    metadata = new
                    {
                        name = $"{normalizedService}-{normalizedEnvironment}",
                        @namespace = normalizedEnvironment,
                        labels = new Dictionary<string, string>
                        {
                            ["managed-by"] = "honua-devops",
                            ["service"] = normalizedService,
                            ["environment"] = normalizedEnvironment
                        },
                        annotations = new Dictionary<string, string>
                        {
                            ["honua.devops/action"] = action,
                            ["honua.devops/revision"] = revision,
                            ["honua.devops/gitops-tool"] = gitOpsTool,
                            ["honua.devops/change-summary"] = normalizedChangeSummary,
                            ["honua.devops/terraform-ref"] = terraformRef,
                            ["honua.devops/requested-at"] = DateTimeOffset.UtcNow.ToString("O")
                        }
                    },
                    spec = new
                    {
                        description = $"GitOps deployment request for {normalizedService} in {normalizedEnvironment}.",
                        srid = 4326,
                        deployment = new
                        {
                            service = normalizedService,
                            environment = normalizedEnvironment,
                            revision,
                            action,
                            changeSummary = normalizedChangeSummary,
                            gitOpsTool,
                            terraform = new
                            {
                                repository = terraformRepository,
                                @ref = terraformRef,
                                targets = deploymentTargets
                            }
                        }
                    }
                };
            })
            .ToArray();

        bool prune = action.Contains("prune", StringComparison.OrdinalIgnoreCase);
        return new
        {
            resources,
            dryRun,
            prune
        };
    }

    private static string NormalizeResourceToken(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        StringBuilder builder = new();
        bool appendHyphen = false;

        foreach (char character in value.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(character);
                appendHyphen = true;
                continue;
            }

            if (appendHyphen)
            {
                builder.Append('-');
                appendHyphen = false;
            }
        }

        string normalized = builder.ToString().Trim('-');
        return string.IsNullOrWhiteSpace(normalized) ? fallback : normalized;
    }

    private static string NormalizeText(string? value, string fallback)
    {
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }

    internal static Uri BuildEndpoint(Uri baseUri, string relativePath)
    {
        string cleanedPath = relativePath.TrimStart('/');
        UriBuilder builder = new(baseUri)
        {
            Query = string.Empty,
            Fragment = string.Empty
        };

        if (!builder.Path.EndsWith("/", StringComparison.Ordinal))
        {
            builder.Path += "/";
        }

        Uri normalizedBaseUri = builder.Uri;
        return new Uri(normalizedBaseUri, cleanedPath);
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

    private enum ApiKeyTransport
    {
        None = 0,
        Bearer = 1,
        XApiKey = 2
    }
}
