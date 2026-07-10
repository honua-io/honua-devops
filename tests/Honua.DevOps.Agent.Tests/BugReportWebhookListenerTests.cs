using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Honua.DevOps.Agent.Operations;
using Honua.DevOps.Agent.Operations.BugReport;

namespace Honua.DevOps.Agent.Tests;

public class BugReportWebhookListenerTests
{
    private const string Secret = "bugreport-listener-secret";

    private static BugReportConfiguration Configuration(int port)
        => new(
            WebhookSecret: Secret,
            Port: port,
            Path: "/bug-reports",
            ReplayWindow: TimeSpan.FromMinutes(5),
            Allowlist: ComponentRepoAllowlist.Parse("sdk-js=honua-io/honua-sdk-js"),
            Labels: BugReportConfiguration.DefaultLabels,
            GitHubApiBaseUri: null,
            GitHubToken: null,
            AllowedHosts: Array.Empty<string>());

    [Fact]
    public async Task Listener_AcceptsSignedBugReport_AndResolvesRepoFromAllowlist()
    {
        int port = AllocateFreePort();
        BugReportConfiguration configuration = Configuration(port);

        TaskCompletionSource<(BugReport Report, RepoRef Repo)> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        BugReportWebhookHandler handler = new(
            Secret,
            configuration.Allowlist,
            configuration.ReplayWindow,
            onAccepted: (report, repo, _) =>
            {
                tcs.TrySetResult((report, repo));
                return Task.CompletedTask;
            });

        await using BugReportWebhookListener listener = new(configuration, handler, new StringWriter(), new StringWriter());

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));
        Task runTask = listener.RunAsync(cts.Token);

        try
        {
            string payloadJson = JsonSerializer.Serialize(new
            {
                eventId = "evt-loop-1",
                eventType = "ticket.bug_report.v1",
                emittedAt = DateTimeOffset.UtcNow,
                ticketId = "ST-LOOP-1",
                component = "sdk-js",
                severity = "high",
                fingerprint = "fp-loop-1",
                envelopeRefs = new[] { "env-1" },
                fixtureRefs = new[] { "fx-1" }
            }, BugReportWebhookPayload.JsonOptions);
            string signature = WebhookSignatureVerifier.ComputeSignatureHeader(Secret, payloadJson);

            using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(5) };
            using HttpRequestMessage request = new(HttpMethod.Post, $"http://localhost:{port}/bug-reports")
            {
                Content = new StringContent(payloadJson, Encoding.UTF8, "application/json")
            };
            request.Headers.TryAddWithoutValidation("X-Honua-Signature", signature);
            request.Headers.TryAddWithoutValidation("X-Honua-Event", "ticket.bug_report.v1");

            using HttpResponseMessage response = await client.SendAsync(request, cts.Token);
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

            (BugReport report, RepoRef repo) = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("ST-LOOP-1", report.TicketId);
            Assert.Equal("honua-io/honua-sdk-js", repo.FullName);
        }
        finally
        {
            cts.Cancel();
            try
            {
                await runTask;
            }
            catch (OperationCanceledException)
            {
                // expected on shutdown
            }
        }
    }

    private static int AllocateFreePort()
    {
        using TcpListener probe = new(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }
}
