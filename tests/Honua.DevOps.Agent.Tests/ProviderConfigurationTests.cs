using Honua.DevOps.Agent.Providers;

namespace Honua.DevOps.Agent.Tests;

public class ProviderConfigurationTests
{
    [Fact]
    public void Load_RejectsNonHttpsRemoteProviderEndpoint()
    {
        using TestEnvironmentVariableScope environment = new();
        SetProviderDefaults(environment);
        environment.Set("HONUA_DEVOPS_CODEX_ENDPOINT", "http://api.example.com/v1");

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => ProviderConfiguration.Load(ProviderKind.Codex));

        Assert.Contains("HONUA_DEVOPS_CODEX_ENDPOINT", exception.Message);
        Assert.Contains("https", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Load_AllowsHttpLoopbackProviderEndpoint()
    {
        using TestEnvironmentVariableScope environment = new();
        SetProviderDefaults(environment);
        environment.Set("HONUA_DEVOPS_CODEX_ENDPOINT", "http://localhost:11434/v1");

        ProviderConfiguration configuration = ProviderConfiguration.Load(ProviderKind.Codex);

        Assert.NotNull(configuration.Endpoint);
        Assert.Equal(Uri.UriSchemeHttp, configuration.Endpoint!.Scheme);
    }

    private static void SetProviderDefaults(TestEnvironmentVariableScope environment)
    {
        environment.Set("HONUA_DEVOPS_CODEX_MODEL", "gpt-test");
        environment.Set("HONUA_DEVOPS_CODEX_API_KEY", "secret");
        environment.Set("HONUA_DEVOPS_CODEX_ENDPOINT", null);
    }
}
