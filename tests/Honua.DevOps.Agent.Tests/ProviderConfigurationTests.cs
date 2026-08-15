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

    [Theory]
    [InlineData("codex", (int)ProviderKind.Codex)]
    [InlineData("Codex", (int)ProviderKind.Codex)]
    [InlineData("CODEX", (int)ProviderKind.Codex)]
    [InlineData("claude", (int)ProviderKind.Claude)]
    [InlineData("Claude", (int)ProviderKind.Claude)]
    [InlineData("local-llama", (int)ProviderKind.LocalLlama)]
    [InlineData("LOCAL-LLAMA", (int)ProviderKind.LocalLlama)]
    [InlineData("LocalLlama", (int)ProviderKind.LocalLlama)]
    [InlineData("localllama", (int)ProviderKind.LocalLlama)]
    [InlineData("  local-llama  ", (int)ProviderKind.LocalLlama)]
    [InlineData("bedrock", (int)ProviderKind.Bedrock)]
    [InlineData("Bedrock", (int)ProviderKind.Bedrock)]
    [InlineData("BEDROCK", (int)ProviderKind.Bedrock)]
    [InlineData("aws-bedrock", (int)ProviderKind.Bedrock)]
    [InlineData("  bedrock  ", (int)ProviderKind.Bedrock)]
    public void TryParse_AcceptsAllSupportedForms(string value, int expectedProvider)
    {
        bool parsed = ProviderKindExtensions.TryParse(value, out ProviderKind provider);

        Assert.True(parsed);
        Assert.Equal((ProviderKind)expectedProvider, provider);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("nim")]
    [InlineData("nvidia-nim")]
    [InlineData("local_llama")]
    [InlineData("local llama")]
    [InlineData("gpt")]
    public void TryParse_RejectsUnknownProviderStrings(string? value)
    {
        bool parsed = ProviderKindExtensions.TryParse(value, out ProviderKind provider);

        Assert.False(parsed);
        Assert.Equal(default(ProviderKind), provider);
    }

    [Fact]
    public void TryParse_ReturnsFalseForNull()
    {
        bool parsed = ProviderKindExtensions.TryParse(null, out ProviderKind provider);

        Assert.False(parsed);
        Assert.Equal(default(ProviderKind), provider);
    }

    [Theory]
    [InlineData((int)ProviderKind.Codex, "HONUA_DEVOPS_CODEX")]
    [InlineData((int)ProviderKind.Claude, "HONUA_DEVOPS_CLAUDE")]
    [InlineData((int)ProviderKind.LocalLlama, "HONUA_DEVOPS_LOCAL_LLAMA")]
    [InlineData((int)ProviderKind.Bedrock, "HONUA_DEVOPS_BEDROCK")]
    public void ToPrefix_MapsProviderToEnvironmentVariablePrefix(int providerValue, string expected)
    {
        ProviderKind provider = (ProviderKind)providerValue;
        Assert.Equal(expected, provider.ToPrefix());
    }

    [Theory]
    [InlineData((int)ProviderKind.Codex, "codex")]
    [InlineData((int)ProviderKind.Claude, "claude")]
    [InlineData((int)ProviderKind.LocalLlama, "local-llama")]
    [InlineData((int)ProviderKind.Bedrock, "bedrock")]
    public void ToConfigValue_ReturnsKebabCanonicalForm(int providerValue, string expected)
    {
        ProviderKind provider = (ProviderKind)providerValue;
        Assert.Equal(expected, provider.ToConfigValue());
    }

    [Fact]
    public void Load_LocalLlama_ReadsLocalLlamaEnvBlockAndExposesKebabName()
    {
        using TestEnvironmentVariableScope environment = new();
        SetLocalLlamaDefaults(environment);
        environment.Set("HONUA_DEVOPS_LOCAL_LLAMA_ENDPOINT", "https://integrate.api.nvidia.com/v1");

        ProviderConfiguration configuration = ProviderConfiguration.Load(ProviderKind.LocalLlama);

        Assert.Equal("local-llama", configuration.Name);
        Assert.Equal("meta/llama-3.3-70b-instruct", configuration.Model);
        Assert.Equal("nim-test-key", configuration.ApiKey);
        Assert.NotNull(configuration.Endpoint);
        Assert.Equal("https", configuration.Endpoint!.Scheme);
        Assert.Equal("integrate.api.nvidia.com", configuration.Endpoint!.Host);
    }

    [Fact]
    public void Load_LocalLlama_AllowsHttpLoopbackForSelfHostedNim()
    {
        using TestEnvironmentVariableScope environment = new();
        SetLocalLlamaDefaults(environment);
        environment.Set("HONUA_DEVOPS_LOCAL_LLAMA_ENDPOINT", "http://localhost:8000/v1");

        ProviderConfiguration configuration = ProviderConfiguration.Load(ProviderKind.LocalLlama);

        Assert.NotNull(configuration.Endpoint);
        Assert.Equal(Uri.UriSchemeHttp, configuration.Endpoint!.Scheme);
    }

    [Fact]
    public void Load_LocalLlama_RejectsNonHttpsRemoteEndpoint()
    {
        using TestEnvironmentVariableScope environment = new();
        SetLocalLlamaDefaults(environment);
        environment.Set("HONUA_DEVOPS_LOCAL_LLAMA_ENDPOINT", "http://nim.example.com/v1");

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => ProviderConfiguration.Load(ProviderKind.LocalLlama));

        Assert.Contains("HONUA_DEVOPS_LOCAL_LLAMA_ENDPOINT", exception.Message);
    }

    [Fact]
    public void Load_LocalLlama_RaisesOnMissingModel()
    {
        using TestEnvironmentVariableScope environment = new();
        environment.Set("HONUA_DEVOPS_LOCAL_LLAMA_MODEL", null);
        environment.Set("HONUA_DEVOPS_LOCAL_LLAMA_API_KEY", "nim-test-key");
        environment.Set("HONUA_DEVOPS_LOCAL_LLAMA_ENDPOINT", null);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => ProviderConfiguration.Load(ProviderKind.LocalLlama));

        Assert.Contains("HONUA_DEVOPS_LOCAL_LLAMA_MODEL", exception.Message);
    }

    [Fact]
    public void Load_Bedrock_ReadsModelRegionAndApiKey()
    {
        using TestEnvironmentVariableScope environment = new();
        environment.Set("HONUA_DEVOPS_BEDROCK_MODEL", "anthropic.claude-sonnet-4-20250514-v1:0");
        environment.Set("HONUA_DEVOPS_BEDROCK_REGION", "eu-west-1");
        environment.Set("HONUA_DEVOPS_BEDROCK_API_KEY", "bedrock-bearer-key");

        ProviderConfiguration configuration = ProviderConfiguration.Load(ProviderKind.Bedrock);

        Assert.Equal("bedrock", configuration.Name);
        Assert.Equal("anthropic.claude-sonnet-4-20250514-v1:0", configuration.Model);
        Assert.Equal("eu-west-1", configuration.Region);
        Assert.Equal("bedrock-bearer-key", configuration.ApiKey);
        Assert.Null(configuration.Endpoint);
    }

    [Fact]
    public void Load_Bedrock_DefaultsRegionToUsWest2_WhenUnset()
    {
        using TestEnvironmentVariableScope environment = new();
        environment.Set("HONUA_DEVOPS_BEDROCK_MODEL", "anthropic.claude-sonnet-4-20250514-v1:0");
        environment.Set("HONUA_DEVOPS_BEDROCK_REGION", null);
        environment.Set("HONUA_DEVOPS_BEDROCK_API_KEY", null);

        ProviderConfiguration configuration = ProviderConfiguration.Load(ProviderKind.Bedrock);

        Assert.Equal("us-west-2", configuration.Region);
    }

    [Fact]
    public void Load_Bedrock_AllowsMissingApiKey_ForIamCredentialChain()
    {
        using TestEnvironmentVariableScope environment = new();
        environment.Set("HONUA_DEVOPS_BEDROCK_MODEL", "anthropic.claude-sonnet-4-20250514-v1:0");
        environment.Set("HONUA_DEVOPS_BEDROCK_REGION", "us-east-1");
        environment.Set("HONUA_DEVOPS_BEDROCK_API_KEY", null);

        ProviderConfiguration configuration = ProviderConfiguration.Load(ProviderKind.Bedrock);

        // An empty ApiKey signals "use the AWS credential chain (IAM)".
        Assert.Equal(string.Empty, configuration.ApiKey);
        Assert.Equal("us-east-1", configuration.Region);
    }

    [Fact]
    public void Load_Bedrock_RaisesOnMissingModel()
    {
        using TestEnvironmentVariableScope environment = new();
        environment.Set("HONUA_DEVOPS_BEDROCK_MODEL", null);
        environment.Set("HONUA_DEVOPS_BEDROCK_REGION", null);
        environment.Set("HONUA_DEVOPS_BEDROCK_API_KEY", null);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => ProviderConfiguration.Load(ProviderKind.Bedrock));

        Assert.Contains("HONUA_DEVOPS_BEDROCK_MODEL", exception.Message);
    }

    private static void SetProviderDefaults(TestEnvironmentVariableScope environment)
    {
        environment.Set("HONUA_DEVOPS_CODEX_MODEL", "gpt-test");
        environment.Set("HONUA_DEVOPS_CODEX_API_KEY", "secret");
        environment.Set("HONUA_DEVOPS_CODEX_ENDPOINT", null);
    }

    private static void SetLocalLlamaDefaults(TestEnvironmentVariableScope environment)
    {
        environment.Set("HONUA_DEVOPS_LOCAL_LLAMA_MODEL", "meta/llama-3.3-70b-instruct");
        environment.Set("HONUA_DEVOPS_LOCAL_LLAMA_API_KEY", "nim-test-key");
        environment.Set("HONUA_DEVOPS_LOCAL_LLAMA_ENDPOINT", null);
    }
}
