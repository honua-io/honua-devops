using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Honua.DevOps.Agent.Operations.DesiredState;
using Honua.DevOps.Agent.Operations.GitOps;
using Honua.DevOps.Agent.Operations.Observability;

namespace Honua.DevOps.Agent.Operations;

internal sealed class BackendGateway : IDisposable
{
    private const string HonuaApiKeyHeader = "X-API-Key";
    private const string HonuaMetadataApiVersion = "honua.io/v1alpha1";

    private readonly BackendConfiguration configuration;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly HttpJsonTransport _transport;

    internal BackendGateway(BackendConfiguration configuration, HttpClient? httpClient = null)
    {
        this.configuration = configuration;
        _httpClient = httpClient ?? HttpClientFactory.Create(configuration.RequestTimeout);
        _ownsHttpClient = httpClient is null;
        _transport = new HttpJsonTransport(_httpClient);
    }

    internal BackendConfiguration Configuration => configuration;

    internal HonuaMcpOpsClient CreateMcpOpsClient()
    {
        Uri endpoint = BuildEndpoint(configuration.HonuaApiBaseUri, configuration.HonuaMcpPath);
        return new HonuaMcpOpsClient(
            _httpClient,
            endpoint,
            request => ApplyApiKey(request, configuration.HonuaApiKey, ApiKeyTransport.XApiKey));
    }

    internal Task<BackendJsonResult> ProposeOpsFindingAsync(
        string findingId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(findingId))
        {
            throw new ArgumentException("Finding id is required.", nameof(findingId));
        }

