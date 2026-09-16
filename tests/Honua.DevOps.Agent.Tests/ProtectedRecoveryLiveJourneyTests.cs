using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Honua.DevOps.Agent.Operations;
using Honua.DevOps.Agent.Operations.Actuation;
using Honua.DevOps.Agent.Operations.GitOps;
using Honua.DevOps.Agent.Operations.OperatorPolicy;
using OperatorPolicyModel = Honua.DevOps.Agent.Operations.OperatorPolicy.OperatorPolicy;

namespace Honua.DevOps.Agent.Tests;

// Opt-in live proof for issue #191 against a booted honua-server candidate whose self-hosted
// rolling target owns a real protected activation (certification/protected-recovery/boot.sh).
// Gated by HONUA_DEVOPS_LIVE_PROTECTED_RECOVERY=true so CI never reaches a backend.
//
// Every scenario runs the real DevOps code (GitOpsExecutor, ActuationSpine, RecoveryExecutor,
// the file-backed desired-intent ledger) against the real server, and asserts only what the
// server, the replicas and the ledger report. Each scenario appends its transcript (every
// request DevOps sent and the response it got, results and ledger records) to the receipt at
// HONUA_DEVOPS_LIVE_PROTECTED_RECOVERY_RECEIPT.
[Collection(ProtectedRecoveryLiveJourneyTests.CollectionName)]
public sealed class ProtectedRecoveryLiveJourneyTests : IDisposable
{
    internal const string CollectionName = "protected-recovery-live";

    private const string EnabledVariable = "HONUA_DEVOPS_LIVE_PROTECTED_RECOVERY";
    private const string ReceiptVariable = "HONUA_DEVOPS_LIVE_PROTECTED_RECOVERY_RECEIPT";
    private const string WorkVariable = "HONUA_DEVOPS_LIVE_PROTECTED_RECOVERY_WORK";
    private const string Target = "proof-selfhosted";
    private const string Prefix = "devops191";
    private const int ActivePort = 19181;
    private const string Actor = "operator@honua.io";

    private static readonly Dictionary<string, string> Healthy = new()
    {
        ["proof_error_rate"] = "0.001",
        ["proof_latency_p95"] = "40",
        ["proof_samples"] = "500"
    };

    private readonly string _ledgerDirectory = Directory.CreateTempSubdirectory("honua-devops-live-recovery-").FullName;
    private readonly FileDesiredIntentLedger _ledger;

