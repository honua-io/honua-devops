using Honua.DevOps.Agent.Operations;
using Honua.DevOps.Agent.Operations.GuidedFix;
using Honua.DevOps.Agent.Operations.OperatorPolicy;
using OperatorPolicyModel = Honua.DevOps.Agent.Operations.OperatorPolicy.OperatorPolicy;

namespace Honua.DevOps.Agent.Tests;

public class GuidedFixWorkflowTests
{
    [Fact]
    public async Task TriageSupportTicketAsync_DefaultsToReadOnlyTriageInPlanMode()
    {
        using BackendGateway gateway = CreateGateway(_ => TestHttpMessageHandler.JsonOk(new { status = "ok" }));
        HonuaOperationsToolkit toolkit = new(CreateRuntime(), gateway);

        OperationResponse response = await toolkit.TriageSupportTicketAsync(
            ticketId: "TICKET-100",
            severity: "high",
            environment: "prod",
            symptoms: "Service returning 500 errors intermittently",
            requestedAction: "diagnose",
            allowedAccessMode: "read-only",
            ttlMinutes: 30,
            rollbackExpected: false,
            attachedEvidence: "Error logs attached",
            cancellationToken: CancellationToken.None);

        Assert.Equal("read-only-triage", response.Status);
        Assert.NotNull(response.Evidence);
        Assert.True(response.Evidence!.DryRun);
        Assert.Equal("read-only-triage", response.Evidence.PolicyGate);
        Assert.Contains(response.Findings, finding => finding.Contains("TICKET-100", StringComparison.Ordinal));
        Assert.Contains(response.Findings, finding => finding.Contains("high", StringComparison.Ordinal));
        Assert.Contains(response.Findings, finding => finding.Contains("read-only-triage", StringComparison.Ordinal));
        Assert.Contains(response.Evidence.RequiredChecks, check => check == "ticket-context");
        Assert.Contains(response.Evidence.RequiredChecks, check => check == "diagnosis-evidence");
        Assert.Contains(response.Evidence.RequiredChecks, check => check == "access-mode-record");
    }

