using System.Net;

namespace Honua.DevOps.Agent.Operations;

internal sealed record BackendConfiguration(
    Uri HonuaApiBaseUri,
    Uri OTelBaseUri,
    string? HonuaApiKey,
    string? OTelApiKey,
    string HonuaReadinessPath,
    string OTelHealthPath,
    string OTelLogsPath,
    string OTelMetricsPath,
    string HonuaAdminErrorsPath,
    string HonuaAdminTelemetryPath,
    string HonuaMetricsHealthPath,
    string HonuaMetricsPerformancePath,
    string HonuaMetricsDatabasePath,
    string HonuaMetricsCachePath,
    string HonuaMetricsMemoryPath,
    string HonuaQueryCacheStatisticsPath,
    string HonuaAdminVersionPath,
    string HonuaAdminCapabilitiesPath,
    string HonuaManifestExportPath,
    string HonuaManifestApplyPath,
    TimeSpan RequestTimeout,
    Uri? SupportApiBaseUri = null,
    string SupportApiTicketsPath = "api/v1/tickets")
{
    private const string HonuaApiBaseUrlVariable = "HONUA_DEVOPS_HONUA_API_BASE_URL";
    private const string OTelBaseUrlVariable = "HONUA_DEVOPS_OTEL_BASE_URL";

    private const string HonuaApiKeyVariable = "HONUA_DEVOPS_HONUA_API_KEY";
    private const string OTelApiKeyVariable = "HONUA_DEVOPS_OTEL_API_KEY";

    private const string HonuaReadinessPathVariable = "HONUA_DEVOPS_HONUA_READINESS_PATH";
    private const string LegacyHonuaHealthPathVariable = "HONUA_DEVOPS_HONUA_HEALTH_PATH";
    private const string OTelHealthPathVariable = "HONUA_DEVOPS_OTEL_HEALTH_PATH";

    private const string OTelLogsPathVariable = "HONUA_DEVOPS_OTEL_LOGS_PATH";
    private const string OTelMetricsPathVariable = "HONUA_DEVOPS_OTEL_METRICS_PATH";

    private const string HonuaAdminErrorsPathVariable = "HONUA_DEVOPS_HONUA_ADMIN_ERRORS_PATH";
    private const string HonuaAdminTelemetryPathVariable = "HONUA_DEVOPS_HONUA_ADMIN_TELEMETRY_PATH";
    private const string HonuaMetricsHealthPathVariable = "HONUA_DEVOPS_HONUA_METRICS_HEALTH_PATH";
    private const string HonuaMetricsPerformancePathVariable = "HONUA_DEVOPS_HONUA_METRICS_PERFORMANCE_PATH";
    private const string HonuaMetricsDatabasePathVariable = "HONUA_DEVOPS_HONUA_METRICS_DATABASE_PATH";
    private const string HonuaMetricsCachePathVariable = "HONUA_DEVOPS_HONUA_METRICS_CACHE_PATH";
    private const string HonuaMetricsMemoryPathVariable = "HONUA_DEVOPS_HONUA_METRICS_MEMORY_PATH";
    private const string HonuaQueryCacheStatisticsPathVariable = "HONUA_DEVOPS_HONUA_QUERY_CACHE_STATS_PATH";
    private const string HonuaAdminVersionPathVariable = "HONUA_DEVOPS_HONUA_ADMIN_VERSION_PATH";
    private const string HonuaAdminCapabilitiesPathVariable = "HONUA_DEVOPS_HONUA_ADMIN_CAPABILITIES_PATH";
    private const string HonuaManifestExportPathVariable = "HONUA_DEVOPS_HONUA_MANIFEST_EXPORT_PATH";
    private const string HonuaManifestApplyPathVariable = "HONUA_DEVOPS_HONUA_MANIFEST_APPLY_PATH";

    // Backwards-compatible aliases from the original /ops route placeholders.
    private const string LegacyTroubleshootPathVariable = "HONUA_DEVOPS_HONUA_TROUBLESHOOT_PATH";
    private const string LegacyTunePathVariable = "HONUA_DEVOPS_HONUA_TUNE_PATH";
    private const string LegacyUpgradePathVariable = "HONUA_DEVOPS_HONUA_UPGRADE_PATH";
    private const string LegacyDeployPathVariable = "HONUA_DEVOPS_HONUA_DEPLOY_PATH";
    private const string LegacyRequirementsPathVariable = "HONUA_DEVOPS_HONUA_REQUIREMENTS_PATH";
    private const string LegacyTopologyPathVariable = "HONUA_DEVOPS_HONUA_TOPOLOGY_PATH";

    private const string SupportApiBaseUrlVariable = "HONUA_DEVOPS_SUPPORT_API_BASE_URL";
    private const string SupportApiTicketsPathVariable = "HONUA_DEVOPS_SUPPORT_API_TICKETS_PATH";

    private const string TimeoutSecondsVariable = "HONUA_DEVOPS_BACKEND_TIMEOUT_SECONDS";

    internal static BackendConfiguration Load()
    {
        Uri honuaApiBaseUri = ParseBaseUri(
            Environment.GetEnvironmentVariable(HonuaApiBaseUrlVariable),
            "http://localhost:8080",
            HonuaApiBaseUrlVariable);

        Uri otelBaseUri = ParseBaseUri(
            Environment.GetEnvironmentVariable(OTelBaseUrlVariable),
            "http://localhost:4318",
            OTelBaseUrlVariable);

        string? honuaApiKey = Normalize(Environment.GetEnvironmentVariable(HonuaApiKeyVariable));
        string? otelApiKey = Normalize(Environment.GetEnvironmentVariable(OTelApiKeyVariable));

        string honuaReadinessPath = ParseRelativePathWithLegacy(
            HonuaReadinessPathVariable,
            LegacyHonuaHealthPathVariable,
            "/healthz/ready");
        string otelHealthPath = ParseRelativePath(
            Environment.GetEnvironmentVariable(OTelHealthPathVariable),
            "/",
            OTelHealthPathVariable);

        string otelLogsPath = ParseRelativePath(
            Environment.GetEnvironmentVariable(OTelLogsPathVariable),
            "/v1/logs/search",
            OTelLogsPathVariable);
        string otelMetricsPath = ParseRelativePath(
            Environment.GetEnvironmentVariable(OTelMetricsPathVariable),
            "/v1/metrics/search",
            OTelMetricsPathVariable);

        string honuaAdminErrorsPath = ParseRelativePathWithLegacy(
            HonuaAdminErrorsPathVariable,
            LegacyTroubleshootPathVariable,
            "/api/v1/admin/observability/errors");
        string honuaAdminTelemetryPath = ParseRelativePathWithLegacy(
            HonuaAdminTelemetryPathVariable,
            LegacyTopologyPathVariable,
            "/api/v1/admin/observability/telemetry");
        string honuaMetricsHealthPath = ParseRelativePath(
            Environment.GetEnvironmentVariable(HonuaMetricsHealthPathVariable),
            "/api/v1/metrics/health",
            HonuaMetricsHealthPathVariable);
        string honuaMetricsPerformancePath = ParseRelativePathWithLegacy(
            HonuaMetricsPerformancePathVariable,
            LegacyTunePathVariable,
            "/api/v1/metrics/performance");
        string honuaMetricsDatabasePath = ParseRelativePath(
            Environment.GetEnvironmentVariable(HonuaMetricsDatabasePathVariable),
            "/api/v1/metrics/database",
            HonuaMetricsDatabasePathVariable);
        string honuaMetricsCachePath = ParseRelativePath(
            Environment.GetEnvironmentVariable(HonuaMetricsCachePathVariable),
            "/api/v1/metrics/cache",
            HonuaMetricsCachePathVariable);
        string honuaMetricsMemoryPath = ParseRelativePath(
            Environment.GetEnvironmentVariable(HonuaMetricsMemoryPathVariable),
            "/api/v1/metrics/memory",
            HonuaMetricsMemoryPathVariable);
        string honuaQueryCacheStatisticsPath = ParseRelativePath(
            Environment.GetEnvironmentVariable(HonuaQueryCacheStatisticsPathVariable),
            "/api/v1/admin/performance/database/query-cache/statistics",
            HonuaQueryCacheStatisticsPathVariable);
        string honuaAdminVersionPath = ParseRelativePathWithLegacy(
            HonuaAdminVersionPathVariable,
            LegacyUpgradePathVariable,
            "/api/v1/admin/version");
        string honuaAdminCapabilitiesPath = ParseRelativePathWithLegacy(
            HonuaAdminCapabilitiesPathVariable,
            LegacyRequirementsPathVariable,
            "/api/v1/admin/capabilities");
        string honuaManifestExportPath = ParseRelativePath(
            Environment.GetEnvironmentVariable(HonuaManifestExportPathVariable),
            "/api/v1/admin/manifest",
            HonuaManifestExportPathVariable);
        string honuaManifestApplyPath = ParseRelativePathWithLegacy(
            HonuaManifestApplyPathVariable,
            LegacyDeployPathVariable,
            "/api/v1/admin/manifest/apply");

        TimeSpan timeout = ParseTimeout(Environment.GetEnvironmentVariable(TimeoutSecondsVariable));

        Uri? supportApiBaseUri = ParseOptionalBaseUri(
            Environment.GetEnvironmentVariable(SupportApiBaseUrlVariable),
            SupportApiBaseUrlVariable);
        string supportApiTicketsPath = ParseRelativePath(
            Environment.GetEnvironmentVariable(SupportApiTicketsPathVariable),
            "api/v1/tickets",
            SupportApiTicketsPathVariable);

        return new BackendConfiguration(
            HonuaApiBaseUri: honuaApiBaseUri,
            OTelBaseUri: otelBaseUri,
            HonuaApiKey: honuaApiKey,
            OTelApiKey: otelApiKey,
            HonuaReadinessPath: honuaReadinessPath,
            OTelHealthPath: otelHealthPath,
            OTelLogsPath: otelLogsPath,
            OTelMetricsPath: otelMetricsPath,
            HonuaAdminErrorsPath: honuaAdminErrorsPath,
            HonuaAdminTelemetryPath: honuaAdminTelemetryPath,
            HonuaMetricsHealthPath: honuaMetricsHealthPath,
            HonuaMetricsPerformancePath: honuaMetricsPerformancePath,
            HonuaMetricsDatabasePath: honuaMetricsDatabasePath,
            HonuaMetricsCachePath: honuaMetricsCachePath,
            HonuaMetricsMemoryPath: honuaMetricsMemoryPath,
            HonuaQueryCacheStatisticsPath: honuaQueryCacheStatisticsPath,
            HonuaAdminVersionPath: honuaAdminVersionPath,
            HonuaAdminCapabilitiesPath: honuaAdminCapabilitiesPath,
            HonuaManifestExportPath: honuaManifestExportPath,
            HonuaManifestApplyPath: honuaManifestApplyPath,
            RequestTimeout: timeout,
            SupportApiBaseUri: supportApiBaseUri,
            SupportApiTicketsPath: supportApiTicketsPath);
    }

    private static string ParseRelativePathWithLegacy(string variableName, string legacyVariableName, string fallback)
    {
        string? primaryValue = Environment.GetEnvironmentVariable(variableName);
        if (!string.IsNullOrWhiteSpace(primaryValue))
        {
            return ParseRelativePath(primaryValue, fallback, variableName);
        }

        string? legacyValue = Environment.GetEnvironmentVariable(legacyVariableName);
        if (!string.IsNullOrWhiteSpace(legacyValue))
        {
            return ParseRelativePath(legacyValue, fallback, legacyVariableName);
        }

        return ParseRelativePath(null, fallback, variableName);
    }

    private static Uri ParseBaseUri(string? value, string fallback, string variableName)
    {
        string candidate = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out Uri? uri))
        {
            throw new InvalidOperationException(
                $"Environment variable `{variableName}` must be a valid absolute URL.");
        }

        if (!uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Environment variable `{variableName}` must use http or https.");
        }

        if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new InvalidOperationException(
                $"Environment variable `{variableName}` must not include query string or fragment.");
        }

        if (!IsLocalUri(uri) && !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Environment variable `{variableName}` must use https for non-local endpoints.");
        }

        return uri;
    }

    private static string ParseRelativePath(string? value, string fallback, string variableName)
    {
        string trimmed = Normalize(value, fallback).Trim();
        if (trimmed.Length == 0)
        {
            throw new InvalidOperationException(
                $"Environment variable `{variableName}` must not be empty.");
        }

        if (trimmed.Equals("/", StringComparison.Ordinal))
        {
            return string.Empty;
        }

        if (trimmed.StartsWith("//", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Environment variable `{variableName}` must not start with `//`.");
        }

        if (trimmed.Contains('\\'))
        {
            throw new InvalidOperationException(
                $"Environment variable `{variableName}` must use `/` path separators.");
        }

        if (!trimmed.StartsWith("/", StringComparison.Ordinal) &&
            Uri.TryCreate(trimmed, UriKind.Absolute, out _))
        {
            throw new InvalidOperationException(
                $"Environment variable `{variableName}` must be a relative path and must not include scheme/host.");
        }

        string normalized = trimmed.StartsWith('/') ? trimmed[1..] : trimmed;
        if (LooksLikeAbsoluteUriPathOverride(normalized))
        {
            throw new InvalidOperationException(
                $"Environment variable `{variableName}` must be a relative path and must not include scheme/host.");
        }

        string[] segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => segment is "." or ".."))
        {
            throw new InvalidOperationException(
                $"Environment variable `{variableName}` must not include `.` or `..` path segments.");
        }

        return normalized;
    }

    private static TimeSpan ParseTimeout(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return TimeSpan.FromSeconds(20);
        }

        if (!int.TryParse(value, out int seconds) || seconds <= 0)
        {
            throw new InvalidOperationException(
                $"Environment variable `{TimeoutSecondsVariable}` must be a positive integer.");
        }

        return TimeSpan.FromSeconds(seconds);
    }

    private static string Normalize(string? value, string fallback)
    {
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }

    private static string? Normalize(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static bool LooksLikeAbsoluteUriPathOverride(string value)
    {
        return value.StartsWith("http:", StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith("https:", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("://", StringComparison.Ordinal);
    }

    private static Uri? ParseOptionalBaseUri(string? value, string variableName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return ParseBaseUri(value, value, variableName);
    }

    private static bool IsLocalUri(Uri uri)
    {
        if (uri.IsLoopback)
        {
            return true;
        }

        if (IPAddress.TryParse(uri.Host, out IPAddress? address))
        {
            return IPAddress.IsLoopback(address);
        }

        return uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase);
    }
}