    public ProtectedRecoveryLiveJourneyTests()
    {
        _ledger = new FileDesiredIntentLedger(Path.Combine(_ledgerDirectory, "audit.jsonl.desired-intent.jsonl"));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_ledgerDirectory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    // Declared recovery: approval records intent, the server opens the window and seals a grant,
    // only the declared recovery is admitted (locally and by the server fence), restoration and
    // quarantine are recorded with lineage, and the next reconcile leaves the prior revision serving.
    [Fact]
    public async Task Live_DeclaredRecovery_IsFencedRecordedAndHoldsOnNextReconcile()
    {
        if (!Enabled(out Live? live))
        {
            return;
        }

        await using Scenario scenario = await Scenario.StartAsync(this, live!, nameof(Live_DeclaredRecovery_IsFencedRecordedAndHoldsOnNextReconcile));
        string prior = live!.Image("prior-a");
        string candidate = live.Image("candidate-b");

        GitOpsExecutionResult sync = await scenario.SyncAsync(candidate, prior, pollSeconds: 60);
        scenario.Record("approved-sync", sync);
        Assert.Equal(GitOpsExecutionStatus.InProgress, sync.Status);
        Assert.Equal(DeployProtectionPhases.Observing, sync.ProtectionPhase);
        Assert.Equal(RolloutJourneyStatus.ConfirmingServiceHealth, RolloutJourneyStatus.From(sync));
        DesiredIntentRecord approved = await LatestAsync();
        Assert.Equal((DesiredIntentKind.Approved, candidate, sync.OperationId), (approved.Kind, approved.DesiredRevision, approved.OperationId));

        JsonElement window = await scenario.ReadProtectionAsync(sync.OperationId!);
        ActuationSpine.DeploymentRecoveryGrant grant = scenario.IssueGrant(sync.OperationId!, prior, candidate,
            window.GetProperty("policyDigest").GetString()!, DateTimeOffset.UtcNow.AddMinutes(10));

        // Wrong actor, wrong target, broadened compensation and an expired grant are refused
        // before any request reaches the server.
        int before = scenario.Mutations;
        GitOpsExecutionResult wrongActor = await scenario.RecoverAsync(grant, "intruder@example.invalid", Target);
        GitOpsExecutionResult wrongTarget = await scenario.RecoverAsync(grant, Actor, "some-other-target");
        GitOpsExecutionResult broadened = await scenario.RecoverAsync(
            scenario.IssueGrant(sync.OperationId!, prior, candidate, window.GetProperty("policyDigest").GetString()!,
                DateTimeOffset.UtcNow.AddMinutes(10), ActuationSpine.PermittedCompensation.QuarantineCandidateOnly),
            Actor, Target);
        ActuationSpine.DeploymentRecoveryGrant shortGrant = scenario.IssueGrant(sync.OperationId!, prior, candidate,
            window.GetProperty("policyDigest").GetString()!, DateTimeOffset.UtcNow.AddSeconds(2));
        await Task.Delay(TimeSpan.FromSeconds(3));
        GitOpsExecutionResult expired = await scenario.RecoverAsync(shortGrant, Actor, Target);
        scenario.Record("wrong-actor", wrongActor);
        scenario.Record("wrong-target", wrongTarget);
        scenario.Record("broadened-compensation", broadened);
        scenario.Record("expired-grant", expired);
        foreach (GitOpsExecutionResult refused in new[] { wrongActor, wrongTarget, broadened, expired })
        {
            Assert.Equal(GitOpsExecutionStatus.ApprovalRequired, refused.Status);
            Assert.False(refused.Mutated);
        }

        Assert.Equal(["recovery-scope-mismatch"], wrongActor.BlockingReasons);
        Assert.Equal(["recovery-scope-mismatch"], wrongTarget.BlockingReasons);
        Assert.Equal(["recovery-scope-mismatch"], broadened.BlockingReasons);
        Assert.Equal(["recovery-scope-mismatch"], expired.BlockingReasons);
        Assert.Equal(before, scenario.Mutations);
        await live.AssertServesAsync("candidate-b");

        // The declared recovery: its body quotes the sealed grant and the server admits it.
        GitOpsExecutionResult recovered = await scenario.RecoverAsync(grant, Actor, Target);
        scenario.Record("declared-recovery", recovered);
        CapturedExchange rollback = Assert.Single(scenario.Exchanges, exchange => exchange.Path.EndsWith("/rollback", StringComparison.Ordinal));
        using (JsonDocument body = JsonDocument.Parse(rollback.RequestBody!))
        {
            Assert.Equal(window.GetProperty("grantId").GetString(), body.RootElement.GetProperty("grantId").GetString());
            Assert.Equal(window.GetProperty("actor").GetString(), body.RootElement.GetProperty("actor").GetString());
            Assert.Equal(candidate, body.RootElement.GetProperty("expectedCandidateRevision").GetString());
            Assert.Equal(prior, body.RootElement.GetProperty("expectedPreviousRevision").GetString());
        }

        Assert.Equal(200, rollback.Status);
        Assert.True(recovered.Status is GitOpsExecutionStatus.InProgress or GitOpsExecutionStatus.RolledBack, recovered.Status);

        // A restarted executor observes the settled server recovery and records it once.
        await scenario.WaitForStatusAsync(sync.OperationId!, "RolledBack");
        GitOpsExecutionResult observed = await scenario.RecoverAsync(grant, Actor, Target, freshSpine: true);
        GitOpsExecutionResult replay = await scenario.RecoverAsync(grant, Actor, Target, freshSpine: true);
        scenario.Record("restart-observes-recovery", observed);
        scenario.Record("replay", replay);
        Assert.Equal(GitOpsExecutionStatus.RolledBack, observed.Status);
        Assert.Equal(RolloutJourneyStatus.PreviousVersionRestored, RolloutJourneyStatus.From(observed));
        Assert.Single(scenario.Exchanges, exchange => exchange.Path.EndsWith("/rollback", StringComparison.Ordinal));

        DesiredIntentRecord restored = await LatestAsync();
        scenario.RecordLedger(restored);
        Assert.Equal((DesiredIntentKind.Restored, prior, sync.OperationId, Actor), (restored.Kind, restored.DesiredRevision, restored.OperationId, restored.Actor));
        Assert.Equal([candidate], restored.RejectedRevisions);
        Assert.NotNull(restored.ApprovalReference);
        await live.AssertServesAsync("prior-a");

        // Next reconcile of the rejected candidate is refused with no mutation; prior keeps serving.
        int beforeReconcile = scenario.Mutations;
        GitOpsExecutionResult resurrect = await scenario.SyncAsync(candidate, prior, pollSeconds: 5);
        scenario.Record("next-reconcile-of-quarantined-candidate", resurrect);
        Assert.Equal(GitOpsExecutionStatus.ApprovalRequired, resurrect.Status);
        Assert.Equal(["desired-revision-quarantined"], resurrect.BlockingReasons);
        Assert.False(resurrect.Mutated);
        Assert.Equal(beforeReconcile, scenario.Mutations);
        await live.AssertServesAsync("prior-a");
    }

    // The server's own reconciler recovers on an injected error-rate regression while DevOps
    // watches: restoration is recorded, the rollout reads Previous version restored, and the
    // next reconcile of the candidate is refused.
    [Fact]
    public async Task Live_ServerRecoveryOnInjectedRegression_IsFoldedIntoDesiredIntent()
    {
        if (!Enabled(out Live? live))
        {
            return;
        }

        await using Scenario scenario = await Scenario.StartAsync(this, live!, nameof(Live_ServerRecoveryOnInjectedRegression_IsFoldedIntoDesiredIntent));
        string prior = live!.Image("prior-a");
        string candidate = live.Image("candidate-b");

        Task inject = scenario.InjectWhenObservingAsync(() =>
            live.WriteTelemetry(new Dictionary<string, string>(Healthy) { ["proof_error_rate"] = "0.42" }, scenario));
        GitOpsExecutionResult sync = await scenario.SyncAsync(candidate, prior, pollSeconds: 240);
        await inject;
        scenario.Record("watched-sync", sync);

        Assert.Equal(GitOpsExecutionStatus.RolledBack, sync.Status);
        Assert.Equal(RolloutJourneyStatus.PreviousVersionRestored, RolloutJourneyStatus.From(sync));
        Assert.DoesNotContain(scenario.Exchanges, exchange => exchange.Path.EndsWith("/rollback", StringComparison.Ordinal));
        DesiredIntentRecord restored = await LatestAsync();
        scenario.RecordLedger(restored);
        Assert.Equal((DesiredIntentKind.Restored, prior, sync.OperationId), (restored.Kind, restored.DesiredRevision, restored.OperationId));
        Assert.Equal([candidate], restored.RejectedRevisions);
        await live.AssertServesAsync("prior-a");

        int before = scenario.Mutations;
        GitOpsExecutionResult resurrect = await scenario.SyncAsync(candidate, prior, pollSeconds: 5);
        scenario.Record("next-reconcile-of-quarantined-candidate", resurrect);
        Assert.Equal(["desired-revision-quarantined"], resurrect.BlockingReasons);
        Assert.Equal(before, scenario.Mutations);
        await live.AssertServesAsync("prior-a");
    }

    // A recovery the server could not prove (the retained prior replica was replaced out of band)
    // quarantines the candidate without claiming restoration, and reads Needs attention.
    [Fact]
    public async Task Live_FailedServerRecovery_QuarantinesWithoutClaimingRestoration()
    {
        if (!Enabled(out Live? live))
        {
            return;
        }

        await using Scenario scenario = await Scenario.StartAsync(this, live!, nameof(Live_FailedServerRecovery_QuarantinesWithoutClaimingRestoration));
        string prior = live!.Image("prior-a");
        string candidate = live.Image("candidate-b");

        Task inject = scenario.InjectWhenObservingAsync(() =>
        {
            live.ReplaceActiveReplica("candidate-c", scenario);
            live.WriteTelemetry(new Dictionary<string, string>(Healthy) { ["proof_error_rate"] = "0.42" }, scenario);
        });
        GitOpsExecutionResult sync = await scenario.SyncAsync(candidate, prior, pollSeconds: 240);
        await inject;
        scenario.Record("watched-sync", sync);

        Assert.Equal(GitOpsExecutionStatus.Indeterminate, sync.Status);
        Assert.Equal((DeployProtectionPhases.Unavailable, DeployProtectionReasonCodes.RollbackFailed), (sync.ProtectionPhase, sync.ProtectionReasonCode));
        Assert.Contains("protected-recovery-unproven", sync.BlockingReasons);
        Assert.Equal(RolloutJourneyStatus.NeedsAttention, RolloutJourneyStatus.From(sync));
        DesiredIntentRecord rejected = await LatestAsync();
        scenario.RecordLedger(rejected);
        Assert.Equal((DesiredIntentKind.Rejected, sync.OperationId), (rejected.Kind, rejected.OperationId));
        Assert.True(string.IsNullOrEmpty(rejected.DesiredRevision));
        Assert.Equal([candidate], rejected.RejectedRevisions);

        int before = scenario.Mutations;
        GitOpsExecutionResult resurrect = await scenario.SyncAsync(candidate, prior, pollSeconds: 5);
        scenario.Record("next-reconcile-of-quarantined-candidate", resurrect);
        Assert.Equal(["desired-revision-quarantined"], resurrect.BlockingReasons);
        Assert.Equal(before, scenario.Mutations);
    }

    // Compare-and-set: newer approved intent for the target means the older grant's recovery is
    // refused before any request, and the server window it would have compensated is untouched.
    [Fact]
    public async Task Live_NewerApprovedIntent_RefusesOlderRecoveryWithoutRequest()
    {
        if (!Enabled(out Live? live))
        {
            return;
        }

        await using Scenario scenario = await Scenario.StartAsync(this, live!, nameof(Live_NewerApprovedIntent_RefusesOlderRecoveryWithoutRequest));
        string prior = live!.Image("prior-a");
        string candidate = live.Image("candidate-b");

        GitOpsExecutionResult sync = await scenario.SyncAsync(candidate, prior, pollSeconds: 60);
        scenario.Record("approved-sync", sync);
        Assert.Equal(DeployProtectionPhases.Observing, sync.ProtectionPhase);
        JsonElement window = await scenario.ReadProtectionAsync(sync.OperationId!);
        ActuationSpine.DeploymentRecoveryGrant grant = scenario.IssueGrant(sync.OperationId!, prior, candidate,
            window.GetProperty("policyDigest").GetString()!, DateTimeOffset.UtcNow.AddMinutes(10));

        // A concurrent approved change for the same target: its intent is recorded before submit.
        GitOpsExecutionResult newer = await scenario.SyncAsync(live.Image("candidate-c"), candidate, pollSeconds: 20);
        scenario.Record("concurrent-approved-change", newer);
        DesiredIntentRecord latest = await LatestAsync();
        scenario.RecordLedger(latest);
        Assert.Equal((DesiredIntentKind.Approved, live.Image("candidate-c")), (latest.Kind, latest.DesiredRevision));

        int before = scenario.Mutations;
        GitOpsExecutionResult recovery = await scenario.RecoverAsync(grant, Actor, Target);
        scenario.Record("older-recovery", recovery);
        Assert.Equal(GitOpsExecutionStatus.ApprovalRequired, recovery.Status);
        Assert.Equal(["recovery-intent-superseded"], recovery.BlockingReasons);
        Assert.Equal(before, scenario.Mutations);
        JsonElement untouched = await scenario.ReadProtectionAsync(sync.OperationId!);
        Assert.Equal(window.GetProperty("grantId").GetString(), untouched.GetProperty("grantId").GetString());
        Assert.Equal(DeployProtectionPhases.Observing, untouched.GetProperty("phase").GetString());
    }

    // The generic, model-invocable rollback stays gated: with the rollback capability off it
    // issues no request at all, even against a live window.
    [Fact]
    public async Task Live_GenericRollbackTool_StaysGated()
    {
        if (!Enabled(out Live? live))
        {
            return;
        }

        await using Scenario scenario = await Scenario.StartAsync(this, live!, nameof(Live_GenericRollbackTool_StaysGated));
        GitOpsExecutionResult sync = await scenario.SyncAsync(live!.Image("candidate-b"), live.Image("prior-a"), pollSeconds: 60);
        Assert.Equal(DeployProtectionPhases.Observing, sync.ProtectionPhase);

        int before = scenario.Exchanges.Count;
        GitOpsExecutionResult generic = await new RollbackExecutor(scenario.Runtime, scenario.Gateway, scenario.Policy)
            .ExecuteRollbackAsync(sync.OperationId!, "model-proposed rollback", authorizationDryRun: false, "lower-env-execution", CancellationToken.None);
        scenario.Record("generic-rollback-tool", generic);
        Assert.Equal(GitOpsExecutionStatus.ExperimentalDisabled, generic.Status);
        Assert.False(generic.Mutated);
        Assert.Equal(before, scenario.Exchanges.Count);
        await live.AssertServesAsync("candidate-b");
    }

    private async Task<DesiredIntentRecord> LatestAsync()
    {
        DesiredIntentSnapshot snapshot = await _ledger.ReadLatestAsync(Target, CancellationToken.None);
        Assert.True(snapshot.Readable, snapshot.Detail);
        return snapshot.Latest!;
    }

    private static bool Enabled(out Live? live)
    {
        live = null;
        if (!string.Equals(Environment.GetEnvironmentVariable(EnabledVariable), "true", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        live = new Live(
            Environment.GetEnvironmentVariable(WorkVariable) ?? throw new InvalidOperationException($"{WorkVariable} is required."),
            Environment.GetEnvironmentVariable(ReceiptVariable));
        return true;
    }

    // ---- live environment (boot.sh) ----

    private sealed class Live(string work, string? receiptPath)
    {
        private readonly Dictionary<string, string> _images = [];

        internal BackendConfiguration Configuration { get; } = BackendConfiguration.Load();

        internal string? ReceiptPath { get; } = receiptPath;

        internal string Image(string revision)
        {
            if (!_images.TryGetValue(revision, out string? id))
            {
                id = Docker("image", "inspect", "-f", "{{.Id}}", $"{Prefix}-workload:{revision}");
                _images[revision] = id;
            }

            return id;
        }

        internal void WriteTelemetry(IReadOnlyDictionary<string, string> values, Scenario scenario)
        {
            JsonObject state = [];
            foreach ((string query, string value) in values)
            {
                state[query] = new JsonObject { ["mode"] = "value", ["value"] = value };
            }

            File.WriteAllText(Path.Combine(work, "telemetry", "state.json"), state.ToJsonString());
            scenario.Note("telemetry-injected", state);
        }

        internal void ResetReplicas()
        {
            string stale = Docker("ps", "-aq", "--filter", $"label=honua.target={Target}");
            if (!string.IsNullOrWhiteSpace(stale))
            {
                Docker(["rm", "-f", .. stale.Split('\n', StringSplitOptions.RemoveEmptyEntries)]);
            }

            RunActive("prior-a");
        }

        internal void ReplaceActiveReplica(string revision, Scenario scenario)
        {
            Docker("rm", "-f", $"{Prefix}-app-{ActivePort}");
            RunActive(revision);
            scenario.Note("fault", new JsonObject { ["label"] = $"retained prior replica replaced out of band by {revision}" });
        }

        internal async Task<string?> ServedAsync()
        {
            using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(10) };
            try
            {
                string body = await client.GetStringAsync(new Uri(Configuration.HonuaApiBaseUri, $"{Prefix}-proxy/live-journey"));
                return JsonNode.Parse(body)?["revision"]?.GetValue<string>();
            }
            catch (HttpRequestException)
            {
                return null;
            }
        }

        // A window left open by an earlier scenario would retire or repoint the next scenario's
        // replicas, so every non-terminal operation is rolled back first (outside the recorder).
        internal async Task SettleLeftoverOperationsAsync(Scenario scenario)
        {
            using HttpClient client = new() { BaseAddress = Configuration.HonuaApiBaseUri, Timeout = TimeSpan.FromMinutes(2) };
            client.DefaultRequestHeaders.Add("X-API-Key", Configuration.HonuaApiKey);
            JsonNode? listed = JsonNode.Parse(await client.GetStringAsync("api/v1/admin/deploy/operations?limit=50"));
            foreach (JsonNode? operation in listed?["items"]?.AsArray() ?? [])
            {
                string? status = operation?["status"]?.GetValue<string>();
                if (status is "Submitted" or "Reconciling" or "RollbackRequested" or "Planned" or "AwaitingApproval")
                {
                    string id = operation!["operationId"]!.GetValue<string>();
                    using HttpResponseMessage settled = await client.PostAsync($"api/v1/admin/deploy/operations/{id}/rollback",
                        new StringContent("{\"reason\":\"live journey cleanup\"}", Encoding.UTF8, "application/json"));
                    scenario.Note("cleanup", new JsonObject { ["operationId"] = id, ["priorStatus"] = status, ["rollbackStatus"] = (int)settled.StatusCode });
                }
            }
        }

        // What the embedded proxy serves: real traffic through the server. The proxy swap can
        // trail the settled operation status by a moment, so this polls briefly.
        internal async Task AssertServesAsync(string revision)
        {
            DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(60);
            string? served;
            while ((served = await ServedAsync()) != revision && DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromSeconds(1));
            }

            Assert.Equal(revision, served);
        }

        internal void RestartServer()
            => Run(Path.Combine(RepositoryRoot(), "certification", "protected-recovery", "boot.sh"), ["restart"],
                new Dictionary<string, string> { ["PROOF_WORK"] = work });

        private void RunActive(string revision)
        {
            string image = Image(revision);
            Docker("run", "-d", "--name", $"{Prefix}-app-{ActivePort}", "-p", $"{ActivePort}:8080",
                "--label", $"honua.target={Target}", "--label", "honua.role=active",
                "--label", $"honua.revision={image}", "--label", $"io.honua.devops.proof.workload={revision}", image);
        }

        private static string Docker(params string[] arguments) => Run("docker", arguments, null);

        private static string Run(string fileName, string[] arguments, IReadOnlyDictionary<string, string>? environment)
        {
            ProcessStartInfo start = new(fileName) { RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string argument in arguments)
            {
                start.ArgumentList.Add(argument);
            }

            foreach ((string name, string value) in environment ?? new Dictionary<string, string>())
            {
                start.Environment[name] = value;
            }

            using Process process = Process.Start(start)!;
            string output = process.StandardOutput.ReadToEnd();
            string error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"{fileName} {string.Join(' ', arguments.Take(2))} failed: {error.Trim()}");
            }

            return output.Trim();
        }

