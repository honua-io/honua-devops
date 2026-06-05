using System.Text.Json;
using Honua.DevOps.Agent.Operations;
using Honua.DevOps.Agent.Operations.ConsoleBridge;
using Honua.DevOps.Agent.Operations.OperatorPolicy;
using OperatorPolicyModel = Honua.DevOps.Agent.Operations.OperatorPolicy.OperatorPolicy;

namespace Honua.DevOps.Agent.Tests;

public class SupportTicketConsoleViewTests
{
    [Fact]
    public async Task BuildSupportTicketConsoleView_ReadOnlyTriage_HasInactiveSessionAndNotEscalated()
    {
        using BackendGateway gateway = CreateGateway();
        HonuaOperationsToolkit toolkit = new(CreateRuntime(), gateway, ReadOnlyPolicy());

        OperationResponse response = await toolkit.BuildSupportTicketConsoleViewAsync(
            ticketId: "TICKET-300",
            severity: "medium",
            environment: "staging",
            symptoms: "intermittent latency",
            requestedAction: "diagnose",
            allowedAccessMode: "read-only",
            ttlMinutes: 45,
            rollbackExpected: false,
            attachedEvidence: "",
            cancellationToken: CancellationToken.None);

        SupportTicketConsoleView view = AssertView(response);
        Assert.Equal("read-only-triage", view.Posture);

        // No live session: read-only access never crosses the operator-scoped boundary.
        Assert.False(view.Session.Active);
        Assert.Equal("read-only", view.Session.AccessMode);
        Assert.Null(view.Session.ExpiresAt);
        Assert.Null(view.Session.EstablishedAt);
        Assert.True(view.Session.CustomerVisible);

        // Not escalated; rationale records the not-escalated posture without a trigger code.
        Assert.False(view.Escalation.Escalated);
        Assert.Equal("not-escalated", view.Escalation.Trigger);
        Assert.Empty(view.Escalation.RequiredApprovalContext);

        // Scorecard is projected with a stable pass/fail.
        Assert.Contains(view.Scorecard.OverallResult, new[] { "pass", "fail" });
        Assert.Equal(view.Scorecard.OverallResult, response.Status);
    }

    [Fact]
    public async Task BuildSupportTicketConsoleView_OperatorScoped_HasActiveSessionWithExpiryAndEscalationRationale()
    {
        using BackendGateway gateway = CreateGateway();
        HonuaOperationsToolkit toolkit = new(
            CreateRuntime(ExecutionMode.Execute, ExecutionTier.ExecuteLowerEnv),
            gateway,
            OperatorScopedPolicy());

        OperationResponse response = await toolkit.BuildSupportTicketConsoleViewAsync(
            ticketId: "TICKET-400",
            severity: "critical",
            environment: "prod",
            // FAULT-001: matches a write-capable remediation scenario.
            symptoms: "FATAL: password authentication failed for postgres user",
            requestedAction: "fix",
            allowedAccessMode: "operator-scoped",
            ttlMinutes: 30,
            rollbackExpected: true,
            attachedEvidence: "Npgsql.PostgresException in app logs",
            cancellationToken: CancellationToken.None);

        SupportTicketConsoleView view = AssertView(response);
        Assert.Equal("operator-scoped", view.Posture);

        // Live session: active, TTL min-clamped to the ticket's 30m, with an absolute expiry.
        Assert.True(view.Session.Active);
        Assert.Equal("operator-scoped", view.Session.AccessMode);
        Assert.Equal(30, view.Session.TtlMinutes);
        Assert.NotNull(view.Session.EstablishedAt);
        Assert.NotNull(view.Session.ExpiresAt);

        DateTimeOffset established = DateTimeOffset.Parse(view.Session.EstablishedAt!);
        DateTimeOffset expires = DateTimeOffset.Parse(view.Session.ExpiresAt!);
        Assert.Equal(30, (int)Math.Round((expires - established).TotalMinutes));

        // Escalation rationale: a matched write-capable fault is the coded trigger.
        Assert.True(view.Escalation.Escalated);
        Assert.Equal("matched-fault-write-remediation", view.Escalation.Trigger);
        Assert.Contains("write-capable", view.Escalation.Signal, StringComparison.Ordinal);
        Assert.Equal("rollback-prepared", view.Escalation.RollbackIntent);
        Assert.Contains(view.Escalation.RequiredApprovalContext, ctx => ctx == "approver-identity");
    }

