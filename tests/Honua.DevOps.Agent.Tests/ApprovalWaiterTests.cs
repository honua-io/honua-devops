using System.Net;
using System.Net.Http;
using Honua.DevOps.Agent.Operations;
using Honua.DevOps.Agent.Operations.ConsoleBridge;
using OperatorPolicyModel = Honua.DevOps.Agent.Operations.OperatorPolicy.OperatorPolicy;
using Honua.DevOps.Agent.Operations.OperatorPolicy;

namespace Honua.DevOps.Agent.Tests;

public sealed class ApprovalWaiterTests
{
    [Fact]
    public async Task WaitAsync_PollsUntilOperationLeavesAwaitingApproval()
    {
        // AwaitingApproval twice, then an operator approves → Submitted.
        var handler = SequencedGetHandler(
            StatusOk("op-1", "AwaitingApproval"),
            StatusOk("op-1", "AwaitingApproval"),
            StatusOk("op-1", "Submitted"));

        var waiter = CreateWaiter(handler, ApprovalMode.PrFirst);

        var result = await waiter.WaitAsync("op-1", CancellationToken.None);

        Assert.Equal(ApprovalWaitOutcome.Resolved, result.Outcome);
        Assert.Equal("Submitted", result.FinalStatus);
        Assert.False(result.SubmittedByWaiter);
        Assert.True(result.Polls >= 3);
        // pr-first must never POST a submit; it only reads.
        Assert.DoesNotContain(handler.CapturedRequests, r => r.Method == "POST");
    }

    [Fact]
    public async Task WaitAsync_PrFirst_DoesNotSubmit_AndReportsSuccessTerminal()
    {
        var handler = SequencedGetHandler(
            StatusOk("op-2", "AwaitingApproval"),
            StatusOk("op-2", "Reconciling"),
            StatusOk("op-2", "Succeeded"));

        // The waiter resolves on the first non-AwaitingApproval status (Reconciling).
        var waiter = CreateWaiter(handler, ApprovalMode.PrFirst);

        var result = await waiter.WaitAsync("op-2", CancellationToken.None);

        Assert.Equal(ApprovalWaitOutcome.Resolved, result.Outcome);
        Assert.Equal("Reconciling", result.FinalStatus);
        Assert.False(result.IsTerminalSuccess);
        Assert.DoesNotContain(handler.CapturedRequests, r => r.Method == "POST");
    }

    [Fact]
    public async Task WaitAsync_DirectAllowed_SubmitsThenResolves()
    {
        int getCount = 0;
        var handler = new TestHttpMessageHandler(request =>
        {
            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsoluteUri.EndsWith("/submit", StringComparison.Ordinal))
            {
                return StatusOk("op-3", "Submitted");
            }

            getCount++;
            // First GET is AwaitingApproval (triggers the direct-allowed submit), then Submitted.
            return getCount == 1
                ? StatusOk("op-3", "AwaitingApproval")
                : StatusOk("op-3", "Submitted");
        });

        var waiter = CreateWaiter(handler, ApprovalMode.DirectAllowed);

        var result = await waiter.WaitAsync("op-3", CancellationToken.None);

        Assert.Equal(ApprovalWaitOutcome.Resolved, result.Outcome);
        Assert.Equal("Submitted", result.FinalStatus);
        Assert.True(result.SubmittedByWaiter);
        Assert.Contains(
            handler.CapturedRequests,
            r => r.Method == "POST" && r.Uri.EndsWith("/submit", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WaitAsync_TimesOut_WhenStillAwaitingApproval()
    {
        var handler = new TestHttpMessageHandler(_ => StatusOk("op-4", "AwaitingApproval"));
        var clock = new ManualTimeProvider(DateTimeOffset.UnixEpoch);

        var waiter = new ApprovalWaiter(
            CreateGateway(handler),
            OperatorPolicyModel.Default,
            timeout: TimeSpan.FromSeconds(30),
            initialPollInterval: TimeSpan.FromSeconds(10),
            maxPollInterval: TimeSpan.FromSeconds(10),
            // Advance the clock on each delay so the deadline is reached deterministically.
            delay: (interval, _) =>
            {
                clock.Advance(interval);
                return Task.CompletedTask;
            },
            timeProvider: clock);

        var result = await waiter.WaitAsync("op-4", CancellationToken.None);

        Assert.Equal(ApprovalWaitOutcome.TimedOut, result.Outcome);
        Assert.Equal("AwaitingApproval", result.FinalStatus);
    }

    [Fact]
    public async Task WaitAsync_NotFound_IsTerminal()
    {
        var handler = new TestHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("not found"),
            });

        var waiter = CreateWaiter(handler, ApprovalMode.PrFirst);

        var result = await waiter.WaitAsync("missing-op", CancellationToken.None);

        Assert.Equal(ApprovalWaitOutcome.NotFound, result.Outcome);
        Assert.Null(result.FinalStatus);
    }

