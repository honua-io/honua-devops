using Honua.DevOps.Agent.Operations.Troubleshooting;

namespace Honua.DevOps.Agent.Tests;

public class FaultInjectionTests
{
    // ---------------------------------------------------------------
    // FaultInjectionContext validation
    // ---------------------------------------------------------------

    [Fact]
    public void FaultInjectionContext_Validate_ThrowsOnMissingEnvironment()
    {
        FaultInjectionContext context = new(
            Environment: "",
            Region: "us-west-2",
            ResourcePrefix: "honua",
            Credentials: new Dictionary<string, string>(),
            DryRun: false,
            Timeout: TimeSpan.FromMinutes(5));

        Assert.Throws<ArgumentException>(() => context.Validate());
    }

    [Fact]
    public void FaultInjectionContext_Validate_ThrowsOnMissingRegion()
    {
        FaultInjectionContext context = new(
            Environment: "staging",
            Region: "  ",
            ResourcePrefix: "honua",
            Credentials: new Dictionary<string, string>(),
            DryRun: false,
            Timeout: TimeSpan.FromMinutes(5));

        Assert.Throws<ArgumentException>(() => context.Validate());
    }

    [Fact]
    public void FaultInjectionContext_Validate_ThrowsOnMissingResourcePrefix()
    {
        FaultInjectionContext context = new(
            Environment: "staging",
            Region: "us-west-2",
            ResourcePrefix: "",
            Credentials: new Dictionary<string, string>(),
            DryRun: false,
            Timeout: TimeSpan.FromMinutes(5));

        Assert.Throws<ArgumentException>(() => context.Validate());
    }

    [Fact]
    public void FaultInjectionContext_Validate_ThrowsOnZeroTimeout()
    {
        FaultInjectionContext context = new(
            Environment: "staging",
            Region: "us-west-2",
            ResourcePrefix: "honua",
            Credentials: new Dictionary<string, string>(),
            DryRun: false,
            Timeout: TimeSpan.Zero);

        Assert.Throws<ArgumentException>(() => context.Validate());
    }

    [Fact]
    public void FaultInjectionContext_Validate_ThrowsOnNegativeTimeout()
    {
        FaultInjectionContext context = new(
            Environment: "staging",
            Region: "us-west-2",
            ResourcePrefix: "honua",
            Credentials: new Dictionary<string, string>(),
            DryRun: false,
            Timeout: TimeSpan.FromMinutes(-1));

        Assert.Throws<ArgumentException>(() => context.Validate());
    }

    [Fact]
    public void FaultInjectionContext_Validate_SucceedsForValidContext()
    {
        FaultInjectionContext context = CreateValidContext();
        context.Validate(); // Should not throw
    }

    // ---------------------------------------------------------------
    // ScriptBasedFaultInjector script path mapping
    // ---------------------------------------------------------------

    [Theory]
    [InlineData("FAULT-001", "inject", "FAULT-001-inject.sh")]
    [InlineData("FAULT-001", "restore", "FAULT-001-restore.sh")]
    [InlineData("FAULT-001", "verify-injected", "FAULT-001-verify-injected.sh")]
    [InlineData("FAULT-001", "verify-restored", "FAULT-001-verify-restored.sh")]
    [InlineData("FAULT-009", "inject", "FAULT-009-inject.sh")]
    [InlineData("FAULT-010", "restore", "FAULT-010-restore.sh")]
    [InlineData("FAULT-015", "inject", "FAULT-015-inject.sh")]
    [InlineData("FAULT-016", "restore", "FAULT-016-restore.sh")]
    public void ScriptBasedFaultInjector_MapsScenarioIdToCorrectScriptPaths(
        string scenarioId, string action, string expectedFileName)
    {
        ScriptBasedFaultInjector injector = new(scenarioId, "aws", "/scripts/fault-injection");
        string scriptPath = injector.GetScriptPath(action);
        Assert.Equal($"/scripts/fault-injection/{expectedFileName}", scriptPath);
    }

    [Theory]
    [InlineData("FAULT-001")]
    [InlineData("FAULT-009")]
    [InlineData("FAULT-010")]
    [InlineData("FAULT-015")]
    [InlineData("FAULT-016")]
    public void SeededRealCloudScenarios_HaveFullCycleScripts(string scenarioId)
    {
        string scriptsRoot = ResolveFaultInjectionScriptsRoot();
        foreach (string action in new[] { "inject", "verify-injected", "restore", "verify-restored" })
        {
            string path = Path.Combine(scriptsRoot, $"{scenarioId}-{action}.sh");
            Assert.True(File.Exists(path), $"Missing fault-injection script: `{path}`.");
        }
    }