        private static string RepositoryRoot()
        {
            DirectoryInfo? directory = new(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Honua.DevOps.slnx")))
            {
                directory = directory.Parent;
            }

            return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
        }
    }

    // ---- one scenario: executors over a recording transport, plus its transcript ----

    private sealed class Scenario : IAsyncDisposable
    {
        private readonly ProtectedRecoveryLiveJourneyTests _owner;
        private readonly Live _live;
        private readonly JsonObject _entry;
        private readonly RecordingHandler _handler = new();
        private ActuationSpine _spine;

        private Scenario(ProtectedRecoveryLiveJourneyTests owner, Live live, string name)
        {
            _owner = owner;
            _live = live;
            _entry = new JsonObject { ["scenario"] = name, ["startedAt"] = DateTimeOffset.UtcNow, ["events"] = new JsonArray() };
            Runtime = OperationRuntime.SafeDefault with
            {
                ExecutionMode = ExecutionMode.Execute,
                ExecutionTier = ExecutionTier.ExecuteLowerEnv,
                DeployTargetId = Target,
                ProtectedRecoveryEnabled = true
            };
            Policy = new OperatorPolicyModel(
                ApprovalMode.DirectAllowed,
                "stdout-evidence",
                new SupportSessionPolicy(SupportSessionAccess.Disabled, 60, true),
                BreakGlassPostActionReviewRequired: true);
            Gateway = new BackendGateway(live.Configuration, new HttpClient(_handler) { Timeout = TimeSpan.FromMinutes(2) });
            _spine = new ActuationSpine(Runtime, Policy);
        }