    [Fact]
    public async Task WaitAsync_ToleratesTransientErrors_ThenResolves()
    {
        int call = 0;
        var handler = new TestHttpMessageHandler(_ =>
        {
            call++;
            // Two transient 502s, then a real status — the waiter must not abort on the flakes.
            return call <= 2
                ? new HttpResponseMessage(HttpStatusCode.BadGateway) { Content = new StringContent("upstream") }
                : StatusOk("op-5", "Submitted");
        });

        var waiter = CreateWaiter(handler, ApprovalMode.PrFirst, maxConsecutiveTransientErrors: 10);

        var result = await waiter.WaitAsync("op-5", CancellationToken.None);

        Assert.Equal(ApprovalWaitOutcome.Resolved, result.Outcome);
        Assert.Equal("Submitted", result.FinalStatus);
    }

    [Fact]
    public async Task WaitAsync_Errors_WhenTransientErrorsExceedBudget()
    {
        var handler = new TestHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.BadGateway) { Content = new StringContent("upstream") });

        var waiter = CreateWaiter(handler, ApprovalMode.PrFirst, maxConsecutiveTransientErrors: 3);

        var result = await waiter.WaitAsync("op-6", CancellationToken.None);

        Assert.Equal(ApprovalWaitOutcome.Errored, result.Outcome);
    }

    [Fact]
    public void ResolveTimeout_UsesDefault_WhenUnset()
    {
        using var scope = new TestEnvironmentVariableScope();
        scope.Set(ApprovalWaiter.ApprovalTimeoutSecondsVariable, null);

        Assert.Equal(
            TimeSpan.FromSeconds(ApprovalWaiter.DefaultApprovalTimeoutSeconds),
            ApprovalWaiter.ResolveTimeout());
    }

    [Fact]
    public void ResolveTimeout_ParsesOverride()
    {
        using var scope = new TestEnvironmentVariableScope();
        scope.Set(ApprovalWaiter.ApprovalTimeoutSecondsVariable, "120");

        Assert.Equal(TimeSpan.FromSeconds(120), ApprovalWaiter.ResolveTimeout());
    }

    [Fact]
    public void ResolveTimeout_RejectsInvalidOverride()
    {
        using var scope = new TestEnvironmentVariableScope();
        scope.Set(ApprovalWaiter.ApprovalTimeoutSecondsVariable, "not-a-number");

        Assert.Throws<InvalidOperationException>(() => ApprovalWaiter.ResolveTimeout());
    }

    private static ApprovalWaiter CreateWaiter(
        TestHttpMessageHandler handler,
        ApprovalMode approvalMode,
        int maxConsecutiveTransientErrors = 10)
    {
        OperatorPolicyModel policy = OperatorPolicyModel.Default with { ApprovalMode = approvalMode };
        return new ApprovalWaiter(
            CreateGateway(handler),
            policy,
            timeout: TimeSpan.FromMinutes(5),
            initialPollInterval: TimeSpan.FromMilliseconds(1),
            maxPollInterval: TimeSpan.FromMilliseconds(1),
            maxConsecutiveTransientErrors: maxConsecutiveTransientErrors,
            delay: (_, _) => Task.CompletedTask,
            timeProvider: TimeProvider.System);
    }

    private static BackendGateway CreateGateway(TestHttpMessageHandler handler)
    {
        var httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        return new BackendGateway(CreateBackendConfiguration(), httpClient);
    }

    private static TestHttpMessageHandler SequencedGetHandler(params HttpResponseMessage[] responses)
    {
        int index = 0;
        return new TestHttpMessageHandler(_ =>
        {
            var response = responses[Math.Min(index, responses.Length - 1)];
            index++;
            return response;
        });
    }

    private static HttpResponseMessage StatusOk(string operationId, string status)
        => TestHttpMessageHandler.JsonOk(new { operationId, status });

    private static BackendConfiguration CreateBackendConfiguration()
        => new(
            HonuaApiBaseUri: new Uri("http://localhost:8080/base"),
            OTelBaseUri: new Uri("http://localhost:4318/otel"),
            HonuaApiKey: null,
            OTelApiKey: null,
            HonuaReadinessPath: "healthz/ready",
            OTelHealthPath: "health",
            OTelLogsPath: "v1/logs/search",
            OTelMetricsPath: "v1/metrics/search",
            HonuaAdminErrorsPath: "api/v1/admin/observability/errors",
            HonuaAdminTelemetryPath: "api/v1/admin/observability/telemetry",
            HonuaMetricsHealthPath: "api/v1/metrics/health",
            HonuaMetricsPerformancePath: "api/v1/metrics/performance",
            HonuaMetricsDatabasePath: "api/v1/metrics/database",
            HonuaMetricsCachePath: "api/v1/metrics/cache",
            HonuaMetricsMemoryPath: "api/v1/metrics/memory",
            HonuaQueryCacheStatisticsPath: "api/v1/admin/performance/database/query-cache/statistics",
            HonuaAdminVersionPath: "api/v1/admin/version",
            HonuaAdminCapabilitiesPath: "api/v1/admin/capabilities",
            HonuaManifestExportPath: "api/v1/admin/manifest",
            HonuaManifestApplyPath: "api/v1/admin/manifest/apply",
            RequestTimeout: TimeSpan.FromSeconds(5));

    private sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        internal void Advance(TimeSpan delta) => _now += delta;
    }
}
