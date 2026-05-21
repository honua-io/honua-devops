using System.Net.Http;

namespace Honua.DevOps.Agent.Operations;

internal static class HttpClientFactory
{
    private static readonly TimeSpan ConnectionLifetime = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ConnectionIdleTimeout = TimeSpan.FromSeconds(50);

    internal static HttpClient Create(TimeSpan requestTimeout)
    {
        SocketsHttpHandler handler = new()
        {
            PooledConnectionLifetime = ConnectionLifetime,
            PooledConnectionIdleTimeout = ConnectionIdleTimeout,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            AllowAutoRedirect = false,
            AutomaticDecompression = System.Net.DecompressionMethods.All
        };

        return new HttpClient(handler, disposeHandler: true)
        {
            Timeout = requestTimeout
        };
    }
}