        internal OperationRuntime Runtime { get; }

        internal OperatorPolicyModel Policy { get; }

        internal BackendGateway Gateway { get; }

        internal IReadOnlyList<CapturedExchange> Exchanges => _handler.Exchanges;

        internal int Mutations => _handler.Exchanges.Count(exchange => exchange.Method == "POST" && !exchange.Path.EndsWith("/plan", StringComparison.Ordinal));

        internal static async Task<Scenario> StartAsync(ProtectedRecoveryLiveJourneyTests owner, Live live, string name)
        {
            Scenario scenario = new(owner, live, name);
            await live.SettleLeftoverOperationsAsync(scenario);
            live.ResetReplicas();
            live.WriteTelemetry(Healthy, scenario);
            await Task.Delay(TimeSpan.FromSeconds(2));
            if (await live.ServedAsync() != "prior-a")
            {
                // The embedded proxy destination is process state; a restart re-derives it.
                live.RestartServer();
                scenario.Note("environment-reset", new JsonObject { ["label"] = "server restarted so the proxy re-derives the active replica" });
            }

            await live.AssertServesAsync("prior-a");
            return scenario;
        }

        internal Task<GitOpsExecutionResult> SyncAsync(string desired, string current, int pollSeconds)
            => new GitOpsExecutor(Runtime, Gateway, Policy,
                    new DeployPollPolicy(TimeSpan.FromSeconds(pollSeconds), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)),
                    intentLedger: _owner._ledger)
                .ExecuteSyncAsync(
                    desiredRevision: desired,
                    currentRevision: current,
                    reason: "protected recovery live journey",
                    idempotencyKey: $"honua-devops:live-recovery:{Guid.NewGuid():N}",
                    correlationId: Actor,
                    priority: "normal",
                    parameters: new Dictionary<string, string>
                    {
                        ["service"] = Target,
                        ["environments"] = "dev",
                        ["action"] = "sync",
                        ["telemetry.connection"] = "proof-prometheus",
                        ["telemetry.error_rate.query"] = "proof_error_rate",
                        ["telemetry.error_rate.threshold"] = "0.05",
                        ["telemetry.latency_p95.query"] = "proof_latency_p95",
                        ["telemetry.latency_p95.threshold_ms"] = "500",
                        ["telemetry.sample_count.query"] = "proof_samples",
                        ["telemetry.sample_count.minimum"] = "10",
                        ["telemetry.warmup_seconds"] = "5",
                        ["telemetry.evidence_grace_seconds"] = "20",
                        ["telemetry.max_staleness_seconds"] = "60",
                        ["telemetry.rollback.consecutive_breaches"] = "1",
                        ["deployment.protection.observation_window_seconds"] = "900"
                    },
                    authorizationDryRun: false,
                    policyGate: "lower-env-execution",
                    CancellationToken.None);

