using System.Net;

namespace Honua.DevOps.Agent.Providers;

internal sealed record ProviderConfiguration(
    string Name,
    string Model,
    string ApiKey,
    Uri? Endpoint,
    string? Region = null)
{
    internal const string DefaultBedrockRegion = "us-west-2";

    internal static ProviderConfiguration Load(ProviderKind provider)
    {
        return provider == ProviderKind.Bedrock
            ? LoadBedrock()
            : LoadOpenAICompatible(provider);
    }

    private static ProviderConfiguration LoadOpenAICompatible(ProviderKind provider)
    {
        string prefix = provider.ToPrefix();
        string model = GetRequiredEnvironmentVariable($"{prefix}_MODEL");
        string apiKey = GetRequiredEnvironmentVariable($"{prefix}_API_KEY");

        string? endpointValue = Environment.GetEnvironmentVariable($"{prefix}_ENDPOINT");
        Uri? endpoint = ParseEndpoint(endpointValue, $"{prefix}_ENDPOINT");

        return new ProviderConfiguration(
            Name: provider.ToConfigValue(),
            Model: model,
            ApiKey: apiKey,
            Endpoint: endpoint);
    }

    // Bedrock uses the AWS credential chain (IAM) by default, so an explicit API key is
    // optional. The model id is configurable (e.g. an inference-profile id or a versioned
    // `anthropic.*` model id) and never carries a hardcoded default model or secret.
    private static ProviderConfiguration LoadBedrock()
    {
        string prefix = ProviderKind.Bedrock.ToPrefix();
        string model = GetRequiredEnvironmentVariable($"{prefix}_MODEL");

        string? region = Environment.GetEnvironmentVariable($"{prefix}_REGION");
        region = string.IsNullOrWhiteSpace(region) ? DefaultBedrockRegion : region.Trim();

        // Optional Bedrock API key (long-lived bearer). When absent, the AWS credential
        // chain (env vars, shared profile, IAM role, Lambda ambient creds) is used.
        string? apiKey = Environment.GetEnvironmentVariable($"{prefix}_API_KEY");
        apiKey = string.IsNullOrWhiteSpace(apiKey) ? string.Empty : apiKey.Trim();

        return new ProviderConfiguration(
            Name: ProviderKind.Bedrock.ToConfigValue(),
            Model: model,
            ApiKey: apiKey,
            Endpoint: null,
            Region: region);
    }

    private static string GetRequiredEnvironmentVariable(string name)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                $"Missing environment variable `{name}`.");
        }

        return value.Trim();
    }

    private static Uri? ParseEndpoint(string? endpointValue, string variableName)
    {
        if (string.IsNullOrWhiteSpace(endpointValue))
        {
            return null;
        }

        if (!Uri.TryCreate(endpointValue.Trim(), UriKind.Absolute, out Uri? endpoint))
        {
            throw new InvalidOperationException(
                $"Environment variable `{variableName}` must be a valid absolute URL.");
        }

        if (!endpoint.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            !endpoint.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Environment variable `{variableName}` must use http or https.");
        }

        if (!IsLocalUri(endpoint) && !endpoint.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Environment variable `{variableName}` must use https for non-local endpoints.");
        }

        return endpoint;
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
