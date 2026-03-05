namespace Honua.DevOps.Agent.Operations;

internal sealed record BackendConfiguration(
    Uri HonuaApiBaseUri,
    Uri OTelBaseUri,
    string? HonuaApiKey,
    string? OTelApiKey,
    string HonuaHealthPath,
    string OTelHealthPath,
    string OTelLogsPath,
    string OTelMetricsPath,
    string HonuaTroubleshootPath,
    string HonuaTunePath,
    string HonuaUpgradePath,
    string HonuaDeployPath,
    string HonuaRequirementsPath,
    string HonuaTopologyPath,
    TimeSpan RequestTimeout)
{
    private const string HonuaApiBaseUrlVariable = "HONUA_DEVOPS_HONUA_API_BASE_URL";
    private const string OTelBaseUrlVariable = "HONUA_DEVOPS_OTEL_BASE_URL";

    private const string HonuaApiKeyVariable = "HONUA_DEVOPS_HONUA_API_KEY";
    private const string OTelApiKeyVariable = "HONUA_DEVOPS_OTEL_API_KEY";

    private const string HonuaHealthPathVariable = "HONUA_DEVOPS_HONUA_HEALTH_PATH";
    private const string OTelHealthPathVariable = "HONUA_DEVOPS_OTEL_HEALTH_PATH";

    private const string OTelLogsPathVariable = "HONUA_DEVOPS_OTEL_LOGS_PATH";
    private const string OTelMetricsPathVariable = "HONUA_DEVOPS_OTEL_METRICS_PATH";

    private const string HonuaTroubleshootPathVariable = "HONUA_DEVOPS_HONUA_TROUBLESHOOT_PATH";
    private const string HonuaTunePathVariable = "HONUA_DEVOPS_HONUA_TUNE_PATH";
    private const string HonuaUpgradePathVariable = "HONUA_DEVOPS_HONUA_UPGRADE_PATH";
    private const string HonuaDeployPathVariable = "HONUA_DEVOPS_HONUA_DEPLOY_PATH";
    private const string HonuaRequirementsPathVariable = "HONUA_DEVOPS_HONUA_REQUIREMENTS_PATH";
    private const string HonuaTopologyPathVariable = "HONUA_DEVOPS_HONUA_TOPOLOGY_PATH";

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

        string honuaHealthPath = Normalize(Environment.GetEnvironmentVariable(HonuaHealthPathVariable), "/health");
        string otelHealthPath = Normalize(Environment.GetEnvironmentVariable(OTelHealthPathVariable), "/");

        string otelLogsPath = Normalize(Environment.GetEnvironmentVariable(OTelLogsPathVariable), "/v1/logs/search");
        string otelMetricsPath = Normalize(Environment.GetEnvironmentVariable(OTelMetricsPathVariable), "/v1/metrics/search");

        string honuaTroubleshootPath = Normalize(Environment.GetEnvironmentVariable(HonuaTroubleshootPathVariable), "/ops/troubleshoot");
        string honuaTunePath = Normalize(Environment.GetEnvironmentVariable(HonuaTunePathVariable), "/ops/performance/tune");
        string honuaUpgradePath = Normalize(Environment.GetEnvironmentVariable(HonuaUpgradePathVariable), "/ops/upgrades/plan");
        string honuaDeployPath = Normalize(Environment.GetEnvironmentVariable(HonuaDeployPathVariable), "/ops/deployments/gitops");
        string honuaRequirementsPath = Normalize(Environment.GetEnvironmentVariable(HonuaRequirementsPathVariable), "/ops/requirements/analyze");
        string honuaTopologyPath = Normalize(Environment.GetEnvironmentVariable(HonuaTopologyPathVariable), "/ops/topology/recommend");

        TimeSpan timeout = ParseTimeout(Environment.GetEnvironmentVariable(TimeoutSecondsVariable));

        return new BackendConfiguration(
            HonuaApiBaseUri: honuaApiBaseUri,
            OTelBaseUri: otelBaseUri,
            HonuaApiKey: honuaApiKey,
            OTelApiKey: otelApiKey,
            HonuaHealthPath: honuaHealthPath,
            OTelHealthPath: otelHealthPath,
            OTelLogsPath: otelLogsPath,
            OTelMetricsPath: otelMetricsPath,
            HonuaTroubleshootPath: honuaTroubleshootPath,
            HonuaTunePath: honuaTunePath,
            HonuaUpgradePath: honuaUpgradePath,
            HonuaDeployPath: honuaDeployPath,
            HonuaRequirementsPath: honuaRequirementsPath,
            HonuaTopologyPath: honuaTopologyPath,
            RequestTimeout: timeout);
    }

    private static Uri ParseBaseUri(string? value, string fallback, string variableName)
    {
        string candidate = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out Uri? uri))
        {
            throw new InvalidOperationException(
                $"Environment variable `{variableName}` must be a valid absolute URL.");
        }

        return uri;
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
}