    [Fact]
    public void ScriptBasedFaultInjector_SetsPropertiesCorrectly()
    {
        ScriptBasedFaultInjector injector = new("FAULT-001", "aws,azure", "/scripts");
        Assert.Equal("FAULT-001", injector.ScenarioId);
        Assert.Equal("aws,azure", injector.TargetCloud);
        Assert.Equal(FaultInjectorStatus.Ready, injector.Status);
    }

    [Fact]
    public void ScriptBasedFaultInjector_ThrowsOnEmptyScenarioId()
    {
        Assert.Throws<ArgumentException>(() => new ScriptBasedFaultInjector("", "aws", "/scripts"));
    }

    [Fact]
    public void ScriptBasedFaultInjector_ThrowsOnEmptyTargetCloud()
    {
        Assert.Throws<ArgumentException>(() => new ScriptBasedFaultInjector("FAULT-001", "", "/scripts"));
    }

    [Fact]
    public void ScriptBasedFaultInjector_ThrowsOnEmptyScriptsBasePath()
    {
        Assert.Throws<ArgumentException>(() => new ScriptBasedFaultInjector("FAULT-001", "aws", ""));
    }

    // ---------------------------------------------------------------
    // ScriptBasedFaultInjector dry-run mode
    // ---------------------------------------------------------------

    [Fact]
    public async Task ScriptBasedFaultInjector_DryRun_InjectDoesNotExecute()
    {
        ScriptBasedFaultInjector injector = new("FAULT-001", "aws", "/nonexistent/path");
        FaultInjectionContext context = CreateValidContext(dryRun: true);

        FaultInjectionResult result = await injector.InjectAsync(context);

        Assert.True(result.Success);
        Assert.Equal("FAULT-001", result.ScenarioId);
        Assert.Equal(FaultInjectionAction.Inject, result.Action);
        Assert.Contains("[DRY-RUN]", result.Detail);
        Assert.NotEmpty(result.Evidence);
        Assert.All(result.Evidence, e => Assert.Contains("[DRY-RUN]", e));
    }

    [Fact]
    public async Task ScriptBasedFaultInjector_DryRun_RestoreDoesNotExecute()
    {
        ScriptBasedFaultInjector injector = new("FAULT-009", "azure", "/nonexistent/path");
        FaultInjectionContext context = CreateValidContext(dryRun: true);

        FaultInjectionResult result = await injector.RestoreAsync(context);

        Assert.True(result.Success);
        Assert.Equal("FAULT-009", result.ScenarioId);
        Assert.Equal(FaultInjectionAction.Restore, result.Action);
        Assert.Contains("[DRY-RUN]", result.Detail);
    }

    [Fact]
    public async Task ScriptBasedFaultInjector_DryRun_VerifyInjectedDoesNotExecute()
    {
        ScriptBasedFaultInjector injector = new("FAULT-010", "aws", "/nonexistent/path");
        FaultInjectionContext context = CreateValidContext(dryRun: true);

        FaultInjectionResult result = await injector.VerifyInjectedAsync(context);

        Assert.True(result.Success);
        Assert.Equal(FaultInjectionAction.VerifyInjected, result.Action);
        Assert.Contains("[DRY-RUN]", result.Detail);
    }

    [Fact]
    public async Task ScriptBasedFaultInjector_DryRun_VerifyRestoredDoesNotExecute()
    {
        ScriptBasedFaultInjector injector = new("FAULT-016", "aws,azure", "/nonexistent/path");
        FaultInjectionContext context = CreateValidContext(dryRun: true);

        FaultInjectionResult result = await injector.VerifyRestoredAsync(context);

        Assert.True(result.Success);
        Assert.Equal(FaultInjectionAction.VerifyRestored, result.Action);
        Assert.Contains("[DRY-RUN]", result.Detail);
    }

    [Fact]
    public async Task ScriptBasedFaultInjector_DryRun_IncludesEnvironmentInEvidence()
    {
        ScriptBasedFaultInjector injector = new("FAULT-001", "aws", "/nonexistent/path");
        FaultInjectionContext context = CreateValidContext(dryRun: true, environment: "staging");

        FaultInjectionResult result = await injector.InjectAsync(context);

        Assert.Contains(result.Evidence, e => e.Contains("staging"));
    }