    [Fact]
    public async Task BuildSupportTicketConsoleView_ProjectionIsInProcessOnly_NotSerialized()
    {
        using BackendGateway gateway = CreateGateway();
        HonuaOperationsToolkit toolkit = new(CreateRuntime(), gateway, ReadOnlyPolicy());

        OperationResponse response = await toolkit.BuildSupportTicketConsoleViewAsync(
            "TICKET-500", "low", "dev", "minor glitch", "diagnose", "read-only", 60, false, "",
            CancellationToken.None);

        // Typed projection available in-process...
        Assert.NotNull(response.ConsoleBridge);
        Assert.Equal("support-ticket-view", response.ConsoleBridge!.Kind);
        Assert.NotNull(response.ConsoleBridge.SupportTicket);

        // ...but excluded from the LLM-facing wire shape (JsonIgnore), like other projections.
        using JsonDocument document = JsonDocument.Parse(JsonSerializer.Serialize(response));
        Assert.False(document.RootElement.TryGetProperty("ConsoleBridge", out _));
    }

    [Fact]
    public async Task BuildSupportTicketConsoleView_SurfacesAuditReferenceForTicketScope()
    {
        using BackendGateway gateway = CreateGateway();
        HonuaOperationsToolkit toolkit = new(CreateRuntime(), gateway, ReadOnlyPolicy());

        OperationResponse response = await toolkit.BuildSupportTicketConsoleViewAsync(
            "TICKET-600", "high", "prod", "errors", "diagnose", "read-only", 60, false, "",
            CancellationToken.None);

        SupportTicketConsoleView view = AssertView(response);
        EvidenceRef audit = Assert.Single(view.AuditReferences);
        Assert.Equal("audit-journal", audit.Type);
        Assert.Equal("audit-scope:support-triage:support-triage:TICKET-600", audit.RawRef);
    }

    private static SupportTicketConsoleView AssertView(OperationResponse response)
    {
        Assert.NotNull(response.ConsoleBridge);
        Assert.Equal("support-ticket-view", response.ConsoleBridge!.Kind);
        Assert.NotNull(response.ConsoleBridge.SupportTicket);
        return response.ConsoleBridge.SupportTicket!;
    }

    private static BackendGateway CreateGateway()
    {
        TestHttpMessageHandler handler = new(_ => TestHttpMessageHandler.JsonOk(new { status = "ok" }));
        HttpClient httpClient = new(handler) { Timeout = TimeSpan.FromSeconds(5) };
        return new BackendGateway(CreateBackendConfiguration(), httpClient);
    }

    private static OperationRuntime CreateRuntime(
        ExecutionMode mode = ExecutionMode.Plan,
        ExecutionTier executionTier = ExecutionTier.Plan)
    {
        return new OperationRuntime(
            mode,
            executionTier,
            "honua-gitops",
            AllowedEnvironments: ["dev", "staging", "prod"],
            TerraformRepository: "https://github.com/honua-io/honua-terraform",
            TerraformRef: "main",
            TerraformLocalPath: "/tmp/honua-terraform",
            TerraformDeploymentTargets: ["eks", "aks"]);
    }

    private static OperatorPolicyModel ReadOnlyPolicy()
    {
        return new OperatorPolicyModel(
            ApprovalMode.PrFirst,
            "stdout-evidence",
            new SupportSessionPolicy(SupportSessionAccess.ReadOnly, 60, true),
            BreakGlassPostActionReviewRequired: true);
    }

    private static OperatorPolicyModel OperatorScopedPolicy()
    {
        return new OperatorPolicyModel(
            ApprovalMode.DirectAllowed,
            "stdout-evidence",
            new SupportSessionPolicy(SupportSessionAccess.OperatorScoped, 60, true),
            BreakGlassPostActionReviewRequired: true);
    }

    private static BackendConfiguration CreateBackendConfiguration()
    {
        return new BackendConfiguration(
            HonuaApiBaseUri: new Uri("http://localhost:8080"),
            OTelBaseUri: new Uri("http://localhost:4318"),
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
    }
}
