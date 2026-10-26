using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Amazon.BedrockRuntime;
using Honua.DevOps.Agent.Providers;
using OpenAI;

namespace Honua.DevOps.Agent.Tests;

public class ProviderTransportTests
{
    [Fact]
    public void DefaultEndpoint_UsesExplicitTransportAndRetryPolicy()
    {
        using TestEnvironmentVariableScope environment = new();
        environment.Set("HONUA_DEVOPS_CODEX_MODEL", "fake-model");
        environment.Set("HONUA_DEVOPS_CODEX_ENDPOINT", null);
        ProviderRoute route = ProviderConfiguration.LoadRoute(ProviderKind.Codex);
        OpenAIClientOptions options = AgentProviderFactory.CreateClientOptions(
            new ProviderConfiguration("codex", route.Model, "fake-key", route.Endpoint));
        Assert.IsType<HttpClientPipelineTransport>(options.Transport);
        Assert.IsType<ClientRetryPolicy>(options.RetryPolicy);
        Assert.Equal(new Uri("https://api.openai.com/v1"), options.Endpoint);
    }

    [Fact]
    public void Bedrock_UsesRegionalResolutionWithoutRedirectsOrRetries()
    {
        using BedrockChatClientAdapter adapter = BedrockChatClientAdapter.FromConfiguration(
            new ProviderConfiguration("bedrock", "fake-model", "fake-token", null, "us-east-2"));
        AmazonBedrockRuntimeClient runtime = Assert.IsType<AmazonBedrockRuntimeClient>(
            adapter.GetService(typeof(AmazonBedrockRuntimeClient)));
        Assert.Equal("us-east-2", runtime.Config.RegionEndpoint.SystemName);
        Assert.True(runtime.Config.IgnoreConfiguredEndpointUrls);
        Assert.False(runtime.Config.AllowAutoRedirect);
        Assert.Equal(0, runtime.Config.MaxErrorRetry);
        Assert.Equal(0, runtime.Config.MaxStaleConnectionRetries);
    }

    [Theory]
    [InlineData(307)]
    [InlineData(308)]
    public async Task ProductionTransport_DoesNotSendToRedirectTarget(int status)
    {
        await using LoopbackServer target = new("HTTP/1.1 500 Error\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        await using LoopbackServer origin = new($"HTTP/1.1 {status} Redirect\r\nLocation: {target.Endpoint}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        await InvokeFailureAsync(origin.Endpoint, status);
        Assert.Equal(1, origin.Requests);
        Assert.Equal(0, target.Requests);
    }

    [Theory]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(503)]
    public async Task ProductionTransport_RetryableFailureSendsExactlyOnce(int status)
    {
        await using LoopbackServer server = new($"HTTP/1.1 {status} Error\r\nRetry-After: 0\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        await InvokeFailureAsync(server.Endpoint, status);
        Assert.Equal(1, server.Requests);
    }

    private static async Task InvokeFailureAsync(Uri endpoint, int expectedStatus)
    {
        using TestEnvironmentVariableScope environment = new();
        environment.Set("HONUA_DEVOPS_CODEX_MODEL", "fake-model");
        environment.Set("HONUA_DEVOPS_CODEX_API_KEY", "fake-key");
        environment.Set("HONUA_DEVOPS_CODEX_ENDPOINT", endpoint.ToString());
        var agent = AgentProviderFactory.Create(ProviderKind.Codex, "synthetic transport test", new(null), ProviderModelUseCase.Operator).Agent;
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
        Exception? failure = await Record.ExceptionAsync(async () =>
            await agent.RunAsync("synthetic prompt", cancellationToken: deadline.Token));
        ClientResultException result = Assert.IsAssignableFrom<ClientResultException>(failure);
        Assert.Equal(expectedStatus, result.Status);
        Assert.False(deadline.IsCancellationRequested); // A timeout must not masquerade as retry refusal.
    }

    private sealed class LoopbackServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;
        private int _requests;
        internal int Requests => Volatile.Read(ref _requests);
        internal Uri Endpoint { get; }

        internal LoopbackServer(string response)
        {
            _listener.Start();
            Endpoint = new Uri($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/v1");
            _loop = ServeAsync(response);
        }

        private async Task ServeAsync(string response)
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    using TcpClient connection = await _listener.AcceptTcpClientAsync(_stop.Token);
                    using NetworkStream stream = connection.GetStream();
                    await DrainRequestAsync(stream, _stop.Token);
                    Interlocked.Increment(ref _requests);
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(response), _stop.Token);
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        }

        private static async Task DrainRequestAsync(NetworkStream stream, CancellationToken cancellationToken)
        {
            using MemoryStream header = new();
            byte[] one = new byte[1];
            while (true)
            {
                int count = await stream.ReadAsync(one, cancellationToken);
                if (count == 0) throw new IOException("Incomplete synthetic request headers.");
                header.WriteByte(one[0]);
                if (header.Length > 16_384) throw new IOException("Synthetic request headers too large.");
                if (header.Length >= 4 && header.GetBuffer().AsSpan((int)header.Length - 4, 4)
                    .SequenceEqual("\r\n\r\n"u8)) break;
            }
            string[] lines = Encoding.ASCII.GetString(header.ToArray()).Split("\r\n");
            Assert.DoesNotContain(lines, line => line.StartsWith("Transfer-Encoding:", StringComparison.OrdinalIgnoreCase));
            string contentLength = Assert.Single(lines, line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
            int remaining = int.Parse(contentLength.Split(':', 2)[1], System.Globalization.CultureInfo.InvariantCulture);
            Assert.InRange(remaining, 1, 65_536);
            byte[] buffer = new byte[4096];
            while (remaining > 0)
            {
                int count = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining)), cancellationToken);
                if (count == 0) throw new IOException("Incomplete synthetic request body.");
                remaining -= count;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _listener.Stop();
            await _loop;
            _stop.Dispose();
        }
    }
}