    // ---------------------------------------------------------------
    // ScriptBasedFaultInjector missing script
    // ---------------------------------------------------------------

    [Fact]
    public async Task ScriptBasedFaultInjector_MissingScript_ReturnsFailure()
    {
        ScriptBasedFaultInjector injector = new("FAULT-999", "aws", "/nonexistent/path");
        FaultInjectionContext context = CreateValidContext(dryRun: false);

        FaultInjectionResult result = await injector.InjectAsync(context);

        Assert.False(result.Success);
        Assert.Equal(FaultInjectorStatus.Failed, injector.Status);
        Assert.Contains("not found", result.Detail);
    }

    // ---------------------------------------------------------------
    // FaultInjectionOrchestrator full cycle in dry-run
    // ---------------------------------------------------------------

    [Fact]
    public async Task FaultInjectionOrchestrator_DryRun_RunsFullCycle()
    {
        FaultScenario scenario = FaultCatalog.Resolve("FAULT-001")!;
        ScriptBasedFaultInjector injector = new("FAULT-001", "aws,azure", "/scripts/fault-injection");
        FaultInjectionOrchestrator orchestrator = new(injector, scenario);
        FaultInjectionContext context = CreateValidContext(dryRun: true);

        FaultInjectionReport report = await orchestrator.ExecuteFullCycleAsync(context);

        Assert.Equal("FAULT-001", report.ScenarioId);
        Assert.True(report.InjectionResult.Success);
        Assert.True(report.RestorationResult.Success);
        Assert.True(report.FullCycleSucceeded);
        Assert.True(report.TotalDuration > TimeSpan.Zero);

        // In dry-run, verification steps should also succeed
        Assert.NotNull(report.InjectionVerification);
        Assert.True(report.InjectionVerification!.Success);
        Assert.NotNull(report.RestorationVerification);
        Assert.True(report.RestorationVerification!.Success);
    }

    [Fact]
    public async Task FaultInjectionOrchestrator_DryRun_WithoutScenario_SkipsEvaluation()
    {
        ScriptBasedFaultInjector injector = new("FAULT-015", "aws", "/scripts/fault-injection");
        FaultInjectionOrchestrator orchestrator = new(injector);
        FaultInjectionContext context = CreateValidContext(dryRun: true);

        FaultInjectionReport report = await orchestrator.ExecuteFullCycleAsync(context);

        Assert.Equal("FAULT-015", report.ScenarioId);
        Assert.True(report.FullCycleSucceeded);
        Assert.Null(report.EvaluationResult);
    }

    [Fact]
    public async Task FaultInjectionOrchestrator_DryRun_AllActionsAreRepresented()
    {
        ScriptBasedFaultInjector injector = new("FAULT-010", "aws,azure", "/scripts/fault-injection");
        FaultInjectionOrchestrator orchestrator = new(injector);
        FaultInjectionContext context = CreateValidContext(dryRun: true);

        FaultInjectionReport report = await orchestrator.ExecuteFullCycleAsync(context);

        Assert.Equal(FaultInjectionAction.Inject, report.InjectionResult.Action);
        Assert.Equal(FaultInjectionAction.VerifyInjected, report.InjectionVerification!.Action);
        Assert.Equal(FaultInjectionAction.Restore, report.RestorationResult.Action);
        Assert.Equal(FaultInjectionAction.VerifyRestored, report.RestorationVerification!.Action);
    }

    [Fact]
    public void FaultInjectionOrchestrator_ThrowsOnNullInjector()
    {
        Assert.Throws<ArgumentNullException>(() => new FaultInjectionOrchestrator(null!));
    }

    // ---------------------------------------------------------------
    // FaultInjectionResult evidence capture
    // ---------------------------------------------------------------

    [Fact]
    public void FaultInjectionResult_CapturesAllFields()
    {
        List<string> evidence = ["stdout line 1", "stderr warning", "metric: error_rate=0.95"];

        FaultInjectionResult result = new(
            Success: true,
            ScenarioId: "FAULT-001",
            Action: FaultInjectionAction.Inject,
            Detail: "Script inject completed successfully (exit code 0)",
            Duration: TimeSpan.FromSeconds(3.5),
            Evidence: evidence);

        Assert.True(result.Success);
        Assert.Equal("FAULT-001", result.ScenarioId);
        Assert.Equal(FaultInjectionAction.Inject, result.Action);
        Assert.Contains("exit code 0", result.Detail);
        Assert.Equal(TimeSpan.FromSeconds(3.5), result.Duration);
        Assert.Equal(3, result.Evidence.Count);
        Assert.Contains("stdout line 1", result.Evidence);
        Assert.Contains("stderr warning", result.Evidence);
        Assert.Contains("metric: error_rate=0.95", result.Evidence);
    }