        internal ActuationSpine.DeploymentRecoveryGrant IssueGrant(string operationId, string prior, string candidate, string policyDigest,
            DateTimeOffset expiresAt, ActuationSpine.PermittedCompensation compensation = ActuationSpine.PermittedCompensation.RestorePriorRevision)
        {
            Assert.True(_spine.AuthorizeRecoveryGrant(
                new ActuationRequest(
                    ActuatorId: "honua.deploy-operation.recovery",
                    Action: "recover",
                    Target: Target,
                    Environments: ["dev"],
                    DesiredState: $"recover:{operationId}",
                    IdempotencyKey: $"honua-devops:recovery:{operationId}",
                    PolicyGate: "protected-recovery",
                    AuthorizationDryRun: false,
                    Actor: Actor),
                tenant: "single-tenant",
                priorRevision: prior,
                candidateRevision: candidate,
                safetyPolicyDigest: policyDigest,
                expiresAtUtc: expiresAt,
                compensation,
                operationId,
                out ActuationSpine.DeploymentRecoveryGrant? grant,
                out string refusal), refusal);
            return grant!;
        }

        internal Task<GitOpsExecutionResult> RecoverAsync(ActuationSpine.DeploymentRecoveryGrant grant, string actor, string target, bool freshSpine = false)
        {
            if (freshSpine)
            {
                _spine = new ActuationSpine(Runtime, Policy);
            }

            return new RecoveryExecutor(Runtime, Gateway, Policy, _spine, _owner._ledger)
                .ExecuteRecoveryAsync(grant, actor, target, "declared recovery: live journey", CancellationToken.None);
        }