        string path = $"{configuration.HonuaOpsFindingsPath.TrimEnd('/')}/{Uri.EscapeDataString(findingId.Trim())}/propose";
        return SendJsonAsync(
            configuration.HonuaApiBaseUri,
            HttpMethod.Post,
            path,
            payload: null,
            configuration.HonuaApiKey,
            ApiKeyTransport.XApiKey,
            cancellationToken);
    }

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
        string operation =
            $"troubleshoot:{NormalizeResourceToken(service, "service")}:{NormalizeResourceToken(environment, "env")}";
        (string Name, string Value)[] queryContext =
        [
            ("service", service),
            ("environment", environment),
            ("incidentSummary", incidentSummary),
            ("suspectedComponent", suspectedComponent),
            ("businessImpact", businessImpact)
        ];
        BackendCallResult[] calls = await Task.WhenAll(
            GetFromHonuaAsync(AddQueryParameters(configuration.HonuaAdminErrorsPath, queryContext), cancellationToken),
            GetFromHonuaAsync(AddQueryParameters(configuration.HonuaMetricsHealthPath, queryContext), cancellationToken),
            GetFromHonuaAsync(AddQueryParameters(configuration.HonuaMetricsPerformancePath, queryContext), cancellationToken));

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
        string operation =
            $"tune:{NormalizeResourceToken(service, "service")}:{NormalizeResourceToken(environment, "env")}";
        (string Name, string Value)[] queryContext =
        [
            ("service", service),
            ("environment", environment),
            ("workloadProfile", workloadProfile),
            ("bottleneck", bottleneck),
            ("targetSlo", targetSlo)
        ];
        BackendCallResult[] calls = await Task.WhenAll(
            GetFromHonuaAsync(AddQueryParameters(configuration.HonuaMetricsPerformancePath, queryContext), cancellationToken),
            GetFromHonuaAsync(AddQueryParameters(configuration.HonuaMetricsDatabasePath, queryContext), cancellationToken),
            GetFromHonuaAsync(AddQueryParameters(configuration.HonuaMetricsCachePath, queryContext), cancellationToken),
            GetFromHonuaAsync(AddQueryParameters(configuration.HonuaMetricsMemoryPath, queryContext), cancellationToken),
            GetFromHonuaAsync(AddQueryParameters(configuration.HonuaQueryCacheStatisticsPath, queryContext), cancellationToken));

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
        BackendCallResult[] calls = await Task.WhenAll(
            GetFromHonuaAsync(configuration.HonuaAdminVersionPath, cancellationToken),
            GetFromHonuaAsync(configuration.HonuaAdminCapabilitiesPath, cancellationToken),
            GetFromHonuaAsync(configuration.HonuaReadinessPath, cancellationToken));

        return CombineResults(operation, calls);
    }

    internal async Task<GitOpsDeployBackendResult> RequestGitOpsDeployAsync(
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
        ExecutionMode executionMode,
        ExecutionTier executionTier,
        string[] allowedEnvironments,
        string? deployTargetId,
        CancellationToken cancellationToken)
    {
        BackendCallResult? deployPreflightResult = null;
        BackendCallResult? deployPlanResult = null;
        BackendCallResult? deployOperationResult = null;
        if (!string.IsNullOrWhiteSpace(deployTargetId))
        {
            deployPreflightResult = await RequestDeployPreflightAsync(
                includeDiagnostics: true,
                cancellationToken);
            deployPlanResult = await PlanDeployOperationAsync(
                deployTargetId,
                revision,
                currentRevision: null,
                new Dictionary<string, string>
                {
                    ["service"] = service,
                    ["environments"] = string.Join(",", environments),
                    ["action"] = action,
                    ["gitOpsTool"] = gitOpsTool,
                    ["dryRun"] = dryRun ? "true" : "false"
                },
                cancellationToken);
        }

        ManifestApplyRequest requestBody = DesiredStateManifestBuilder.BuildGitOpsDeployRequest(
            service,
            environments,
            revision,
            action,
            changeSummary,
            gitOpsTool,
            terraformRepository,
            terraformRef,
            deploymentTargets,
            dryRun,
            executionMode,
            executionTier,
            allowedEnvironments);

        using BackendJsonResult manifestSnapshot = await GetJsonFromHonuaAsync(
            configuration.HonuaManifestExportPath,
            cancellationToken);
        using BackendJsonResult capabilitiesSnapshot = await GetJsonFromHonuaAsync(
            configuration.HonuaAdminCapabilitiesPath,
            cancellationToken);
        // #153: write-enabled requests must enter the durable deploy-control
        // operation/approval spine before any mutating backend route. The manifest
        // route is permitted here only when the server is explicitly asked to dry-run.
        BackendCallResult? applyResult = dryRun
            ? await PostToHonuaAsync(
                configuration.HonuaManifestApplyPath,
                requestBody,
                cancellationToken)
            : null;

        // Deploy-control operation creation/submission is owned by the GitOps actuation
        // executors (GitOpsExecutor / PromotionExecutor), which create the operation with
        // submitImmediately=false and only submit when EXECUTION_MODE=execute AND the approval
        // gate is satisfied. This combined backend path no longer creates an operation here,
        // so it never issues the old unguarded submitImmediately=true create.

        List<BackendCallResult> combinedCalls =
        [
            manifestSnapshot.CallResult,
            capabilitiesSnapshot.CallResult
        ];
        if (applyResult is not null)
        {
            combinedCalls.Add(applyResult);
        }
        if (deployPreflightResult is not null)
        {
            combinedCalls.Add(deployPreflightResult);
        }

        if (deployPlanResult is not null)
        {
            combinedCalls.Add(deployPlanResult);
        }

        if (deployOperationResult is not null)
        {
            combinedCalls.Add(deployOperationResult);
        }

        BackendCallResult combinedResult = CombineResults(
            "gitops-deploy",
            combinedCalls);
        return new GitOpsDeployBackendResult(
            ApplyResult: applyResult,
            ExportResult: manifestSnapshot.CallResult,
            CapabilitiesResult: capabilitiesSnapshot.CallResult,
            CombinedResult: combinedResult,
            ExportPayload: manifestSnapshot.Payload is null
                ? null
                : JsonDocument.Parse(manifestSnapshot.Payload.RootElement.GetRawText()),
            CapabilitiesPayload: capabilitiesSnapshot.Payload is null
                ? null
                : JsonDocument.Parse(capabilitiesSnapshot.Payload.RootElement.GetRawText()),
            DeployPreflightResult: deployPreflightResult,
            DeployPlanResult: deployPlanResult,
            DeployOperationResult: deployOperationResult);
    }

    internal async Task<GitOpsDeployBackendResult> PlanGitOpsRunAsync(CancellationToken cancellationToken)
    {
        using BackendJsonResult manifestSnapshot = await GetJsonFromHonuaAsync(
            configuration.HonuaManifestExportPath,
            cancellationToken);
        using BackendJsonResult capabilitiesSnapshot = await GetJsonFromHonuaAsync(
            configuration.HonuaAdminCapabilitiesPath,
            cancellationToken);

        BackendCallResult skippedApplyResult = new(
            IsSuccess: true,
            Endpoint: BuildEndpoint(configuration.HonuaApiBaseUri, configuration.HonuaManifestApplyPath).ToString(),
            Detail: "plan-only: manifest apply skipped",
            PayloadPreview: "apply not requested");
        BackendCallResult combinedResult = CombineResults(
            "gitops-engine-plan",
            [manifestSnapshot.CallResult, capabilitiesSnapshot.CallResult]);

        return new GitOpsDeployBackendResult(
            ApplyResult: skippedApplyResult,
            ExportResult: manifestSnapshot.CallResult,
            CapabilitiesResult: capabilitiesSnapshot.CallResult,
            CombinedResult: combinedResult,
            ExportPayload: manifestSnapshot.Payload is null
                ? null
                : JsonDocument.Parse(manifestSnapshot.Payload.RootElement.GetRawText()),
            CapabilitiesPayload: capabilitiesSnapshot.Payload is null
                ? null
                : JsonDocument.Parse(capabilitiesSnapshot.Payload.RootElement.GetRawText()));
    }

    internal Task<BackendCallResult> ApplyManifestAsync(
        ManifestApplyRequest requestBody,
        CancellationToken cancellationToken)
    {
        return PostToHonuaAsync(
            configuration.HonuaManifestApplyPath,
            requestBody,
            cancellationToken);
    }

    internal Task<BackendJsonResult> ExportManifestSnapshotAsync(CancellationToken cancellationToken)
    {
        return GetJsonFromHonuaAsync(configuration.HonuaManifestExportPath, cancellationToken);
    }

    internal Task<BackendJsonResult> GetCapabilitySnapshotAsync(CancellationToken cancellationToken)
    {
        return GetJsonFromHonuaAsync(configuration.HonuaAdminCapabilitiesPath, cancellationToken);
    }

    internal Task<BackendCallResult> RequestDeployPreflightAsync(bool includeDiagnostics, CancellationToken cancellationToken)
    {
        string path = includeDiagnostics
            ? AddQueryParameters(configuration.HonuaDeployPreflightPath, ("includeDiagnostics", "true"))
            : configuration.HonuaDeployPreflightPath;

        return GetFromHonuaAsync(path, cancellationToken);
    }

    internal Task<BackendCallResult> PlanDeployOperationAsync(
        string targetId,
        string desiredRevision,
        string? currentRevision,
        IReadOnlyDictionary<string, string>? parameters,
        CancellationToken cancellationToken)
    {
        return PostToHonuaAsync(
            configuration.HonuaDeployPlanPath,
            new
            {
                targetId,
                desiredRevision,
                currentRevision,
                parameters
            },
            cancellationToken);
    }

    internal Task<BackendCallResult> CreateDeployOperationAsync(
        string targetId,
        string desiredRevision,
        string? currentRevision,
        string reason,
        bool submitImmediately,
        string idempotencyKey,
        string correlationId,
        string priority,
        IReadOnlyDictionary<string, string>? parameters,
        CancellationToken cancellationToken)
    {
        return PostToHonuaAsync(
            configuration.HonuaDeployOperationsPath,
            new
            {
                targetId,
                desiredRevision,
                currentRevision,
                reason,
                idempotencyKey,
                correlationId,
                priority,
                submitImmediately,
                parameters
            },
            cancellationToken);
    }

    internal Task<BackendCallResult> GetDeployOperationAsync(string operationId, CancellationToken cancellationToken)
    {
        return GetFromHonuaAsync(
            $"{configuration.HonuaDeployOperationsPath}/{Uri.EscapeDataString(operationId)}",
            cancellationToken);
    }

    // JSON-returning variants used by the Console bridge: it must read the durable server
    // operationId and workflow status fields, not just a truncated payload preview.
    internal Task<BackendJsonResult> GetDeployOperationJsonAsync(string operationId, CancellationToken cancellationToken)
    {
        return GetJsonFromHonuaAsync(
            $"{configuration.HonuaDeployOperationsPath}/{Uri.EscapeDataString(operationId)}",
            cancellationToken);
    }

    internal Task<BackendJsonResult> CreateDeployOperationJsonAsync(
        string targetId,
        string desiredRevision,
        string? currentRevision,
        string reason,
        bool submitImmediately,
        string idempotencyKey,
        string correlationId,
        string priority,
        IReadOnlyDictionary<string, string>? parameters,
        CancellationToken cancellationToken)
    {
        return PostJsonToHonuaAsync(
            configuration.HonuaDeployOperationsPath,
            new
            {
                targetId,
                desiredRevision,
                currentRevision,
                reason,
                idempotencyKey,
                correlationId,
                priority,
                submitImmediately,
                parameters
            },
            cancellationToken);
    }

    internal Task<BackendCallResult> SubmitDeployOperationAsync(
        string operationId,
        string reason,
        CancellationToken cancellationToken)
    {
        return PostToHonuaAsync(
            $"{configuration.HonuaDeployOperationsPath}/{Uri.EscapeDataString(operationId)}/submit",
            new { reason },
            cancellationToken);
    }

    // JSON-returning submit variant used by the GitOps executors: after a submit the
    // orchestrator must read the post-submit workflow status (and any blockingReasons)
    // from the durable server operation, not just a truncated payload preview.
    internal Task<BackendJsonResult> SubmitDeployOperationJsonAsync(
        string operationId,
        string reason,
        CancellationToken cancellationToken)
    {
        return PostJsonToHonuaAsync(
            $"{configuration.HonuaDeployOperationsPath}/{Uri.EscapeDataString(operationId)}/submit",
            new { reason },
            cancellationToken);
    }

    // JSON-returning rollback variant: the executor needs the server's resulting status
    // and, when the OperatorApprovalGate denies a data-affecting rollback, the structured
    // 403 body so it can surface the approval requirement instead of inventing one.
    internal Task<BackendJsonResult> RollbackDeployOperationJsonAsync(
        string operationId,
        string reason,
        CancellationToken cancellationToken)
    {
        return PostJsonToHonuaAsync(
            $"{configuration.HonuaDeployOperationsPath}/{Uri.EscapeDataString(operationId)}/rollback",
            new { reason },
            cancellationToken);
    }

    // ---- Additive metadata-release layer-evolution lifecycle (Demo B safe-rollback) ----
    // The server's metadata-release lifecycle (honua-server #1738/#1739) health-gates a layer
    // change on a post-publish Smoke check and, on failure, rolls back metadata AND the
    // operational schema (reactivate prior revision + run the down-script). These methods let the
    // DevOps AI loop submit the change and then DETECT the rolled-back state + smoke evidence so it
    // can diagnose. The metadata-release operation is a WorkflowOperationKind.MetadataRelease
    // record, so its id resolves through the shared deploy operations read/rollback endpoints.

    internal Task<BackendJsonResult> CreateMetadataReleaseOperationJsonAsync(
        string packageId,
        string targetEnvironment,
        string resourceSemanticId,
        string newFieldName,
        string? newFieldType,
        string? dataPopulateWorkloadId,
        string reason,
        string idempotencyKey,
        string correlationId,
        CancellationToken cancellationToken)
    {
        return PostJsonToHonuaAsync(
            configuration.HonuaMetadataReleaseOperationsPath,
            new
            {
                packageId,
                targetEnvironment,
                resourceSemanticId,
                newFieldName,
                newFieldType,
                dataPopulateWorkloadId,
                reason,
                idempotencyKey,
                correlationId
            },
            cancellationToken);
    }

    // Detect seam: read the most recent metadata-release operation for a package. The server
    // reconciles in-flight operations on read, so a poll here surfaces the terminal status
    // (RolledBack / Succeeded / ManualInterventionRequired), the failing CurrentPhase/errorMessage
    // (the injected smoke failure), the rollback plan class, and the smoke evidence ref.
    internal Task<BackendJsonResult> GetMetadataReleaseOperationByPackageJsonAsync(
        string packageId,
        CancellationToken cancellationToken)
    {
        return GetJsonFromHonuaAsync(
            $"{configuration.HonuaMetadataReleaseByPackagePath}/{Uri.EscapeDataString(packageId)}/operation",
            cancellationToken);
    }

    internal Task<BackendCallResult> RequestManifestDriftAsync(bool verbose, CancellationToken cancellationToken)
    {
        string path = verbose
            ? AddQueryParameters(configuration.HonuaManifestDriftPath, ("verbose", "true"))
            : configuration.HonuaManifestDriftPath;

        return GetFromHonuaAsync(path, cancellationToken);
    }

    internal Task<BackendCallResult> RequestManifestVersionsAsync(int limit, CancellationToken cancellationToken)
    {
        int clampedLimit = Math.Clamp(limit, 1, 100);
        return GetFromHonuaAsync(
            AddQueryParameters(configuration.HonuaManifestVersionsPath, ("limit", clampedLimit.ToString())),
            cancellationToken);
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
        BackendCallResult[] calls = await Task.WhenAll(
            GetFromHonuaAsync(configuration.HonuaAdminCapabilitiesPath, cancellationToken),
            GetFromHonuaAsync(configuration.HonuaManifestExportPath, cancellationToken),
            GetFromHonuaAsync(configuration.HonuaMetricsPerformancePath, cancellationToken));

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
        BackendCallResult[] calls = await Task.WhenAll(
            GetFromHonuaAsync(configuration.HonuaAdminTelemetryPath, cancellationToken),
            GetFromHonuaAsync(configuration.HonuaMetricsPerformancePath, cancellationToken),
            GetFromHonuaAsync(configuration.HonuaMetricsCachePath, cancellationToken));

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

    internal static string? ExtractEditionFromCapabilities(JsonDocument capabilities)
    {
        if (capabilities is null)
        {
            return null;
        }

        return FindEditionToken(capabilities.RootElement);
    }

    private static string? FindEditionToken(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    if (string.Equals(property.Name, "edition", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(property.Name, "licenseEdition", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(property.Name, "licenseTier", StringComparison.OrdinalIgnoreCase))
                    {
                        if (property.Value.ValueKind == JsonValueKind.String)
                        {
                            string? value = property.Value.GetString();
                            if (!string.IsNullOrWhiteSpace(value))
                            {
                                return value.Trim();
                            }
                        }
                    }

                    string? nested = FindEditionToken(property.Value);
                    if (nested is not null)
                    {
                        return nested;
                    }
                }
                return null;

            case JsonValueKind.Array:
                foreach (JsonElement item in element.EnumerateArray())
                {
                    string? nested = FindEditionToken(item);
                    if (nested is not null)
                    {
                        return nested;
                    }
                }
                return null;

            default:
                return null;
        }
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

    private Task<BackendJsonResult> GetJsonFromHonuaAsync(string relativePath, CancellationToken cancellationToken)
    {
        return SendJsonAsync(
            configuration.HonuaApiBaseUri,
            HttpMethod.Get,
            relativePath,
            payload: null,
            configuration.HonuaApiKey,
            ApiKeyTransport.XApiKey,
            cancellationToken);
    }

    private Task<BackendJsonResult> PostJsonToHonuaAsync(string relativePath, object payload, CancellationToken cancellationToken)
    {
        return SendJsonAsync(
            configuration.HonuaApiBaseUri,
            HttpMethod.Post,
            relativePath,
            payload,
            configuration.HonuaApiKey,
            ApiKeyTransport.XApiKey,
            cancellationToken);
    }

    private Task<BackendCallResult> SendAsync(
        Uri baseUri,
        HttpMethod method,
        string relativePath,
        object? payload,
        string? apiKey,
        ApiKeyTransport apiKeyTransport,
        CancellationToken cancellationToken)
    {
        Uri endpoint = BuildEndpoint(baseUri, relativePath);
        return _transport.SendAsync(
            method,
            endpoint,
            payload,
            request => ApplyApiKey(request, apiKey, apiKeyTransport),
            cancellationToken);
    }

    private Task<BackendJsonResult> SendJsonAsync(
        Uri baseUri,
        HttpMethod method,
        string relativePath,
        object? payload,
        string? apiKey,
        ApiKeyTransport apiKeyTransport,
        CancellationToken cancellationToken)
    {
        Uri endpoint = BuildEndpoint(baseUri, relativePath);
        return _transport.SendJsonAsync(
            method,
            endpoint,
            payload,
            request => ApplyApiKey(request, apiKey, apiKeyTransport),
            cancellationToken);
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

    internal static BackendCallResult CombineResults(string operation, IReadOnlyList<BackendCallResult> calls)
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
            IsSuccess: successCount == calls.Count,
            Endpoint: endpoints,
            Detail: detail,
            PayloadPreview: HttpJsonTransport.SummarizeBody(preview));
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

    private static string AddQueryParameters(string relativePath, params (string Name, string Value)[] parameters)
    {
        StringBuilder builder = new(relativePath);
        string separator = relativePath.Contains('?', StringComparison.Ordinal) ? "&" : "?";
        foreach ((string name, string value) in parameters)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            builder
                .Append(separator)
                .Append(Uri.EscapeDataString(name))
                .Append('=')
                .Append(Uri.EscapeDataString(value.Trim()));
            separator = "&";
        }

        return builder.ToString();
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
        Uri endpoint = new(normalizedBaseUri, cleanedPath);
        bool baseAuthorityMatchesEndpoint =
            endpoint.Scheme.Equals(normalizedBaseUri.Scheme, StringComparison.OrdinalIgnoreCase) &&
            endpoint.Host.Equals(normalizedBaseUri.Host, StringComparison.OrdinalIgnoreCase) &&
            endpoint.Port == normalizedBaseUri.Port;
        if (!baseAuthorityMatchesEndpoint)
        {
            throw new InvalidOperationException(
                "Relative backend path resolved outside the configured base host.");
        }

        return endpoint;
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
