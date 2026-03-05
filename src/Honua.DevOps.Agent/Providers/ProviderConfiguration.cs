using System.Net;

namespace Honua.DevOps.Agent.Providers;

internal sealed record ProviderConfiguration(
    string Name,
    string Model,
    string ApiKey,
    Uri? Endpoint)
{
    internal static ProviderConfiguration Load(ProviderKind provider)
    {
        string prefix = provider.ToPrefix();
        string model = GetRequiredEnvironmentVariable($"{prefix}_MODEL");
        string apiKey = GetRequiredEnvironmentVariable($"{prefix}_API_KEY");

        string? endpointValue = Environment.GetEnvironmentVariable($"{prefix}_ENDPOINT");
        Uri? endpoint = ParseEndpoint(endpointValue, $"{prefix}_ENDPOINT");

        return new ProviderConfiguration(
            Name: provider.ToString().ToLowerInvariant(),
            Model: model,
            ApiKey: apiKey,
            Endpoint: endpoint);
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