    [Fact]
    public void FaultInjectionResult_SupportsEmptyEvidence()
    {
        FaultInjectionResult result = new(
            Success: false,
            ScenarioId: "FAULT-009",
            Action: FaultInjectionAction.Restore,
            Detail: "Script not found",
            Duration: TimeSpan.FromMilliseconds(5),
            Evidence: []);

        Assert.False(result.Success);
        Assert.Empty(result.Evidence);
    }

    [Fact]
    public void FaultInjectionResult_RecordEquality()
    {
        List<string> evidence = ["line1"];

        FaultInjectionResult a = new(true, "FAULT-001", FaultInjectionAction.Inject, "ok", TimeSpan.FromSeconds(1), evidence);
        FaultInjectionResult b = new(true, "FAULT-001", FaultInjectionAction.Inject, "ok", TimeSpan.FromSeconds(1), evidence);

        Assert.Equal(a, b);
    }

    // ---------------------------------------------------------------
    // FaultInjectorStatus enum
    // ---------------------------------------------------------------

    [Fact]
    public void FaultInjectorStatus_HasExpectedValues()
    {
        Assert.Equal(0, (int)FaultInjectorStatus.Ready);
        Assert.Equal(1, (int)FaultInjectorStatus.Injected);
        Assert.Equal(2, (int)FaultInjectorStatus.Restored);
        Assert.Equal(3, (int)FaultInjectorStatus.Failed);
    }

    // ---------------------------------------------------------------
    // FaultInjectionAction enum
    // ---------------------------------------------------------------

    [Fact]
    public void FaultInjectionAction_HasExpectedValues()
    {
        Assert.Equal(0, (int)FaultInjectionAction.Inject);
        Assert.Equal(1, (int)FaultInjectionAction.Restore);
        Assert.Equal(2, (int)FaultInjectionAction.VerifyInjected);
        Assert.Equal(3, (int)FaultInjectionAction.VerifyRestored);
    }

    // ---------------------------------------------------------------
    // FaultInjectionReport record
    // ---------------------------------------------------------------

    [Fact]
    public void FaultInjectionReport_FullCycleSucceeded_TrueWhenBothSucceed()
    {
        FaultInjectionResult inject = new(true, "FAULT-001", FaultInjectionAction.Inject, "ok", TimeSpan.FromSeconds(1), []);
        FaultInjectionResult verifyInjected = new(true, "FAULT-001", FaultInjectionAction.VerifyInjected, "active", TimeSpan.FromSeconds(1), []);
        FaultInjectionResult restore = new(true, "FAULT-001", FaultInjectionAction.Restore, "ok", TimeSpan.FromSeconds(1), []);
        FaultInjectionResult verifyRestored = new(true, "FAULT-001", FaultInjectionAction.VerifyRestored, "restored", TimeSpan.FromSeconds(1), []);

        FaultInjectionReport report = new("FAULT-001", inject, verifyInjected, null, restore, verifyRestored, TimeSpan.FromSeconds(5));
        Assert.True(report.FullCycleSucceeded);
    }

    [Fact]
    public void FaultInjectionReport_FullCycleSucceeded_FalseWhenVerificationIsMissing()
    {
        FaultInjectionResult inject = new(true, "FAULT-001", FaultInjectionAction.Inject, "ok", TimeSpan.FromSeconds(1), []);
        FaultInjectionResult restore = new(true, "FAULT-001", FaultInjectionAction.Restore, "ok", TimeSpan.FromSeconds(1), []);

        FaultInjectionReport report = new("FAULT-001", inject, null, null, restore, null, TimeSpan.FromSeconds(5));
        Assert.False(report.FullCycleSucceeded);
    }

    [Fact]
    public void FaultInjectionReport_FullCycleSucceeded_FalseWhenInjectFails()
    {
        FaultInjectionResult inject = new(false, "FAULT-001", FaultInjectionAction.Inject, "failed", TimeSpan.FromSeconds(1), []);
        FaultInjectionResult restore = new(true, "FAULT-001", FaultInjectionAction.Restore, "ok", TimeSpan.FromSeconds(1), []);

        FaultInjectionReport report = new("FAULT-001", inject, null, null, restore, null, TimeSpan.FromSeconds(5));
        Assert.False(report.FullCycleSucceeded);
    }

