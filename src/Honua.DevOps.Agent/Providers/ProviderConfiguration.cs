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

    internal static ProviderConfiguration Load(ProviderKind provider) => LoadCredentials(LoadRoute(provider));

    internal static ProviderRoute LoadRoute(ProviderKind provider)
    {
        string prefix = provider.ToPrefix();
        string model = GetRequiredEnvironmentVariable($"{prefix}_MODEL");
        if (provider == ProviderKind.Bedrock)
        {
            string? region = Environment.GetEnvironmentVariable($"{prefix}_REGION");
            return new(provider, model, null, string.IsNullOrWhiteSpace(region) ? DefaultBedrockRegion : region.Trim());
        }
        Uri endpoint = ParseEndpoint(Environment.GetEnvironmentVariable($"{prefix}_ENDPOINT"), $"{prefix}_ENDPOINT")
            ?? new Uri("https://api.openai.com/v1");
        return new(provider, model, endpoint, null);
    }

    internal static ProviderConfiguration LoadCredentials(ProviderRoute route)
    {
        string variable = $"{route.Provider.ToPrefix()}_API_KEY";
        string key = route.Provider == ProviderKind.Bedrock
            ? Environment.GetEnvironmentVariable(variable)?.Trim() ?? string.Empty
            : GetRequiredEnvironmentVariable(variable);
        return new(route.Name, route.Model, key, route.Endpoint, route.Region);
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

        if (!string.IsNullOrEmpty(endpoint.UserInfo) ||
            !string.IsNullOrEmpty(endpoint.Query) ||
            !string.IsNullOrEmpty(endpoint.Fragment))
        {
            throw new InvalidOperationException(
                $"Environment variable `{variableName}` must not contain userinfo, query, or fragment.");
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