    [Fact]
    public async Task TriageSupportTicketAsync_ResolvesGuidedFixWhenSessionEnabled()
    {
        using BackendGateway gateway = CreateGateway(_ => TestHttpMessageHandler.JsonOk(new { status = "ok" }));
        HonuaOperationsToolkit toolkit = new(
            CreateRuntime(mode: ExecutionMode.Execute, executionTier: ExecutionTier.ExecuteLowerEnv),
            gateway,
            GuidedFixPolicy(SupportSessionAccess.ReadOnly));

        OperationResponse response = await toolkit.TriageSupportTicketAsync(
            ticketId: "TICKET-200",
            severity: "medium",
            environment: "staging",
            symptoms: "Cache hit ratio degraded",
            requestedAction: "fix",
            allowedAccessMode: "guided-fix",
            ttlMinutes: 60,
            rollbackExpected: false,
            attachedEvidence: "",
            cancellationToken: CancellationToken.None);

        Assert.Equal("guided-fix", response.Status);
        Assert.NotNull(response.Evidence);
        Assert.True(response.Evidence!.DryRun);
        Assert.Equal("guided-fix", response.Evidence.PolicyGate);
        Assert.Contains(response.Findings, finding => finding.Contains("guided-fix", StringComparison.Ordinal));
        Assert.Contains(response.Findings, finding => finding.Contains("Missing:", StringComparison.Ordinal));
        Assert.Contains(response.Actions, action => action.Contains("guided-fix", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TriageSupportTicketAsync_ResolvesOperatorScopedWithEscalation()
    {
        using BackendGateway gateway = CreateGateway(_ => TestHttpMessageHandler.JsonOk(new { status = "ok" }));
        HonuaOperationsToolkit toolkit = new(
            CreateRuntime(mode: ExecutionMode.Execute, executionTier: ExecutionTier.ExecuteLowerEnv),
            gateway,
            GuidedFixPolicy(SupportSessionAccess.OperatorScoped));

        OperationResponse response = await toolkit.TriageSupportTicketAsync(
            ticketId: "TICKET-300",
            severity: "critical",
            environment: "staging",
            symptoms: "Database unreachable, all requests failing",
            requestedAction: "emergency fix",
            allowedAccessMode: "operator-scoped",
            ttlMinutes: 15,
            rollbackExpected: true,
            attachedEvidence: "Connection timeout logs",
            cancellationToken: CancellationToken.None);

        Assert.Equal("operator-scoped", response.Status);
        Assert.NotNull(response.Evidence);
        Assert.False(response.Evidence!.DryRun);
        Assert.Equal("operator-scoped", response.Evidence.PolicyGate);
        Assert.Contains(response.Actions, action => action.Contains("Escalation requires approval", StringComparison.Ordinal));
        Assert.Contains(response.Actions, action => action.Contains("Rollback intent", StringComparison.Ordinal));
        Assert.Contains(response.Evidence.RequiredChecks, check => check.StartsWith("ticket-reference:", StringComparison.Ordinal));
        Assert.Contains(response.Evidence.RequiredChecks, check => check == "rollback-readiness");
        Assert.Contains(response.Risks, risk => risk.Contains("operator-scoped", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task TriageSupportTicketAsync_ForcesReadOnlyWhenSessionDisabled()
    {
        using BackendGateway gateway = CreateGateway(_ => TestHttpMessageHandler.JsonOk(new { status = "ok" }));
        HonuaOperationsToolkit toolkit = new(
            CreateRuntime(mode: ExecutionMode.Execute, executionTier: ExecutionTier.ExecuteLowerEnv),
            gateway,
            OperatorPolicyModel.Default);

        OperationResponse response = await toolkit.TriageSupportTicketAsync(
            ticketId: "TICKET-400",
            severity: "high",
            environment: "dev",
            symptoms: "Ingress returning 502",
            requestedAction: "fix",
            allowedAccessMode: "operator-scoped",
            ttlMinutes: 30,
            rollbackExpected: false,
            attachedEvidence: "ALB logs",
            cancellationToken: CancellationToken.None);

        Assert.Equal("read-only-triage", response.Status);
        Assert.NotNull(response.Evidence);
        Assert.True(response.Evidence!.DryRun);
    }

    [Fact]
    public async Task TriageSupportTicketAsync_HandlesBackendFailure()
    {
        using BackendGateway gateway = CreateGateway(_ =>
            new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError));
        HonuaOperationsToolkit toolkit = new(CreateRuntime(), gateway);

        OperationResponse response = await toolkit.TriageSupportTicketAsync(
            ticketId: "TICKET-500",
            severity: "critical",
            environment: "prod",
            symptoms: "Complete outage",
            requestedAction: "diagnose",
            allowedAccessMode: "read-only",
            ttlMinutes: 60,
            rollbackExpected: true,
            attachedEvidence: "",
            cancellationToken: CancellationToken.None);

        Assert.Equal("backend-error", response.Status);
        Assert.NotNull(response.Evidence);
        Assert.Contains(response.Findings, finding => finding.Contains("low", StringComparison.Ordinal));
        Assert.Contains(response.Findings, finding => finding.Contains("Missing:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TriageSupportTicketAsync_PreservesEmptySymptomsForMissingEvidence()
    {
        using BackendGateway gateway = CreateGateway(_ => TestHttpMessageHandler.JsonOk(new { status = "ok" }));
        HonuaOperationsToolkit toolkit = new(CreateRuntime(), gateway);

        OperationResponse response = await toolkit.TriageSupportTicketAsync(
            ticketId: "TICKET-NO-SYMPTOMS",
            severity: "medium",
            environment: "prod",
            symptoms: "",
            requestedAction: "diagnose",
            allowedAccessMode: "read-only",
            ttlMinutes: 30,
            rollbackExpected: false,
            attachedEvidence: "",
            cancellationToken: CancellationToken.None);

        Assert.Contains(response.Findings, finding =>
            finding.Contains("Symptom description is empty", StringComparison.Ordinal));
        Assert.Contains(response.Findings, finding =>
            finding.Contains("Recommended next action: request-more-evidence", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TriageSupportTicketAsync_RejectsInvalidSeverity()
    {
        using BackendGateway gateway = CreateGateway(_ => TestHttpMessageHandler.JsonOk(new { status = "ok" }));
        HonuaOperationsToolkit toolkit = new(CreateRuntime(), gateway);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => toolkit.TriageSupportTicketAsync(
                ticketId: "TICKET-600",
                severity: "catastrophic",
                environment: "dev",
                symptoms: "test",
                requestedAction: "diagnose",
                allowedAccessMode: "read-only",
                ttlMinutes: 30,
                rollbackExpected: false,
                attachedEvidence: "",
                cancellationToken: CancellationToken.None));
    }

    [Fact]
    public async Task TriageSupportTicketAsync_RejectsEmptyTicketId()
    {
        using BackendGateway gateway = CreateGateway(_ => TestHttpMessageHandler.JsonOk(new { status = "ok" }));
        HonuaOperationsToolkit toolkit = new(CreateRuntime(), gateway);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => toolkit.TriageSupportTicketAsync(
                ticketId: "",
                severity: "medium",
                environment: "dev",
                symptoms: "test",
                requestedAction: "diagnose",
                allowedAccessMode: "read-only",
                ttlMinutes: 30,
                rollbackExpected: false,
                attachedEvidence: "",
                cancellationToken: CancellationToken.None));
    }

    [Fact]
    public async Task TriageSupportTicketAsync_CapsEscalationTtlAtPolicyLimit()
    {
        using BackendGateway gateway = CreateGateway(_ => TestHttpMessageHandler.JsonOk(new { status = "ok" }));
        OperatorPolicyModel policy = new(
            ApprovalMode.DirectAllowed,
            "audit://test",
            new SupportSessionPolicy(SupportSessionAccess.OperatorScoped, 30, true),
            BreakGlassPostActionReviewRequired: true);
        HonuaOperationsToolkit toolkit = new(
            CreateRuntime(mode: ExecutionMode.Execute, executionTier: ExecutionTier.ExecuteLowerEnv),
            gateway,
            policy);

        OperationResponse response = await toolkit.TriageSupportTicketAsync(
            ticketId: "TICKET-700",
            severity: "high",
            environment: "staging",
            symptoms: "Memory pressure causing OOM kills",
            requestedAction: "fix",
            allowedAccessMode: "operator-scoped",
            ttlMinutes: 120,
            rollbackExpected: false,
            attachedEvidence: "OOM logs",
            cancellationToken: CancellationToken.None);

        Assert.Equal("operator-scoped", response.Status);
        Assert.Contains(response.Actions, action => action.Contains("TTL 30m", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TriageSupportTicketAsync_EmitsRollbackReadinessWhenExpected()
    {
        using BackendGateway gateway = CreateGateway(_ => TestHttpMessageHandler.JsonOk(new { status = "ok" }));
        HonuaOperationsToolkit toolkit = new(CreateRuntime(), gateway);

        OperationResponse response = await toolkit.TriageSupportTicketAsync(
            ticketId: "TICKET-800",
            severity: "high",
            environment: "prod",
            symptoms: "Bad deployment",
            requestedAction: "rollback",
            allowedAccessMode: "read-only",
            ttlMinutes: 30,
            rollbackExpected: true,
            attachedEvidence: "Deployment logs",
            cancellationToken: CancellationToken.None);

        Assert.NotNull(response.Evidence);
        Assert.Contains(response.Evidence!.RequiredChecks, check => check == "rollback-readiness");
        Assert.Contains(response.ValidationChecks, check =>
            check.Contains("rollback", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("p1", "critical")]
    [InlineData("sev2", "high")]
    [InlineData("medium", "medium")]
    [InlineData("p4", "low")]
    public void SupportSeverityParse_AcceptsAliases(string input, string expectedConfigValue)
    {
        SupportSeverity severity = SupportSeverityExtensions.Parse(input);
        Assert.Equal(expectedConfigValue, severity.ToConfigValue());
    }

    [Fact]
    public void GuidedFixPlanner_ReadOnlyTriageWhenPlanMode()
    {
        SupportTicket ticket = CreateTicket(allowedAccessMode: "operator-scoped");
        BackendCallResult backend = CreateSuccessBackendResult();

        GuidedFixResult result = GuidedFixPlanner.Build(
            ticket,
            GuidedFixPolicy(SupportSessionAccess.OperatorScoped),
            ExecutionMode.Plan,
            ExecutionTier.Plan,
            backend);

        Assert.Equal(GuidedFixMode.ReadOnlyTriage, result.Mode);
    }

    [Fact]
    public void GuidedFixPlanner_HighConfidenceWhenFaultMatched()
    {
        SupportTicket ticket = CreateTicket(
            symptoms: "CrashLoopBackOff after deployment",
            attachedEvidence: "ImagePullBackOff. Back-off restarting failed container");
        BackendCallResult backend = CreateSuccessBackendResult();

        GuidedFixResult result = GuidedFixPlanner.Build(
            ticket,
            OperatorPolicyModel.Default,
            ExecutionMode.Plan,
            ExecutionTier.Plan,
            backend);

        Assert.Equal("high", result.Confidence);
        Assert.NotNull(result.MatchedFault);
        Assert.Equal("rollout-readiness", result.MatchedFault!.FaultCategory);
    }

    [Fact]
    public void GuidedFixPlanner_LowConfidenceWithNoSymptomsAndBackendDown()
    {
        SupportTicket ticket = CreateTicket(symptoms: "", attachedEvidence: "");
        BackendCallResult backend = CreateFailedBackendResult();

        GuidedFixResult result = GuidedFixPlanner.Build(
            ticket,
            OperatorPolicyModel.Default,
            ExecutionMode.Plan,
            ExecutionTier.Plan,
            backend);

        Assert.Equal("low", result.Confidence);
        Assert.True(result.MissingEvidence.Count >= 2);
        Assert.Equal("request-more-evidence", result.RecommendedNextAction);
    }

    [Fact]
    public void GuidedFixPlanner_EscalationIncludesApprovalContext()
    {
        SupportTicket ticket = CreateTicket(
            allowedAccessMode: "operator-scoped",
            rollbackExpected: true,
            ttlMinutes: 15);
        OperatorPolicyModel policy = GuidedFixPolicy(SupportSessionAccess.OperatorScoped);
        BackendCallResult backend = CreateSuccessBackendResult();

        GuidedFixResult result = GuidedFixPlanner.Build(
            ticket,
            policy,
            ExecutionMode.Execute,
            ExecutionTier.ExecuteLowerEnv,
            backend);

        Assert.Equal(GuidedFixMode.OperatorScoped, result.Mode);
        Assert.NotNull(result.Escalation);
        Assert.Contains(result.Escalation!.RequiredApprovalContext, ctx => ctx == "approver-identity");
        Assert.Contains(result.Escalation.RequiredApprovalContext, ctx => ctx.StartsWith("ticket-reference:", StringComparison.Ordinal));
        Assert.Contains(result.Escalation.RequiredApprovalContext, ctx => ctx == "operator-justification");
        Assert.Equal("rollback-prepared", result.Escalation.RollbackIntent);
        Assert.Equal(15, result.Escalation.TtlMinutes);
    }

    [Fact]
    public async Task TriageSupportTicketAsync_PrFirstBlocksOperatorScopedEscalation()
    {
        using BackendGateway gateway = CreateGateway(_ => TestHttpMessageHandler.JsonOk(new { status = "ok" }));
        OperatorPolicyModel prFirstPolicy = new(
            ApprovalMode.PrFirst,
            "stdout-evidence",
            new SupportSessionPolicy(SupportSessionAccess.OperatorScoped, 60, true),
            BreakGlassPostActionReviewRequired: true);
        HonuaOperationsToolkit toolkit = new(
            CreateRuntime(mode: ExecutionMode.Execute, executionTier: ExecutionTier.ExecuteLowerEnv),
            gateway,
            prFirstPolicy);

        OperationResponse response = await toolkit.TriageSupportTicketAsync(
            ticketId: "TICKET-PRFIRST",
            severity: "critical",
            environment: "staging",
            symptoms: "DB unreachable",
            requestedAction: "fix",
            allowedAccessMode: "operator-scoped",
            ttlMinutes: 30,
            rollbackExpected: false,
            attachedEvidence: "connection refused",
            cancellationToken: CancellationToken.None);

        Assert.Equal("guided-fix", response.Status);
        Assert.True(response.Evidence!.DryRun);
    }

    [Fact]
    public async Task TriageSupportTicketAsync_BreakGlassOnlyBlocksOperatorScopedUnlessBGTier()
    {
        using BackendGateway gateway = CreateGateway(_ => TestHttpMessageHandler.JsonOk(new { status = "ok" }));
        OperatorPolicyModel bgOnlyPolicy = new(
            ApprovalMode.BreakGlassOnly,
            "stdout-evidence",
            new SupportSessionPolicy(SupportSessionAccess.OperatorScoped, 60, true),
            BreakGlassPostActionReviewRequired: true);
        HonuaOperationsToolkit toolkit = new(
            CreateRuntime(mode: ExecutionMode.Execute, executionTier: ExecutionTier.ExecuteLowerEnv),
            gateway,
            bgOnlyPolicy);

        OperationResponse response = await toolkit.TriageSupportTicketAsync(
            ticketId: "TICKET-BGONLY",
            severity: "critical",
            environment: "staging",
            symptoms: "Complete outage",
            requestedAction: "fix",
            allowedAccessMode: "operator-scoped",
            ttlMinutes: 15,
            rollbackExpected: true,
            attachedEvidence: "all endpoints down",
            cancellationToken: CancellationToken.None);

        Assert.Equal("guided-fix", response.Status);
        Assert.True(response.Evidence!.DryRun);
    }

    [Fact]
    public async Task TriageSupportTicketAsync_ReadOnlyTicketRemainsReadOnlyUnderPrFirstPolicy()
    {
        using BackendGateway gateway = CreateGateway(_ => TestHttpMessageHandler.JsonOk(new { status = "ok" }));
        OperatorPolicyModel prFirstPolicy = new(
            ApprovalMode.PrFirst,
            "stdout-evidence",
            new SupportSessionPolicy(SupportSessionAccess.OperatorScoped, 60, true),
            BreakGlassPostActionReviewRequired: true);
        HonuaOperationsToolkit toolkit = new(
            CreateRuntime(mode: ExecutionMode.Execute, executionTier: ExecutionTier.ExecuteLowerEnv),
            gateway,
            prFirstPolicy);

        OperationResponse response = await toolkit.TriageSupportTicketAsync(
            ticketId: "TICKET-READONLY",
            severity: "high",
            environment: "staging",
            symptoms: "Service returning 500 after deployment",
            requestedAction: "diagnose",
            allowedAccessMode: "read-only",
            ttlMinutes: 30,
            rollbackExpected: false,
            attachedEvidence: "health endpoint failing",
            cancellationToken: CancellationToken.None);

        Assert.Equal("read-only-triage", response.Status);
        Assert.True(response.Evidence!.DryRun);
    }

    [Fact]
    public async Task TriageSupportTicketAsync_ProposeTierForcesReadOnlyTriage()
    {
        using BackendGateway gateway = CreateGateway(_ => TestHttpMessageHandler.JsonOk(new { status = "ok" }));
        HonuaOperationsToolkit toolkit = new(
            CreateRuntime(mode: ExecutionMode.Execute, executionTier: ExecutionTier.Propose),
            gateway,
            GuidedFixPolicy(SupportSessionAccess.OperatorScoped));

        OperationResponse response = await toolkit.TriageSupportTicketAsync(
            ticketId: "TICKET-PROPOSE",
            severity: "high",
            environment: "staging",
            symptoms: "Latency regression",
            requestedAction: "fix",
            allowedAccessMode: "operator-scoped",
            ttlMinutes: 60,
            rollbackExpected: false,
            attachedEvidence: "P95 latency doubled",
            cancellationToken: CancellationToken.None);

        Assert.Equal("read-only-triage", response.Status);
        Assert.True(response.Evidence!.DryRun);
    }

    [Fact]
    public async Task TriageSupportTicketAsync_EvidenceUsesEffectiveRequestedTtl()
    {
        using BackendGateway gateway = CreateGateway(_ => TestHttpMessageHandler.JsonOk(new { status = "ok" }));
        OperatorPolicyModel policy = new(
            ApprovalMode.DirectAllowed,
            "audit://test",
            new SupportSessionPolicy(SupportSessionAccess.OperatorScoped, 30, true),
            BreakGlassPostActionReviewRequired: true);
        HonuaOperationsToolkit toolkit = new(
            CreateRuntime(mode: ExecutionMode.Execute, executionTier: ExecutionTier.ExecuteLowerEnv),
            gateway,
            policy);

        OperationResponse response = await toolkit.TriageSupportTicketAsync(
            ticketId: "TICKET-TTL",
            severity: "critical",
            environment: "staging",
            symptoms: "DB outage after secret rotation",
            requestedAction: "fix",
            allowedAccessMode: "operator-scoped",
            ttlMinutes: 15,
            rollbackExpected: true,
            attachedEvidence: "FATAL: password authentication failed. Npgsql.PostgresException. connection refused",
            cancellationToken: CancellationToken.None);

        Assert.NotNull(response.Evidence);
        Assert.Equal(15, response.Evidence!.SupportSessionTtlMinutes);
    }

    [Fact]
    public void DiagnoseFromEvidence_RejectsAmbiguousGenericSymptoms()
    {
        string[] ambiguousInputs =
        [
            "Service returning 500 errors intermittently",
            "Deployment failed",
            "Database errors",
            "Something is wrong with the service",
            "Performance is slow",
            "Service unhealthy",
            "Latency increased"
        ];

        foreach (string input in ambiguousInputs)
        {
            SupportTicket ticket = CreateTicket(symptoms: input, attachedEvidence: "");
            FaultMatch? match = GuidedFixPlanner.DiagnoseFromEvidence(ticket);

            Assert.True(match is null,
                $"Generic symptom '{input}' should not match any scenario but matched: {match?.ScenarioName}");
        }
    }

    [Fact]
    public void GuidedFixPlanner_BreakGlassOnlyReachesExecuteInBreakGlassTier()
    {
        SupportTicket ticket = CreateTicket(
            allowedAccessMode: "operator-scoped",
            symptoms: "Complete DB outage after secret rotation",
            attachedEvidence: "FATAL: password authentication failed. Npgsql.PostgresException. connection refused");
        OperatorPolicyModel bgPolicy = new(
            ApprovalMode.BreakGlassOnly,
            "audit://emergency",
            new SupportSessionPolicy(SupportSessionAccess.OperatorScoped, 30, true),
            BreakGlassPostActionReviewRequired: true);
        BackendCallResult backend = CreateSuccessBackendResult();

        GuidedFixResult result = GuidedFixPlanner.Build(
            ticket,
            bgPolicy,
            ExecutionMode.Execute,
            ExecutionTier.BreakGlass,
            backend);

        Assert.Equal(GuidedFixMode.OperatorScoped, result.Mode);
        Assert.NotNull(result.MatchedFault);
        Assert.Equal("execute-remediation", result.RecommendedNextAction);
    }

    [Fact]
    public void DiagnoseFromEvidence_MatchesPostgresPasswordFromLogs()
    {
        SupportTicket ticket = CreateTicket(
            symptoms: "Service returning 500 errors after secret rotation",
            attachedEvidence: "FATAL: password authentication failed for user \"honua_app\". Npgsql.PostgresException");

        FaultMatch? match = GuidedFixPlanner.DiagnoseFromEvidence(ticket);

        Assert.NotNull(match);
        Assert.Equal("FAULT-001", match!.ScenarioId);
        Assert.Equal("secret-credential", match.FaultCategory);
        Assert.True(match.MatchScore > 0);
        Assert.Contains(match.RemediationSteps, step => step.Contains("Restore previous secret version", StringComparison.Ordinal));
    }

    [Fact]
    public void DiagnoseFromEvidence_MatchesCrashLoopFromKubernetesEvents()
    {
        SupportTicket ticket = CreateTicket(
            symptoms: "Pods in CrashLoopBackOff after deployment, service unavailable",
            attachedEvidence: "ImagePullBackOff: failed to pull image myregistry/honua:bad-tag. Back-off restarting failed container.");

        FaultMatch? match = GuidedFixPlanner.DiagnoseFromEvidence(ticket);

        Assert.NotNull(match);
        Assert.Equal("FAULT-010", match!.ScenarioId);
        Assert.Equal("rollout-readiness", match.FaultCategory);
        Assert.Contains(match.RemediationSteps, step => step.Contains("kubectl rollout undo", StringComparison.Ordinal));
    }

    [Fact]
    public void DiagnoseFromEvidence_MatchesRedisAuthFailure()
    {
        SupportTicket ticket = CreateTicket(
            symptoms: "Cache misses spiking, elevated latency on tile endpoints",
            attachedEvidence: "StackExchange.Redis.RedisConnectionException: NOAUTH Authentication required");

        FaultMatch? match = GuidedFixPlanner.DiagnoseFromEvidence(ticket);

        Assert.NotNull(match);
        Assert.Equal("FAULT-003", match!.ScenarioId);
        Assert.Equal("redis-connectivity", match.FaultCategory);
        Assert.Contains(match.RemediationSteps, step => step.Contains("Redis auth token", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DiagnoseFromEvidence_MatchesMissingSpatialIndex()
    {
        SupportTicket ticket = CreateTicket(
            symptoms: "Map tile requests extremely slow, P95 latency 50x normal",
            attachedEvidence: "Seq Scan on large table parcels. query plan shows no index usage. statement timeout after 30s");

        FaultMatch? match = GuidedFixPlanner.DiagnoseFromEvidence(ticket);

        Assert.NotNull(match);
        Assert.Equal("FAULT-013", match!.ScenarioId);
        Assert.Equal("postgres-performance", match.FaultCategory);
        Assert.Contains(match.RemediationSteps, step => step.Contains("spatial index", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DiagnoseFromEvidence_MatchesOidcFailure()
    {
        SupportTicket ticket = CreateTicket(
            symptoms: "All authenticated requests return 401, admin UI login broken",
            attachedEvidence: "IDX10205: Issuer validation failed. invalid_token. audience validation failed");

        FaultMatch? match = GuidedFixPlanner.DiagnoseFromEvidence(ticket);

        Assert.NotNull(match);
        Assert.Equal("FAULT-009", match!.ScenarioId);
        Assert.Equal("oidc-auth", match.FaultCategory);
    }

    [Fact]
    public void DiagnoseFromEvidence_MatchesGitOpsDrift()
    {
        SupportTicket ticket = CreateTicket(
            symptoms: "Unexpected behavior after manual config change, drift alerts firing",
            attachedEvidence: "config value differs from manifest. drift detected on service roads-api in staging");

        FaultMatch? match = GuidedFixPlanner.DiagnoseFromEvidence(ticket);

        Assert.NotNull(match);
        Assert.Equal("FAULT-016", match!.ScenarioId);
        Assert.Equal("gitops-drift", match.FaultCategory);
        Assert.Contains(match.RemediationSteps, step => step.Contains("honua-gitops sync", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DiagnoseFromEvidence_ReturnsNullWhenNoMatch()
    {
        SupportTicket ticket = CreateTicket(
            symptoms: "Billing inquiry",
            attachedEvidence: "");

        FaultMatch? match = GuidedFixPlanner.DiagnoseFromEvidence(ticket);

        Assert.Null(match);
    }

    [Fact]
    public async Task TriageSupportTicketAsync_DiagnosesAndSuggestsSpecificFix()
    {
        using BackendGateway gateway = CreateGateway(_ => TestHttpMessageHandler.JsonOk(new { status = "ok" }));
        HonuaOperationsToolkit toolkit = new(CreateRuntime(), gateway);

        OperationResponse response = await toolkit.TriageSupportTicketAsync(
            ticketId: "TICKET-DIAG-1",
            severity: "critical",
            environment: "prod",
            symptoms: "Service returning 500 errors, all DB queries failing",
            requestedAction: "diagnose",
            allowedAccessMode: "read-only",
            ttlMinutes: 30,
            rollbackExpected: true,
            attachedEvidence: "FATAL: password authentication failed for user honua. Npgsql.PostgresException. connection refused",
            cancellationToken: CancellationToken.None);

        Assert.Contains(response.Findings, f => f.Contains("Matched fault:", StringComparison.Ordinal));
        Assert.Contains(response.Findings, f => f.Contains("secret-credential", StringComparison.Ordinal));
        Assert.Contains(response.Findings, f => f.Contains("Rollback path:", StringComparison.Ordinal));
        Assert.Contains(response.Actions, a => a.Contains("Restore previous secret version", StringComparison.Ordinal));
        Assert.Contains(response.Findings, f => f.Contains("high", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TriageSupportTicketAsync_ExecutesModeShowsExecutionSteps()
    {
        using BackendGateway gateway = CreateGateway(_ => TestHttpMessageHandler.JsonOk(new { status = "ok" }));
        HonuaOperationsToolkit toolkit = new(
            CreateRuntime(mode: ExecutionMode.Execute, executionTier: ExecutionTier.ExecuteLowerEnv),
            gateway,
            GuidedFixPolicy(SupportSessionAccess.OperatorScoped));

        OperationResponse response = await toolkit.TriageSupportTicketAsync(
            ticketId: "TICKET-EXEC-1",
            severity: "critical",
            environment: "staging",
            symptoms: "CrashLoopBackOff after bad deployment",
            requestedAction: "fix",
            allowedAccessMode: "operator-scoped",
            ttlMinutes: 15,
            rollbackExpected: true,
            attachedEvidence: "ImagePullBackOff. Back-off restarting failed container. pod_restarts increases",
            cancellationToken: CancellationToken.None);

        Assert.Equal("operator-scoped", response.Status);
        Assert.Contains(response.Actions, a => a.Contains("EXECUTION MODE", StringComparison.Ordinal));
        Assert.Contains(response.Actions, a => a.Contains("kubectl rollout undo", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(response.Findings, f => f.Contains("Matched fault:", StringComparison.Ordinal));
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

    private static BackendGateway CreateGateway(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        TestHttpMessageHandler handler = new(responder);
        HttpClient httpClient = new(handler) { Timeout = TimeSpan.FromSeconds(5) };
        return new BackendGateway(CreateBackendConfiguration(), httpClient);
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

    private static OperatorPolicyModel GuidedFixPolicy(SupportSessionAccess access)
    {
        return new OperatorPolicyModel(
            ApprovalMode.DirectAllowed,
            "stdout-evidence",
            new SupportSessionPolicy(access, 60, true),
            BreakGlassPostActionReviewRequired: true);
    }

    private static SupportTicket CreateTicket(
        string ticketId = "TEST-001",
        string severity = "high",
        string environment = "staging",
        string symptoms = "Service errors",
        string requestedAction = "diagnose",
        string allowedAccessMode = "read-only",
        int ttlMinutes = 60,
        bool rollbackExpected = false,
        string attachedEvidence = "logs attached")
    {
        return new SupportTicket(
            TicketId: ticketId,
            Service: "support-triage",
            Severity: SupportSeverityExtensions.Parse(severity),
            Environment: environment,
            Symptoms: symptoms,
            RequestedAction: requestedAction,
            AllowedAccessMode: allowedAccessMode,
            TtlMinutes: ttlMinutes,
            RollbackExpected: rollbackExpected,
            AttachedEvidence: attachedEvidence);
    }

    private static BackendCallResult CreateSuccessBackendResult()
    {
        return new BackendCallResult(
            IsSuccess: true,
            Endpoint: "http://localhost:8080/api/v1/admin/troubleshoot",
            Detail: "ok",
            PayloadPreview: "{}");
    }

    private static BackendCallResult CreateFailedBackendResult()
    {
        return new BackendCallResult(
            IsSuccess: false,
            Endpoint: "http://localhost:8080/api/v1/admin/troubleshoot",
            Detail: "connection refused",
            PayloadPreview: "");
    }
}