        internal async Task<JsonElement> ReadProtectionAsync(string operationId)
        {
            using BackendJsonResult read = await Gateway.GetDeployOperationJsonAsync(operationId, CancellationToken.None);
            Assert.True(read.CallResult.IsSuccess, read.CallResult.Detail);
            return read.Payload!.RootElement.GetProperty("protection").Clone();
        }

        internal async Task WaitForStatusAsync(string operationId, string status)
        {
            DateTimeOffset deadline = DateTimeOffset.UtcNow.AddMinutes(3);
            while (true)
            {
                using BackendJsonResult read = await Gateway.GetDeployOperationJsonAsync(operationId, CancellationToken.None);
                if (read.Payload is not null && DeployOperationReader.ReadStatus(read.Payload.RootElement) == status)
                {
                    return;
                }

                Assert.True(DateTimeOffset.UtcNow < deadline, $"operation {operationId} did not reach {status}");
                await Task.Delay(TimeSpan.FromSeconds(2));
            }
        }

        // Watches the recording transport for the first observation of an open window, then
        // injects the fault while the executor under test keeps polling.
        internal async Task InjectWhenObservingAsync(Action inject)
        {
            DateTimeOffset deadline = DateTimeOffset.UtcNow.AddMinutes(3);
            while (!_handler.Exchanges.Any(exchange => exchange.ResponseBody?.Contains("\"phase\":\"observing\"", StringComparison.Ordinal) == true))
            {
                Assert.True(DateTimeOffset.UtcNow < deadline, "the candidate never reached an open observation window");
                await Task.Delay(TimeSpan.FromMilliseconds(500));
            }

            inject();
        }

