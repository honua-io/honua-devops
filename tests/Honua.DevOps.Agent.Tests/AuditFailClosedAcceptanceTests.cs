using System.Net.Http;
using System.Text.Json;

using Honua.DevOps.Agent.Operations;
using Honua.DevOps.Agent.Operations.Actuation;
using Honua.DevOps.Agent.Operations.Audit;
using Honua.DevOps.Agent.Operations.GitOps;
using Honua.DevOps.Agent.Operations.Observability;
using OperatorPolicyModel = Honua.DevOps.Agent.Operations.OperatorPolicy.OperatorPolicy;

namespace Honua.DevOps.Agent.Tests;

/// <summary>
/// Executable denominator for honua-devops#179. These tests drive the actual GitOps
/// gateway executor and the actual audit emitter; the fault sink is only the injected
/// dependency whose durability is under test.
/// </summary>
public sealed class AuditFailClosedAcceptanceTests
{
    [Theory]
    [InlineData("audit parent unwritable")]
    [InlineData("audit append failed")]
    [InlineData("audit flush failed")]
    public async Task GatewayMutation_RefusesBeforeAnyBackendCall_WhenDurabilityProbeFails(string failure)
    {
        TestHttpMessageHandler handler = new(_ => TestHttpMessageHandler.JsonOk(new { status = "ok" }));
        using BackendGateway gateway = ProvisioningSubstrateFixtures.CreateGateway(handler);
        FaultAuditSink sink = new(probeFailure: failure);
        ActuationSpine spine = new(
            CreateRuntime(),
            ProvisioningSubstrateFixtures.DirectAllowedPolicy() with { AuditHookTarget = sink.Target },
            sink,
            NewRecoveryStore(out string recoveryRoot));

        try
        {
            GitOpsExecutor executor = new(CreateRuntime(), gateway, ProvisioningSubstrateFixtures.DirectAllowedPolicy(), spine: spine);

            GitOpsExecutionResult result = await executor.ExecuteSyncAsync(
                desiredRevision: "release/2026.03",
                currentRevision: null,
                reason: "acceptance",
                idempotencyKey: "honua-devops:acceptance:gateway:preflight",
                correlationId: "honua-devops:acceptance",
                priority: "normal",
                parameters: new Dictionary<string, string>
                {
                    ["service"] = "roads-api",
                    ["environments"] = "dev",
                    ["action"] = "sync"
                },
                authorizationDryRun: false,
                policyGate: "lower-env-execution",
                CancellationToken.None);

            Assert.Equal(GitOpsExecutionStatus.ContractUnavailable, result.Status);
            Assert.Contains("audit-sink-unavailable", result.BlockingReasons);
            Assert.Empty(handler.CapturedRequests);
            Assert.Empty(Directory.Exists(recoveryRoot)
                ? Directory.EnumerateFileSystemEntries(recoveryRoot)
                : []);
        }
        finally
        {
            if (Directory.Exists(recoveryRoot))
            {
                Directory.Delete(recoveryRoot, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData("append")]
    [InlineData("flush")]
    public async Task GatewayMutation_AuditFailureAfterAcknowledgement_WritesRecoveryAndRestartRefusesDuplicate(string failurePoint)
    {
        TestHttpMessageHandler handler = new(request =>
        {
            string path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Post && path.EndsWith("/deploy/operations", StringComparison.Ordinal))
            {
                return TestHttpMessageHandler.JsonOk(new { operationId = "op-179", status = "Planned" });
            }

            if (request.Method == HttpMethod.Post && path.EndsWith("/submit", StringComparison.Ordinal))
            {
                return TestHttpMessageHandler.JsonOk(new { operationId = "op-179", status = "Reconciling" });
            }

            return TestHttpMessageHandler.JsonOk(new
            {
                operationId = "op-179",
                status = "Succeeded",
                actuatorReceipt = new { receiptId = "rcpt-179", operationId = "op-179" }
            });
        });
        using BackendGateway gateway = ProvisioningSubstrateFixtures.CreateGateway(handler);
        string recoveryRoot;
        AuditRecoveryStore recoveryStore = NewRecoveryStore(out recoveryRoot);
        FaultAuditSink failingSink = new(writeFailure: failurePoint);
        OperatorPolicyModel policy = ProvisioningSubstrateFixtures.DirectAllowedPolicy() with
        {
            AuditHookTarget = failingSink.Target
        };
        OperationRuntime runtime = CreateRuntime();

        try
        {
            ActuationSpine firstSpine = new(runtime, policy, failingSink, recoveryStore);
            GitOpsExecutor executor = new(runtime, gateway, policy, spine: firstSpine);
            GitOpsExecutionResult execution = await executor.ExecuteSyncAsync(
                desiredRevision: "release/2026.03",
                currentRevision: null,
                reason: "acceptance",
                idempotencyKey: "honua-devops:acceptance:gateway:post-mutation",
                correlationId: "honua-devops:acceptance",
                priority: "normal",
                parameters: new Dictionary<string, string>
                {
                    ["service"] = "roads-api",
                    ["environments"] = "dev",
                    ["action"] = "sync"
                },
                authorizationDryRun: false,
                policyGate: "lower-env-execution",
                CancellationToken.None);

            Assert.Equal(GitOpsExecutionStatus.Succeeded, execution.Status);
            Assert.True(execution.Mutated);
            int acknowledgedBackendCallCount = handler.CapturedRequests.Count;
            Assert.True(acknowledgedBackendCallCount >= 3);
            Assert.NotNull(execution.IdempotencyKey);

            OperationResponse response = new(
                Status: execution.Status,
                Summary: "gateway acceptance",
                Findings: execution.Findings,
                Actions: [],
                ValidationChecks: [],
                Risks: [],
                BackendSteps: execution.BackendSteps,
                Actuation: execution.ToActuationResult(
                    actuatorId: "honua.gitops.sync",
                    action: "sync",
                    target: "prod-api"));

            IOException auditFailure = await Assert.ThrowsAsync<IOException>(() => ToolCallAuditor.EmitAsync(
                new AuditContext("session", "execute", "execute-lower-env", "direct-allowed", "mcp", failingSink, recoveryStore),
                new ToolCallRecord("deploy_service_with_gitops", new Dictionary<string, object?>()),
                response,
                CancellationToken.None));
            Assert.Contains(failurePoint, auditFailure.Message, StringComparison.Ordinal);
            Assert.True(failingSink.WriteAccepted);
            Assert.True(recoveryStore.TryRead(execution.IdempotencyKey!, out AuditRecoveryEvidence? evidence));
            Assert.NotNull(evidence);
            Assert.Equal("indeterminate/reconciliation-required", evidence!.RecoveryState);
            Assert.Equal("op-179", evidence.OperationId);
            Assert.Equal(execution.IdempotencyKey, evidence.IdempotencyKey);
            Assert.True(evidence.MutationAttempted);
            Assert.True(evidence.BackendAcknowledged);
            Assert.Contains("rcpt-179", evidence.ApprovalReference ?? string.Empty, StringComparison.Ordinal);
            Assert.DoesNotContain("Authorization", JsonSerializer.Serialize(evidence), StringComparison.OrdinalIgnoreCase);

            // Simulate a restarted host: a fresh spine has no in-memory claim, so the
            // durable recovery record must be what prevents a second backend mutation.
            FaultAuditSink recoveredSink = new();
            ActuationSpine restartedSpine = new(runtime, policy with { AuditHookTarget = recoveredSink.Target }, recoveredSink, recoveryStore);
            GitOpsExecutor retryExecutor = new(runtime, gateway, policy with { AuditHookTarget = recoveredSink.Target }, spine: restartedSpine);
            GitOpsExecutionResult retry = await retryExecutor.ExecuteSyncAsync(
                desiredRevision: "release/2026.03",
                currentRevision: null,
                reason: "acceptance retry",
                idempotencyKey: execution.IdempotencyKey!,
                correlationId: "honua-devops:acceptance",
                priority: "normal",
                parameters: new Dictionary<string, string>
                {
                    ["service"] = "roads-api",
                    ["environments"] = "dev",
                    ["action"] = "sync"
                },
                authorizationDryRun: false,
                policyGate: "lower-env-execution",
                CancellationToken.None);

            Assert.Equal(GitOpsExecutionStatus.Indeterminate, retry.Status);
            Assert.Contains("audit-reconciliation-required", retry.BlockingReasons);
            Assert.Equal(acknowledgedBackendCallCount, handler.CapturedRequests.Count);
        }
        finally
        {
            if (Directory.Exists(recoveryRoot))
            {
                Directory.Delete(recoveryRoot, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData("apply", "execute-lower-env", "audit parent unwritable")]
    [InlineData("apply", "execute-lower-env", "audit append failed")]
    [InlineData("apply", "execute-lower-env", "audit flush failed")]
    [InlineData("destroy", "break-glass", "audit parent unwritable")]
    [InlineData("destroy", "break-glass", "audit append failed")]
    [InlineData("destroy", "break-glass", "audit flush failed")]
    public async Task ProvisionInfrastructureExactMutation_RefusesBeforeStartingTheRealProcessWhenDurabilityProbeFails(
        string action,
        string tierName,
        string failure)
    {
        using TerraformTestRoot root = new();
        FakeSubstrateRunner plannerRunner = new();
        using BackendGateway gateway = ProvisioningSubstrateFixtures.CreateGateway();
        bool isDestroy = action == "destroy";
        ExecutionMode planningMode = isDestroy ? ExecutionMode.Execute : ExecutionMode.Plan;
        ExecutionTier planningTier = isDestroy ? ExecutionTier.BreakGlass : ExecutionTier.Plan;
        HonuaOperationsToolkit planner = new(
            ProvisioningSubstrateFixtures.CreateRuntime(root.Path, planningMode, planningTier),
            gateway,
            isDestroy ? ProvisioningSubstrateFixtures.DirectAllowedPolicy() : null,
            provisioningProcessRunner: plannerRunner);
        OperationResponse plan = await planner.ProvisionInfrastructureAsync(
            "aws-ecs", "small", isDestroy ? "destroy" : "plan", "{\"environment\":\"dev\"}", false, string.Empty);
        string challenge = ProvisioningSubstrateFixtures.ExtractChallenge(plan, $"confirmation=");
        string approval = ProvisioningSubstrateFixtures.CreateApprovalReceipt(plan, action);

        string processMarker = Path.Combine(root.Path, "apply-started");
        File.WriteAllText(root.ApplyScript, $"#!/usr/bin/env bash\nprintf started > '{processMarker}'\nexit 0\n");
        FaultAuditSink sink = new(probeFailure: failure);
        string recoveryRoot;
        AuditRecoveryStore recoveryStore = NewRecoveryStore(out recoveryRoot);
        ExecutionTier tier = tierName == "break-glass" ? ExecutionTier.BreakGlass : ExecutionTier.ExecuteLowerEnv;
        OperationRuntime runtime = ProvisioningSubstrateFixtures.CreateRuntime(root.Path, ExecutionMode.Execute, tier);
        OperatorPolicyModel policy = ProvisioningSubstrateFixtures.DirectAllowedPolicy() with
        {
            AuditHookTarget = sink.Target
        };
        HonuaOperationsToolkit executor = new(
            runtime,
            gateway,
            policy,
            auditSink: sink,
            provisioningProcessRunner: SystemProvisioningProcessRunner.Instance,
            auditRecoveryStore: recoveryStore);

        try
        {
            OperationResponse response = await executor.ProvisionInfrastructureAsync(
                "aws-ecs", "small", action, "{\"environment\":\"dev\"}", true, challenge, approval);

            Assert.Equal("audit-sink-unavailable", response.Status);
            Assert.Contains("not started", response.Summary, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(processMarker));
            Assert.Empty(Directory.Exists(recoveryRoot)
                ? Directory.EnumerateFileSystemEntries(recoveryRoot)
                : []);
        }
        finally
        {
            if (Directory.Exists(recoveryRoot))
            {
                Directory.Delete(recoveryRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ToolkitLocalMutationSeams_RefuseBeforeWritingFilesOrStartingVerifierWhenDurabilityProbeFails()
    {
        using TerraformTestRoot root = new();
        FakeSubstrateRunner runner = new();
        using BackendGateway gateway = ProvisioningSubstrateFixtures.CreateGateway();
        (HonuaOperationsToolkit ordinaryToolkit, OperationResponse apply) = await ApplyForLocalSeamAsync(root, runner, gateway);
        string provisioningOperationId = apply.ProvisioningLineage!.ProvisioningOperationId;
        string handoffDirectory = Path.Combine(root.Path, "handoff");

        OperationResponse ordinaryHandoff = await ordinaryToolkit.InstallHandoffAsync(
            "aws-ecs", string.Empty, string.Empty, handoffDirectory, false, provisioningOperationId);
        Assert.Equal("install-handoff-written", ordinaryHandoff.Status);
        string handoffPath = Path.Combine(handoffDirectory, "honua-mcp-proxy.handoff.json");

        FaultAuditSink sink = new(probeFailure: "disconnected audit sink");
        string recoveryRoot;
        AuditRecoveryStore recoveryStore = NewRecoveryStore(out recoveryRoot);
        OperatorPolicyModel policy = ProvisioningSubstrateFixtures.DirectAllowedPolicy() with
        {
            AuditHookTarget = sink.Target
        };
        FakeInstallHandoffVerifier verifier = new(succeed: true);
        HonuaOperationsToolkit guardedToolkit = new(
            ProvisioningSubstrateFixtures.CreateRuntime(root.Path, ExecutionMode.Execute, ExecutionTier.ExecuteLowerEnv),
            gateway,
            policy,
            auditSink: sink,
            provisioningProcessRunner: runner,
            installHandoffVerifier: verifier,
            auditRecoveryStore: recoveryStore);

        try
        {
            string newHandoffDirectory = Path.Combine(root.Path, "blocked-handoff");
            OperationResponse blockedInstall = await guardedToolkit.InstallHandoffAsync(
                "aws-ecs", string.Empty, string.Empty, newHandoffDirectory, false, provisioningOperationId);
            Assert.Equal("audit-sink-unavailable", blockedInstall.Status);
            Assert.False(Directory.Exists(newHandoffDirectory));

            OperationResponse blockedVerify = await guardedToolkit.VerifyInstallHandoffAsync(handoffPath, false);
            Assert.Equal("audit-sink-unavailable", blockedVerify.Status);
            Assert.Null(verifier.Request);
        }
        finally
        {
            if (Directory.Exists(recoveryRoot))
            {
                Directory.Delete(recoveryRoot, recursive: true);
            }
        }
    }

    private static async Task<(HonuaOperationsToolkit Toolkit, OperationResponse Apply)> ApplyForLocalSeamAsync(
        TerraformTestRoot root,
        FakeSubstrateRunner runner,
        BackendGateway gateway)
    {
        HonuaOperationsToolkit planner = new(
            ProvisioningSubstrateFixtures.CreateRuntime(root.Path, ExecutionMode.Plan, ExecutionTier.Plan),
            gateway,
            provisioningProcessRunner: runner);
        OperationResponse plan = await planner.ProvisionInfrastructureAsync(
            "aws-ecs", "small", "plan", "{\"environment\":\"dev\"}", false, string.Empty);
        string challenge = ProvisioningSubstrateFixtures.ExtractChallenge(plan, "confirmation=");
        HonuaOperationsToolkit toolkit = new(
            ProvisioningSubstrateFixtures.CreateRuntime(root.Path, ExecutionMode.Execute, ExecutionTier.ExecuteLowerEnv),
            gateway,
            ProvisioningSubstrateFixtures.DirectAllowedPolicy(),
            provisioningProcessRunner: runner);
        OperationResponse apply = await toolkit.ProvisionInfrastructureAsync(
            "aws-ecs", "small", "apply", "{\"environment\":\"dev\"}", true, challenge,
            ProvisioningSubstrateFixtures.CreateApprovalReceipt(plan, "apply"));
        Assert.Equal("infrastructure-provisioned", apply.Status);
        return (toolkit, apply);
    }

    private static OperationRuntime CreateRuntime()
        => new(
            ExecutionMode.Execute,
            ExecutionTier.ExecuteLowerEnv,
            "honua-gitops",
            AllowedEnvironments: ["dev", "staging", "prod"],
            TerraformRepository: "https://github.com/honua-io/honua-iac",
            TerraformRef: "main",
            TerraformLocalPath: "/tmp/honua-iac",
            TerraformDeploymentTargets: ["eks", "aks"],
            ProductionEnvironments: ["prod"],
            DeployTargetId: "prod-api");

    private static AuditRecoveryStore NewRecoveryStore(out string root)
    {
        root = Path.Combine(Path.GetTempPath(), $"honua-audit-recovery-{Guid.NewGuid():n}");
        return new AuditRecoveryStore(root);
    }

    private sealed class FaultAuditSink(string? probeFailure = null, string? writeFailure = null) : IAuditSink, IProbeableAuditSink
    {
        private readonly string? _probeFailure = probeFailure;
        private readonly string? _writeFailure = writeFailure;

        internal bool WriteAccepted { get; private set; }

        public string Target => "acceptance-fault-sink";

        public bool TryProbe(out string reason)
        {
            reason = _probeFailure ?? string.Empty;
            return _probeFailure is null;
        }

        public Task WriteAsync(AuditRecord record, CancellationToken cancellationToken = default)
        {
            if (_writeFailure is not null)
            {
                WriteAccepted = true;
                return Task.FromException(new IOException($"{_writeFailure} injected"));
            }

            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
