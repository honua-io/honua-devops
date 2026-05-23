using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Honua.DevOps.Agent.Operations;

namespace Honua.DevOps.Agent.Tests;

public class EscalationWebhookTests
{
    private const string Secret = "shared-test-secret";

    [Fact]
    public void Verifier_AcceptsKnownGoodSignature()
    {
        // Lock down the well-known recipe so the contract drift is caught here.
        byte[] secret = Encoding.UTF8.GetBytes("supersecret");
        byte[] body = Encoding.UTF8.GetBytes("hello world");
        string header = WebhookSignatureVerifier.ComputeSignatureHeader(secret, body);

        // From the spec: HMAC-SHA256("supersecret", "hello world") lowercase hex.
        Assert.StartsWith("sha256=", header, StringComparison.Ordinal);
        Assert.True(WebhookSignatureVerifier.TryVerify(secret, body, header));
    }

    [Fact]
    public void Verifier_RejectsMismatchedSignature()
    {
        byte[] secret = Encoding.UTF8.GetBytes(Secret);
        byte[] body = Encoding.UTF8.GetBytes("{\"eventType\":\"ticket.escalation_requested\"}");
        string tampered = WebhookSignatureVerifier.ComputeSignatureHeader(secret, body)[..^2] + "00";

        Assert.False(WebhookSignatureVerifier.TryVerify(secret, body, tampered));
    }

    [Fact]
    public void Verifier_RejectsMissingHeader()
    {
        byte[] secret = Encoding.UTF8.GetBytes(Secret);
        byte[] body = Encoding.UTF8.GetBytes("anything");

        Assert.False(WebhookSignatureVerifier.TryVerify(secret, body, null));
        Assert.False(WebhookSignatureVerifier.TryVerify(secret, body, ""));
        Assert.False(WebhookSignatureVerifier.TryVerify(secret, body, "  "));
    }

    [Fact]
    public void Verifier_RejectsWrongPrefix()
    {
        byte[] secret = Encoding.UTF8.GetBytes(Secret);
        byte[] body = Encoding.UTF8.GetBytes("anything");
        string signature = WebhookSignatureVerifier.ComputeSignatureHeader(secret, body);

        // strip the sha256= prefix to simulate a misconfigured receiver header
        string headerWithoutPrefix = signature["sha256=".Length..];
        Assert.False(WebhookSignatureVerifier.TryVerify(secret, body, headerWithoutPrefix));
        Assert.False(WebhookSignatureVerifier.TryVerify(secret, body, "md5=" + headerWithoutPrefix));
    }

    [Fact]
    public void Verifier_RejectsNonHexValue()
    {
        byte[] secret = Encoding.UTF8.GetBytes(Secret);
        byte[] body = Encoding.UTF8.GetBytes("anything");

        Assert.False(WebhookSignatureVerifier.TryVerify(secret, body, "sha256=not-hex-value-here-zzz"));
    }

    [Fact]
    public async Task Handler_Returns401OnBadSignature()
    {
        EscalationWebhookHandler handler = new(Secret);
        byte[] body = Encoding.UTF8.GetBytes(BuildHonuaSupportPayloadJson());

        WebhookHandlerResult result = await handler.HandleAsync(body, "sha256=" + new string('0', 64), CancellationToken.None);

        Assert.Equal(401, result.StatusCode);
        Assert.Equal("invalid-signature", result.Reason);
        Assert.Null(result.Payload);
    }

    [Fact]
    public async Task Handler_Returns400OnMalformedJson()
    {
        EscalationWebhookHandler handler = new(Secret);
        byte[] body = Encoding.UTF8.GetBytes("{not json");
        string signature = WebhookSignatureVerifier.ComputeSignatureHeader(Secret, "{not json");

        WebhookHandlerResult result = await handler.HandleAsync(body, signature, CancellationToken.None);

        Assert.Equal(400, result.StatusCode);
        Assert.Equal("malformed-json", result.Reason);
    }