        internal void Record(string label, GitOpsExecutionResult result)
            => Note("result", new JsonObject
            {
                ["label"] = label,
                ["status"] = result.Status,
                ["journey"] = RolloutJourneyStatus.Label(RolloutJourneyStatus.From(result)),
                ["operationId"] = result.OperationId,
                ["serverStatus"] = result.ServerStatus,
                ["mutated"] = result.Mutated,
                ["protectionPhase"] = result.ProtectionPhase,
                ["protectionReasonCode"] = result.ProtectionReasonCode,
                ["blockingReasons"] = new JsonArray([.. result.BlockingReasons.Select(reason => (JsonNode?)reason)]),
                ["findings"] = new JsonArray([.. result.Findings.Select(finding => (JsonNode?)finding)])
            });

        internal void RecordLedger(DesiredIntentRecord record)
            => Note("desired-intent", JsonSerializer.SerializeToNode(record)!);

        internal void Note(string kind, JsonNode payload)
            => _entry["events"]!.AsArray().Add(new JsonObject { ["at"] = DateTimeOffset.UtcNow, ["kind"] = kind, ["payload"] = payload.DeepClone() });

        public ValueTask DisposeAsync()
        {
            _entry["exchanges"] = JsonSerializer.SerializeToNode(_handler.Exchanges);
            _entry["finishedAt"] = DateTimeOffset.UtcNow;
            if (_live.ReceiptPath is { } path)
            {
                lock (typeof(ProtectedRecoveryLiveJourneyTests))
                {
                    File.AppendAllText(path, _entry.ToJsonString() + "\n");
                }
            }

            Gateway.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed record CapturedExchange(string At, string Method, string Path, string? RequestBody, int Status, string? ResponseBody);

    // Records what DevOps sent and received. Headers (the API key) are never captured.
    private sealed class RecordingHandler() : DelegatingHandler(new HttpClientHandler())
    {
        private readonly List<CapturedExchange> _exchanges = [];

        internal IReadOnlyList<CapturedExchange> Exchanges
        {
            get
            {
                lock (_exchanges)
                {
                    return [.. _exchanges];
                }
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string? requestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            HttpResponseMessage response = await base.SendAsync(request, cancellationToken);
            byte[] bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            string? contentType = response.Content.Headers.ContentType?.ToString();
            response.Content = new ByteArrayContent(bytes);
            if (contentType is not null)
            {
                response.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
            }

            lock (_exchanges)
            {
                _exchanges.Add(new CapturedExchange(
                    DateTimeOffset.UtcNow.ToString("O"),
                    request.Method.Method,
                    request.RequestUri?.PathAndQuery ?? string.Empty,
                    requestBody,
                    (int)response.StatusCode,
                    Encoding.UTF8.GetString(bytes)));
            }

            return response;
        }
    }
}

[CollectionDefinition(ProtectedRecoveryLiveJourneyTests.CollectionName, DisableParallelization = true)]
public sealed class ProtectedRecoveryLiveJourneyCollection;
