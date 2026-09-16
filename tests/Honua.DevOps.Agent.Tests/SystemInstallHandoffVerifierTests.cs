using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text;
using Honua.DevOps.Agent.Operations;

namespace Honua.DevOps.Agent.Tests;

/// <summary>
/// System tests for the production verifier.  These deliberately use a real child
/// process and a live loopback HTTP listener; no IInstallHandoffVerifier fake is in
/// the path being qualified.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class SystemInstallHandoffVerifierTests
{
    private const string Candidate = "honua-2026.1.1-test";
    private const string Integrity = "sha512-dGVzdC1pbnRlZ3JpdHk=";
    private const string AdminKey = "system-verifier-secret-do-not-print";

    [Fact]
    public async Task RealVerifier_ProvesLiveServerAndMultiPageProxyAndReapsChild()
    {
        using VerifierFixture fixture = new();
        await using LocalCandidateServer server = await LocalCandidateServer.StartAsync(Candidate, AdminKey);

        InstallHandoffVerificationResult result = await fixture.VerifyAsync(server.BaseUrl, "happy");

        Assert.True(result.Succeeded, result.Detail);
        Assert.Equal("install-handoff-verified", result.Status);
        Assert.Equal(new[] { "honua_admin_server_status", "honua_buffer_features" }, result.ObservedTools);
        Assert.DoesNotContain(AdminKey, Serialize(result), StringComparison.Ordinal);
        Assert.True(await fixture.WaitForChildReapedAsync());
    }

    [Theory]
    [InlineData("noise", "mcp-stdout-noise")]
    [InlineData("malformed", "mcp-response-malformed")]
    [InlineData("out-of-order", "mcp-response-out-of-order")]
    [InlineData("repeat-cursor", "mcp-pagination-loop")]
    [InlineData("missing-tool", "mcp-roster-incomplete")]
    [InlineData("early-exit", "handoff-verification-failed")]
    public async Task ProtocolFaults_AreTypedBoundedSecretlessAndReaped(string mode, string expectedStatus)
    {
        using VerifierFixture fixture = new();
        await using LocalCandidateServer server = await LocalCandidateServer.StartAsync(Candidate, AdminKey);

        InstallHandoffVerificationResult result = await fixture.VerifyAsync(server.BaseUrl, mode);

        Assert.False(result.Succeeded);
        Assert.Equal(expectedStatus, result.Status);
        Assert.DoesNotContain(AdminKey, Serialize(result), StringComparison.Ordinal);
        Assert.True(await fixture.WaitForChildReapedAsync());
    }

    [Fact]
    public async Task SilentProxy_TerminatesAtOverallDeadlineAndIsReaped()
    {
        using VerifierFixture fixture = new(TimeSpan.FromMilliseconds(750));
        await using LocalCandidateServer server = await LocalCandidateServer.StartAsync(Candidate, AdminKey);

        InstallHandoffVerificationResult result = await fixture.VerifyAsync(server.BaseUrl, "silent");

        Assert.False(result.Succeeded);
        Assert.Equal("handoff-verification-timeout", result.Status);
        Assert.True(await fixture.WaitForChildReapedAsync());
    }

    [Fact]
    public async Task CandidateSubstringIsNotAcceptedAsIdentity()
    {
        using VerifierFixture fixture = new();
        await using LocalCandidateServer server = await LocalCandidateServer.StartAsync("prefix-" + Candidate + "-suffix", AdminKey);

        InstallHandoffVerificationResult result = await fixture.VerifyAsync(server.BaseUrl, "happy");

        Assert.False(result.Succeeded);
        Assert.Equal("candidate-identity-mismatch", result.Status);
        Assert.False(fixture.ChildStarted);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task AuthenticationFailuresNeverLaunchProxy(HttpStatusCode status)
    {
        using VerifierFixture fixture = new();
        await using LocalCandidateServer server = await LocalCandidateServer.StartAsync(Candidate, AdminKey, status);

        InstallHandoffVerificationResult result = await fixture.VerifyAsync(server.BaseUrl, "happy");

        Assert.False(result.Succeeded);
        Assert.Equal("handoff-auth-failed", result.Status);
        Assert.False(fixture.ChildStarted);
    }

    [Fact]
    public async Task MissingSecretAndUnavailableRegistryReturnTypedNonReadyResults()
    {
        using VerifierFixture fixture = new();
        await using LocalCandidateServer server = await LocalCandidateServer.StartAsync(Candidate, AdminKey);
        Environment.SetEnvironmentVariable(fixture.SecretVariable, null);
        InstallHandoffVerificationResult missing = await fixture.VerifyAsync(server.BaseUrl, "happy", setSecret: false);

        SystemInstallHandoffVerifier unavailable = new(Path.Combine(fixture.Root, "does-not-exist"));
        Environment.SetEnvironmentVariable(fixture.SecretVariable, AdminKey);
        InstallHandoffVerificationResult registry = await unavailable.VerifyAsync(fixture.Request(server.BaseUrl, "happy"));

        Assert.Equal("secret-resolution-failed", missing.Status);
        Assert.Equal("proxy-registry-unavailable", registry.Status);
        Assert.False(fixture.ChildStarted);
    }

    [Fact]
    public async Task HealthFailureAndInstalledPackageIntegrityMismatchFailBeforeProxyLaunch()
    {
        using VerifierFixture fixture = new(npmIntegrity: "sha512-d3Jvbmc=");
        await using LocalCandidateServer server = await LocalCandidateServer.StartAsync(
            Candidate, AdminKey, healthStatus: HttpStatusCode.ServiceUnavailable);

        InstallHandoffVerificationResult mismatch = await fixture.VerifyAsync(server.BaseUrl, "happy");
        Assert.Equal("proxy-integrity-mismatch", mismatch.Status);

        using VerifierFixture healthyPackage = new();
        InstallHandoffVerificationResult unhealthy = await healthyPackage.VerifyAsync(server.BaseUrl, "happy");
        Assert.Equal("handoff-health-failed", unhealthy.Status);
        Assert.False(fixture.ChildStarted);
        Assert.False(healthyPackage.ChildStarted);
    }

    private static string Serialize(InstallHandoffVerificationResult result)
        => System.Text.Json.JsonSerializer.Serialize(result);

    private sealed class VerifierFixture : IDisposable
    {
        private readonly TimeSpan deadline;
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), $"honua-handoff-system-{Guid.NewGuid():n}");
        internal string SecretVariable { get; } = $"HONUA_HANDOFF_TEST_{Guid.NewGuid():N}";
        private string Marker => Path.Combine(Root, "child.pid");
        internal bool ChildStarted => File.Exists(Marker);

        internal VerifierFixture(TimeSpan? deadline = null, string npmIntegrity = Integrity)
        {
            this.deadline = deadline ?? TimeSpan.FromSeconds(5);
            Directory.CreateDirectory(Root);
            WriteExecutable("npm", "#!/usr/bin/env bash\nprintf '%s\\n' '\"" + npmIntegrity + "\"'\n");
            WriteExecutable("proxy", ProxyScript);
        }

        internal InstallHandoffVerificationRequest Request(string baseUrl, string mode) => new(
            Path.Combine(Root, "proxy"), [mode, Marker], new Dictionary<string, string>(),
            "secret://" + SecretVariable, baseUrl, Candidate, "@honua/mcp-server@2026.1.1",
            Integrity, $"urn:honua:provisioning:{new string('a', 32)}",
            ["honua_admin_server_status", "honua_buffer_features"], deadline);

        internal async Task<InstallHandoffVerificationResult> VerifyAsync(string baseUrl, string mode, bool setSecret = true)
        {
            if (setSecret) Environment.SetEnvironmentVariable(SecretVariable, AdminKey);
            SystemInstallHandoffVerifier verifier = new(Path.Combine(Root, "npm"));
            return await verifier.VerifyAsync(Request(baseUrl, mode));
        }

        internal async Task<bool> WaitForChildReapedAsync()
        {
            if (!File.Exists(Marker)) return false;
            string pid = await File.ReadAllTextAsync(Marker);
            for (int attempt = 0; attempt < 20; attempt++)
            {
                if (!Directory.Exists("/proc/" + pid.Trim())) return true;
                await Task.Delay(25);
            }
            return false;
        }

        private void WriteExecutable(string name, string content)
        {
            string path = Path.Combine(Root, name);
            File.WriteAllText(path, content);
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(SecretVariable, null);
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
        }

        private const string ProxyScript = """
#!/usr/bin/env bash
mode="$1"
marker="$2"
printf '%s' "$$" > "$marker"
if [[ "$mode" == early-exit ]]; then exit 7; fi
if [[ "$mode" == silent ]]; then while IFS= read -r line; do sleep 30; done; exit 0; fi
while IFS= read -r line; do
  if [[ "$line" != *'"id"'* ]]; then continue; fi
  id=$(printf '%s' "$line" | sed -n 's/.*"id":\([0-9][0-9]*\).*/\1/p')
  if [[ "$mode" == noise ]]; then printf '%s\n' 'starting proxy'; mode=happy; continue; fi
  if [[ "$mode" == malformed ]]; then printf '%s\n' '{"jsonrpc":"2.0","result":{}}'; mode=happy; continue; fi
  if [[ "$mode" == out-of-order ]]; then printf '{"jsonrpc":"2.0","id":%s,"result":{}}\n' "$((id + 1))"; mode=happy; continue; fi
  if [[ "$line" == *'"initialize"'* ]]; then
    printf '{"jsonrpc":"2.0","id":%s,"result":{"protocolVersion":"2025-06-18"}}\n' "$id"
  elif [[ "$line" == *'"tools/list"'* ]]; then
    if [[ "$line" == *'"cursor"'* ]]; then
      next=''
      [[ "$mode" == repeat-cursor ]] && next=',"nextCursor":"page-2"'
      printf '{"jsonrpc":"2.0","id":%s,"result":{"tools":[{"name":"honua_buffer_features"}]%s}}\n' "$id" "$next"
    else
      next=',"nextCursor":"page-2"'
      [[ "$mode" == missing-tool ]] && next=''
      printf '{"jsonrpc":"2.0","id":%s,"result":{"tools":[{"name":"honua_admin_server_status"}]%s}}\n' "$id" "$next"
    fi
  elif [[ "$line" == *'"tools/call"'* ]]; then
    printf '{"jsonrpc":"2.0","id":%s,"result":{"content":[]}}\n' "$id"
  fi
done
""";
    }

    private sealed class LocalCandidateServer : IAsyncDisposable
    {
        private readonly HttpListener listener;
        private readonly CancellationTokenSource stop = new();
        private readonly Task loop;
        internal string BaseUrl { get; }

        private LocalCandidateServer(
            HttpListener listener, string baseUrl, string candidate, string key,
            HttpStatusCode authStatus, HttpStatusCode healthStatus)
        {
            this.listener = listener;
            BaseUrl = baseUrl;
            loop = Task.Run(async () =>
            {
                while (!stop.IsCancellationRequested)
                {
                    HttpListenerContext context;
                    try { context = await listener.GetContextAsync().WaitAsync(stop.Token); }
                    catch (OperationCanceledException) { break; }
                    string path = context.Request.Url!.AbsolutePath;
                    if (path == "/healthz/ready")
                    {
                        context.Response.StatusCode = (int)healthStatus;
                    }
                    else if (path == "/api/v1/admin/version")
                    {
                        bool keyMatches = context.Request.Headers["X-API-Key"] == key;
                        context.Response.StatusCode = keyMatches ? (int)authStatus : 401;
                        if (context.Response.StatusCode == 200)
                        {
                            byte[] bytes = Encoding.UTF8.GetBytes($"{{\"candidateReference\":\"{candidate}\"}}");
                            await context.Response.OutputStream.WriteAsync(bytes);
                        }
                    }
                    else context.Response.StatusCode = 404;
                    context.Response.Close();
                }
            });
        }

        internal static Task<LocalCandidateServer> StartAsync(
            string candidate, string key, HttpStatusCode authStatus = HttpStatusCode.OK,
            HttpStatusCode healthStatus = HttpStatusCode.OK)
        {
            using TcpListener reservation = new(IPAddress.Loopback, 0);
            reservation.Start();
            int port = ((IPEndPoint)reservation.LocalEndpoint).Port;
            reservation.Stop();
            string baseUrl = $"http://127.0.0.1:{port}";
            HttpListener listener = new();
            listener.Prefixes.Add(baseUrl + "/");
            listener.Start();
            return Task.FromResult(new LocalCandidateServer(listener, baseUrl, candidate, key, authStatus, healthStatus));
        }

        public async ValueTask DisposeAsync()
        {
            stop.Cancel();
            listener.Stop();
            await loop;
            listener.Close();
            stop.Dispose();
        }
    }
}