    [Fact]
    public void FaultInjectionReport_FullCycleSucceeded_FalseWhenRestoreFails()
    {
        FaultInjectionResult inject = new(true, "FAULT-001", FaultInjectionAction.Inject, "ok", TimeSpan.FromSeconds(1), []);
        FaultInjectionResult restore = new(false, "FAULT-001", FaultInjectionAction.Restore, "failed", TimeSpan.FromSeconds(1), []);

        FaultInjectionReport report = new("FAULT-001", inject, null, null, restore, null, TimeSpan.FromSeconds(5));
        Assert.False(report.FullCycleSucceeded);
    }

    [Fact]
    public async Task FaultInjectionOrchestrator_FailsFullCycleWhenInjectionVerificationFails()
    {
        FakeFaultInjector injector = new(
            injectSuccess: true,
            verifyInjectedSuccess: false,
            restoreSuccess: true,
            verifyRestoredSuccess: true);
        FaultInjectionOrchestrator orchestrator = new(injector);

        FaultInjectionReport report = await orchestrator.ExecuteFullCycleAsync(CreateValidContext());

        Assert.True(report.InjectionResult.Success);
        Assert.NotNull(report.InjectionVerification);
        Assert.False(report.InjectionVerification!.Success);
        Assert.True(report.RestorationResult.Success);
        Assert.True(report.RestorationVerification!.Success);
        Assert.False(report.FullCycleSucceeded);
    }

    [Fact]
    public async Task FaultInjectionOrchestrator_FailsFullCycleWhenRestorationVerificationFails()
    {
        FakeFaultInjector injector = new(
            injectSuccess: true,
            verifyInjectedSuccess: true,
            restoreSuccess: true,
            verifyRestoredSuccess: false);
        FaultInjectionOrchestrator orchestrator = new(injector);

        FaultInjectionReport report = await orchestrator.ExecuteFullCycleAsync(CreateValidContext());

        Assert.True(report.InjectionVerification!.Success);
        Assert.True(report.RestorationResult.Success);
        Assert.False(report.RestorationVerification!.Success);
        Assert.False(report.FullCycleSucceeded);
    }

    // ---------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------

    private static FaultInjectionContext CreateValidContext(
        bool dryRun = false,
        string environment = "staging")
    {
        return new FaultInjectionContext(
            Environment: environment,
            Region: "us-west-2",
            ResourcePrefix: "honua-test",
            Credentials: new Dictionary<string, string> { ["AWS_PROFILE"] = "test" },
            DryRun: dryRun,
            Timeout: TimeSpan.FromMinutes(5));
    }

    private static string ResolveFaultInjectionScriptsRoot()
    {
        string repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        string scriptsRoot = Path.Combine(repoRoot, "scripts", "fault-injection");
        Assert.True(Directory.Exists(scriptsRoot), $"Fault-injection scripts root not found: `{scriptsRoot}`.");
        return scriptsRoot;
    }

    private sealed class FakeFaultInjector(
        bool injectSuccess,
        bool verifyInjectedSuccess,
        bool restoreSuccess,
        bool verifyRestoredSuccess) : IFaultInjector
    {
        public string ScenarioId => "FAULT-TEST";

        public string TargetCloud => "test";

        public FaultInjectorStatus Status => FaultInjectorStatus.Ready;

        public Task<FaultInjectionResult> InjectAsync(FaultInjectionContext context, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result(FaultInjectionAction.Inject, injectSuccess));
        }

        public Task<FaultInjectionResult> RestoreAsync(FaultInjectionContext context, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result(FaultInjectionAction.Restore, restoreSuccess));
        }

        public Task<FaultInjectionResult> VerifyInjectedAsync(FaultInjectionContext context, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result(FaultInjectionAction.VerifyInjected, verifyInjectedSuccess));
        }

        public Task<FaultInjectionResult> VerifyRestoredAsync(FaultInjectionContext context, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result(FaultInjectionAction.VerifyRestored, verifyRestoredSuccess));
        }

        private static FaultInjectionResult Result(FaultInjectionAction action, bool success)
        {
            return new FaultInjectionResult(
                success,
                "FAULT-TEST",
                action,
                success ? "ok" : "failed verification",
                TimeSpan.FromMilliseconds(1),
                []);
        }
    }
}