    [Fact]
    public async Task Handler_Returns400OnWrongEventType()
    {
        string payload = """{"eventType":"ticket.created","ticketId":"T-1"}""";
        byte[] body = Encoding.UTF8.GetBytes(payload);
        string signature = WebhookSignatureVerifier.ComputeSignatureHeader(Secret, payload);

        EscalationWebhookHandler handler = new(Secret);
        WebhookHandlerResult result = await handler.HandleAsync(body, signature, CancellationToken.None);

        Assert.Equal(400, result.StatusCode);
        Assert.StartsWith("unexpected-event", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Handler_Returns202AndInvokesCallbackOnValidEvent()
    {
        EscalationWebhookPayload? captured = null;
        EscalationWebhookHandler handler = new(Secret, (payload, _) =>
        {
            captured = payload;
            return Task.CompletedTask;
        });

        string payloadJson = BuildHonuaSupportPayloadJson();
        byte[] body = Encoding.UTF8.GetBytes(payloadJson);
        string signature = WebhookSignatureVerifier.ComputeSignatureHeader(Secret, payloadJson);

        WebhookHandlerResult result = await handler.HandleAsync(body, signature, CancellationToken.None);

        Assert.Equal(202, result.StatusCode);
        Assert.Equal("accepted", result.Reason);
        Assert.NotNull(captured);
        Assert.Equal("evt-20260520-0042", captured!.EventId);
        Assert.Equal(EscalationWebhookPayload.ExpectedEvent, captured.EventType);
        Assert.Equal("ST-20260520-0042", captured.TicketId);
        Assert.Equal("customer-x", captured.CustomerId);
        Assert.Equal("critical", captured.Severity);
        Assert.Equal("triaging", captured.Phase);
        Assert.Equal("investigating", captured.CustomerStatus);
        Assert.Equal("https://support.example.test/tickets/ST-20260520-0042", captured.TicketUrl);
        Assert.NotNull(captured.Escalation);
        Assert.Equal(2, captured.Escalation!.Tier);
        Assert.Equal("operator-scoped", captured.Escalation.AccessMode);
        Assert.Equal(
            DateTimeOffset.Parse("2026-05-20T18:42:00Z").ToUniversalTime(),
            captured.Escalation.EscalatedAt!.Value.ToUniversalTime());
        Assert.Equal("tier=paid-pilot | first-response: within 4 business hours | cadence: at least every 1 business day for production-blocking pilot issues", captured.SlaSummary());
    }

    [Fact]
    public async Task Handler_AcceptsHonuaSupportFixtureWithoutOptionalFields()
    {
        // Regression guard for the contract-drift finding: honua-support's
        // SupportNotificationPayload does not carry symptoms/diagnosis, and
        // small notifications can omit ticketUrl + escalation. We must still
        // accept these and return 202.
        string payloadJson = JsonSerializer.Serialize(new
        {
            eventId = "evt-min-1",
            eventType = "ticket.escalation_requested",
            ticketId = "ST-MIN-1",
            customerId = "customer-min",
            severity = "high",
            environment = "prod",
            service = "tiles-api",
            phase = "open",
            customerStatus = "awaiting-honua",
            sla = new
            {
                supportTier = "paid-pilot",
                firstResponseTarget = "by next business day",
                updateCadence = "as status changes or by next business day",
                defaultSupportMode = "ticketed, artifact-first, AI-assisted unless customer opts out",
                normalSupportPath = "observe -> guided-fix"
            }
        }, EscalationWebhookPayload.JsonOptions);

        byte[] body = Encoding.UTF8.GetBytes(payloadJson);
        string signature = WebhookSignatureVerifier.ComputeSignatureHeader(Secret, payloadJson);

        EscalationWebhookPayload? captured = null;
        EscalationWebhookHandler handler = new(Secret, (payload, _) =>
        {
            captured = payload;
            return Task.CompletedTask;
        });

        WebhookHandlerResult result = await handler.HandleAsync(body, signature, CancellationToken.None);

        Assert.Equal(202, result.StatusCode);
        Assert.Equal("accepted", result.Reason);
        Assert.NotNull(captured);
        Assert.Null(captured!.Escalation);
        Assert.Null(captured.TicketUrl);
        Assert.Null(captured.EscalatedAt);
    }

    [Fact]
    public void Configuration_RequiresSecret()
    {
        using TestEnvironmentVariableScope scope = new();
        scope.Set("HONUA_DEVOPS_WEBHOOK_SECRET", null);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(WebhookListenerConfiguration.Load);
        Assert.Contains("HONUA_DEVOPS_WEBHOOK_SECRET", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Configuration_LoadsDefaults()
    {
        using TestEnvironmentVariableScope scope = new();
        scope.Set("HONUA_DEVOPS_WEBHOOK_SECRET", "x");
        scope.Set("HONUA_DEVOPS_WEBHOOK_PORT", null);
        scope.Set("HONUA_DEVOPS_WEBHOOK_PATH", null);
        scope.Set("HONUA_DEVOPS_WEBHOOK_AUTO_TRIAGE", null);

        WebhookListenerConfiguration configuration = WebhookListenerConfiguration.Load();

        Assert.Equal("x", configuration.Secret);
        Assert.Equal(WebhookListenerConfiguration.DefaultPort, configuration.Port);
        Assert.Equal(WebhookListenerConfiguration.DefaultPath, configuration.Path);
        Assert.True(configuration.AutoTriage);
    }

    [Fact]
    public void Configuration_RejectsRelativePath()
    {
        using TestEnvironmentVariableScope scope = new();
        scope.Set("HONUA_DEVOPS_WEBHOOK_SECRET", "x");
        scope.Set("HONUA_DEVOPS_WEBHOOK_PATH", "escalations");

        Assert.Throws<InvalidOperationException>(WebhookListenerConfiguration.Load);
    }

    [Fact]
    public void Configuration_RejectsInvalidPort()
    {
        using TestEnvironmentVariableScope scope = new();
        scope.Set("HONUA_DEVOPS_WEBHOOK_SECRET", "x");
        scope.Set("HONUA_DEVOPS_WEBHOOK_PORT", "0");

        Assert.Throws<InvalidOperationException>(WebhookListenerConfiguration.Load);
    }

    [Fact]
    public async Task Listener_AcceptsSignedPostOverLoopback()
    {
        int port = AllocateFreePort();
        WebhookListenerConfiguration configuration = new(
            Secret: Secret,
            Port: port,
            Path: "/escalations",
            AutoTriage: false);

        TaskCompletionSource<EscalationWebhookPayload> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        EscalationWebhookHandler webhookHandler = new(Secret, (payload, _) =>
        {
            tcs.TrySetResult(payload);
            return Task.CompletedTask;
        });

        StringWriter stdout = new();
        StringWriter stderr = new();
        await using EscalationWebhookListener listener = new(configuration, webhookHandler, stdout, stderr);

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));
        Task runTask = listener.RunAsync(cts.Token);

        try
        {
            string payloadJson = BuildHonuaSupportPayloadJson();
            string signature = WebhookSignatureVerifier.ComputeSignatureHeader(Secret, payloadJson);

            using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(5) };
            using HttpRequestMessage request = new(HttpMethod.Post, $"http://localhost:{port}/escalations")
            {
                Content = new StringContent(payloadJson, Encoding.UTF8, "application/json")
            };
            request.Headers.TryAddWithoutValidation("X-Honua-Signature", signature);
            request.Headers.TryAddWithoutValidation("X-Honua-Event", "ticket.escalation_requested");

            using HttpResponseMessage response = await client.SendAsync(request, cts.Token);
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

            EscalationWebhookPayload payload = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("ST-20260520-0042", payload.TicketId);
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

    [Fact]
    public async Task Listener_Returns401OnTamperedSignatureOverLoopback()
    {
        int port = AllocateFreePort();
        WebhookListenerConfiguration configuration = new(
            Secret: Secret,
            Port: port,
            Path: "/escalations",
            AutoTriage: false);

        bool callbackInvoked = false;
        EscalationWebhookHandler webhookHandler = new(Secret, (_, _) =>
        {
            callbackInvoked = true;
            return Task.CompletedTask;
        });

        await using EscalationWebhookListener listener = new(configuration, webhookHandler);

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));
        Task runTask = listener.RunAsync(cts.Token);

        try
        {
            string payloadJson = BuildHonuaSupportPayloadJson();

            using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(5) };
            using HttpRequestMessage request = new(HttpMethod.Post, $"http://localhost:{port}/escalations")
            {
                Content = new StringContent(payloadJson, Encoding.UTF8, "application/json")
            };
            request.Headers.TryAddWithoutValidation("X-Honua-Signature", "sha256=" + new string('a', 64));

            using HttpResponseMessage response = await client.SendAsync(request, cts.Token);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.False(callbackInvoked);
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

    /// <summary>
    /// Builds the exact JSON shape honua-support's
    /// <c>SupportNotificationService</c> serializes from
    /// <c>SupportNotificationPayload</c>, including the camelCase property
    /// names produced by <c>JsonSerializerDefaults.Web</c>. Field-for-field
    /// fidelity with the sender's contract is what this fixture protects.
    /// </summary>
    private static string BuildHonuaSupportPayloadJson()
    {
        object body = new
        {
            eventId = "evt-20260520-0042",
            eventType = "ticket.escalation_requested",
            ticketId = "ST-20260520-0042",
            customerId = "customer-x",
            severity = "critical",
            environment = "prod",
            service = "roads-api",
            phase = "triaging",
            customerStatus = "investigating",
            sla = new
            {
                supportTier = "paid-pilot",
                firstResponseTarget = "within 4 business hours",
                updateCadence = "at least every 1 business day for production-blocking pilot issues",
                defaultSupportMode = "ticketed, artifact-first, AI-assisted unless customer opts out",
                normalSupportPath = "observe -> guided-fix"
            },
            escalation = new
            {
                tier = 2,
                accessMode = "operator-scoped",
                escalatedAt = DateTimeOffset.Parse("2026-05-20T18:42:00Z")
            },
            ticketUrl = "https://support.example.test/tickets/ST-20260520-0042"
        };

        return JsonSerializer.Serialize(body, EscalationWebhookPayload.JsonOptions);
    }
}
