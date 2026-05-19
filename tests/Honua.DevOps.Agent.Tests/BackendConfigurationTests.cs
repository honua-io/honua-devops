using Honua.DevOps.Agent.Operations;

namespace Honua.DevOps.Agent.Tests;

public class BackendConfigurationTests
{
    [Fact]
    public void Load_RejectsAbsolutePathOverrides()
    {
        using TestEnvironmentVariableScope environment = new();
        ResetBackendVariables(environment);
        environment.Set("HONUA_DEVOPS_OTEL_LOGS_PATH", "https://evil.example/v1/logs/search");

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(BackendConfiguration.Load);

        Assert.Contains("HONUA_DEVOPS_OTEL_LOGS_PATH", exception.Message);
    }

    [Fact]
    public void Load_RejectsSlashPrefixedAbsolutePathOverrides()
    {
        using TestEnvironmentVariableScope environment = new();
        ResetBackendVariables(environment);
        environment.Set("HONUA_DEVOPS_OTEL_LOGS_PATH", "/https://evil.example/v1/logs/search");

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(BackendConfiguration.Load);

        Assert.Contains("HONUA_DEVOPS_OTEL_LOGS_PATH", exception.Message);
    }

    [Fact]
    public void Load_RejectsNonHttpsRemoteBackend()
    {
        using TestEnvironmentVariableScope environment = new();
        ResetBackendVariables(environment);
        environment.Set("HONUA_DEVOPS_HONUA_API_BASE_URL", "http://api.example.com");

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(BackendConfiguration.Load);

        Assert.Contains("HONUA_DEVOPS_HONUA_API_BASE_URL", exception.Message);
        Assert.Contains("https", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Load_AllowsHttpLoopbackBackend()
    {
        using TestEnvironmentVariableScope environment = new();
        ResetBackendVariables(environment);
        environment.Set("HONUA_DEVOPS_HONUA_API_BASE_URL", "http://127.0.0.1:8080");

        BackendConfiguration configuration = BackendConfiguration.Load();

        Assert.Equal(Uri.UriSchemeHttp, configuration.HonuaApiBaseUri.Scheme);
    }

    [Fact]
    public void Load_NormalizesRelativePathsForEndpointJoining()
    {
        using TestEnvironmentVariableScope environment = new();
        ResetBackendVariables(environment);
        environment.Set("HONUA_DEVOPS_HONUA_METRICS_PERFORMANCE_PATH", "/api/v1/metrics/performance");

        BackendConfiguration configuration = BackendConfiguration.Load();

        Assert.Equal("api/v1/metrics/performance", configuration.HonuaMetricsPerformancePath);
    }

    [Fact]
    public void Load_ConfiguresHonuaSupportApiWhenProvided()
    {
        using TestEnvironmentVariableScope environment = new();
        ResetBackendVariables(environment);
        environment.Set("HONUA_DEVOPS_SUPPORT_API_BASE_URL", "http://localhost:5100");
        environment.Set("HONUA_DEVOPS_SUPPORT_API_TICKETS_PATH", "/api/v1/tickets");
        environment.Set("HONUA_DEVOPS_SUPPORT_API_BEARER_TOKEN", "support-operator-token");

        BackendConfiguration configuration = BackendConfiguration.Load();

        Assert.Equal(new Uri("http://localhost:5100"), configuration.SupportApiBaseUri);
        Assert.Equal("api/v1/tickets", configuration.SupportApiTicketsPath);
        Assert.Equal("support-operator-token", configuration.SupportApiBearerToken);
    }

    [Fact]
    public void Load_ConfiguresRealHonuaDeployControlPaths()
    {
        using TestEnvironmentVariableScope environment = new();
        ResetBackendVariables(environment);
        environment.Set("HONUA_DEVOPS_HONUA_DEPLOY_PREFLIGHT_PATH", "/api/v1/admin/deploy/preflight");
        environment.Set("HONUA_DEVOPS_HONUA_DEPLOY_PLAN_PATH", "/api/v1/admin/deploy/plan");
        environment.Set("HONUA_DEVOPS_HONUA_DEPLOY_OPERATIONS_PATH", "/api/v1/admin/deploy/operations");
        environment.Set("HONUA_DEVOPS_HONUA_MANIFEST_DRIFT_PATH", "/api/v1/admin/manifest/drift");
        environment.Set("HONUA_DEVOPS_HONUA_MANIFEST_VERSIONS_PATH", "/api/v1/admin/manifest/versions");

        BackendConfiguration configuration = BackendConfiguration.Load();

        Assert.Equal("api/v1/admin/deploy/preflight", configuration.HonuaDeployPreflightPath);
        Assert.Equal("api/v1/admin/deploy/plan", configuration.HonuaDeployPlanPath);
        Assert.Equal("api/v1/admin/deploy/operations", configuration.HonuaDeployOperationsPath);
        Assert.Equal("api/v1/admin/manifest/drift", configuration.HonuaManifestDriftPath);
        Assert.Equal("api/v1/admin/manifest/versions", configuration.HonuaManifestVersionsPath);
    }

    private static void ResetBackendVariables(TestEnvironmentVariableScope environment)
    {
        string[] variableNames =
        [
            "HONUA_DEVOPS_HONUA_API_BASE_URL",
            "HONUA_DEVOPS_OTEL_BASE_URL",
            "HONUA_DEVOPS_HONUA_API_KEY",
            "HONUA_DEVOPS_OTEL_API_KEY",
            "HONUA_DEVOPS_HONUA_READINESS_PATH",
            "HONUA_DEVOPS_HONUA_HEALTH_PATH",
            "HONUA_DEVOPS_OTEL_HEALTH_PATH",
            "HONUA_DEVOPS_OTEL_LOGS_PATH",
            "HONUA_DEVOPS_OTEL_METRICS_PATH",
            "HONUA_DEVOPS_HONUA_ADMIN_ERRORS_PATH",
            "HONUA_DEVOPS_HONUA_ADMIN_TELEMETRY_PATH",
            "HONUA_DEVOPS_HONUA_METRICS_HEALTH_PATH",
            "HONUA_DEVOPS_HONUA_METRICS_PERFORMANCE_PATH",
            "HONUA_DEVOPS_HONUA_METRICS_DATABASE_PATH",
            "HONUA_DEVOPS_HONUA_METRICS_CACHE_PATH",
            "HONUA_DEVOPS_HONUA_METRICS_MEMORY_PATH",
            "HONUA_DEVOPS_HONUA_QUERY_CACHE_STATS_PATH",
            "HONUA_DEVOPS_HONUA_ADMIN_VERSION_PATH",
            "HONUA_DEVOPS_HONUA_ADMIN_CAPABILITIES_PATH",
            "HONUA_DEVOPS_HONUA_MANIFEST_EXPORT_PATH",
            "HONUA_DEVOPS_HONUA_MANIFEST_APPLY_PATH",
            "HONUA_DEVOPS_HONUA_DEPLOY_PREFLIGHT_PATH",
            "HONUA_DEVOPS_HONUA_DEPLOY_PLAN_PATH",
            "HONUA_DEVOPS_HONUA_DEPLOY_OPERATIONS_PATH",
            "HONUA_DEVOPS_HONUA_MANIFEST_DRIFT_PATH",
            "HONUA_DEVOPS_HONUA_MANIFEST_VERSIONS_PATH",
            "HONUA_DEVOPS_HONUA_TROUBLESHOOT_PATH",
            "HONUA_DEVOPS_HONUA_TUNE_PATH",
            "HONUA_DEVOPS_HONUA_UPGRADE_PATH",
            "HONUA_DEVOPS_HONUA_DEPLOY_PATH",
            "HONUA_DEVOPS_HONUA_REQUIREMENTS_PATH",
            "HONUA_DEVOPS_HONUA_TOPOLOGY_PATH",
            "HONUA_DEVOPS_SUPPORT_API_BASE_URL",
            "HONUA_DEVOPS_SUPPORT_API_TICKETS_PATH",
            "HONUA_DEVOPS_SUPPORT_API_BEARER_TOKEN",
            "HONUA_DEVOPS_BACKEND_TIMEOUT_SECONDS"
        ];

        foreach (string variableName in variableNames)
        {
            environment.Set(variableName, null);
        }

        environment.Set("HONUA_DEVOPS_HONUA_API_BASE_URL", "http://localhost:8080");
        environment.Set("HONUA_DEVOPS_OTEL_BASE_URL", "http://localhost:4318");
    }
}
