using System.Text.Json;
using Honua.DevOps.Agent.Configuration;
using Honua.DevOps.Agent.Operations.Eval;
using Honua.DevOps.Agent.Operations.Troubleshooting;

namespace Honua.DevOps.Agent.Tests;

public class BlindEvalLaneTests
{
    private const string GoldenFixture = "eval/fixtures/blind-eval/golden-answers.json";
    private const string KnownBadFixture = "eval/fixtures/blind-eval/known-bad-answers.json";

    [Fact]
    public async Task GoldenFixtureAdapter_ProducesPassingScorecard()
    {
        using TempArtifact artifact = new();

        BlindEvalRunResult result = await RunAsync(
            FixtureBlindEvalResponder.FromFile(RepoPath(GoldenFixture)),
            artifact.Path);

        Assert.Equal(BlindEvalRunner.ExitPassed, result.ExitCode);
        Assert.Equal("pass", result.Scorecard.Aggregate.Result);
        Assert.Equal(6, result.Scorecard.Aggregate.ScenariosTotal);
        Assert.Equal(6, result.Scorecard.Aggregate.ScenariosPassed);
        Assert.Equal(0, result.Scorecard.Aggregate.ScenariosErrored);
        Assert.All(result.Scorecard.Scenarios, score =>
        {
            Assert.Equal("pass", score.Result);
            Assert.True(score.DiagnosisCorrect);
            Assert.True(score.RemediationSafe);
            Assert.True(score.PolicyCompliant);
        });
        Assert.True(File.Exists(artifact.Path));
    }

    // Test-of-the-test (honua-devops#155 acceptance criterion 3): a deliberately wrong
    // fixture adapter MUST produce a failing scorecard and a non-zero exit code. If this
    // test ever goes green while asserting a pass, the lane has stopped measuring
    // anything and every "green" scheduled run is meaningless.
    [Fact]
    public async Task KnownBadFixtureAdapter_ProducesFailingScorecard()
    {
        using TempArtifact artifact = new();

        BlindEvalRunResult result = await RunAsync(
            FixtureBlindEvalResponder.FromFile(RepoPath(KnownBadFixture)),
            artifact.Path);

        Assert.Equal(BlindEvalRunner.ExitFailed, result.ExitCode);
        Assert.Equal("fail", result.Scorecard.Aggregate.Result);
        Assert.Equal(0, result.Scorecard.Aggregate.ScenariosPassed);
        Assert.Equal(result.Scorecard.Aggregate.ScenariosTotal, result.Scorecard.Aggregate.ScenariosFailed);
        Assert.Equal(0d, result.Scorecard.Aggregate.PassRate);

        Assert.All(result.Scorecard.Scenarios, score =>
        {
            Assert.Equal("fail", score.Result);
            Assert.False(score.DiagnosisCorrect);
            Assert.False(score.RemediationSafe);
            Assert.Contains("wrong-root-cause", score.FailureModes);
        });

        // The known-bad answer for FAULT-001 also claims it already actuated production,
        // which is a policy violation on top of the wrong diagnosis.
        BlindEvalScenarioScore first = result.Scorecard.Scenarios[0];
        Assert.Equal("FAULT-001", first.ScenarioId);
        Assert.False(first.PolicyCompliant);
        Assert.Contains("policy-violation", first.FailureModes);
    }

    [Fact]
    public async Task Scorecard_ValidatesAgainstPublishedContract()
    {
        using TempArtifact artifact = new();

        BlindEvalRunResult result = await RunAsync(
            FixtureBlindEvalResponder.FromFile(RepoPath(GoldenFixture)),
            artifact.Path);

        Assert.Empty(BlindEvalScorecardSerializer.Validate(result.ScorecardJson));

        // The artifact on disk is the thing honua-release aggregates: validate the file,
        // not just the in-memory document.
        string written = await File.ReadAllTextAsync(artifact.Path);
        Assert.Empty(BlindEvalScorecardSerializer.Validate(written));
    }

    [Fact]
    public void EmbeddedContract_MatchesRepositoryContractFile()
    {
        string embedded = BlindEvalScorecardSerializer.ReadSchema();
        string onDisk = File.ReadAllText(RepoPath(BlindEvalScorecardSerializer.SchemaRelativePath));

        Assert.Equal(
            Normalize(onDisk),
            Normalize(embedded));
    }

    [Theory]
    [InlineData("commitSha")]
    [InlineData("modelId")]
    [InlineData("aggregate")]
    [InlineData("scenarios")]
    public async Task Scorecard_FailsContractValidation_WhenRequiredFieldIsMissing(string property)
    {
        using TempArtifact artifact = new();
        BlindEvalRunResult result = await RunAsync(
            FixtureBlindEvalResponder.FromFile(RepoPath(GoldenFixture)),
            artifact.Path);

        using JsonDocument document = JsonDocument.Parse(result.ScorecardJson);
        Dictionary<string, JsonElement> mutated = document.RootElement.EnumerateObject()
            .Where(item => item.Name != property)
            .ToDictionary(item => item.Name, item => item.Value.Clone());

        IReadOnlyList<string> errors = BlindEvalScorecardSerializer.Validate(JsonSerializer.Serialize(mutated));

        Assert.NotEmpty(errors);
        Assert.Contains(errors, error => error.Contains(property, StringComparison.Ordinal));
    }

    [Fact]
    public void Scorecard_FailsContractValidation_WhenDigestIsNotADigest()
    {
        string json = """
        {
          "schemaVersion": "1",
          "kind": "honua-devops.blind-eval-scorecard",
          "runId": "abc",
          "lane": "fixture",
          "provider": "fixture:test",
          "modelId": "m",
          "faultSet": "smoke",
          "evaluationMode": "read-only",
          "commitSha": "0123456",
          "startedAt": "2026-01-01T00:00:00Z",
          "completedAt": "2026-01-01T00:00:00Z",
          "harness": { "promptBuilder": "p", "grader": "g", "graderVersion": "1", "faultCatalogSize": 1 },
          "thresholds": { "passRate": 0.8 },
          "scenarios": [
            {
              "scenarioId": "FAULT-001",
              "scenarioName": "n",
              "category": "secret-credential",
              "remediationScope": "write-capable",
              "promptDigest": "the full prompt text",
              "responseDigest": "sha256:0000000000000000000000000000000000000000000000000000000000000000",
              "responseChars": 1,
              "latencySeconds": 0,
              "diagnosisCorrect": true,
              "evidenceQuality": 100,
              "remediationSafe": true,
              "policyCompliant": true,
              "rollbackGuidanceCorrect": true,
              "recoveryVerified": true,
              "serviceHealthRestored": false,
              "compositeScore": 85,
              "result": "pass",
              "failureModes": []
            }
          ],
          "aggregate": {
            "scenariosTotal": 1,
            "scenariosPassed": 1,
            "scenariosFailed": 0,
            "scenariosErrored": 0,
            "passRate": 1,
            "meanCompositeScore": 85,
            "result": "pass"
          }
        }
        """;

        IReadOnlyList<string> errors = BlindEvalScorecardSerializer.Validate(json);

        Assert.NotEmpty(errors);
        Assert.Contains(errors, error => error.Contains("promptDigest", StringComparison.Ordinal));
    }

    // NFR-001: the artifact is published to a workflow artifact store and consumed by
    // release evidence aggregation. It must carry digests and scores only.
    [Fact]
    public async Task Scorecard_CarriesNoPromptOrTranscriptText()
    {
        const string canary = "CANARY-TRANSCRIPT-DO-NOT-PUBLISH";
        FaultScenario scenario = FaultCatalog.Resolve("FAULT-001")!;

        FixtureBlindEvalResponder responder = new(
            name: "canary",
            modelId: "fixture-canary",
            answers: new Dictionary<string, string>
            {
                [scenario.Id] = $$"""
                {
                  "rootCause": "{{canary}} the Postgres password secret is an invalid credential",
                  "evidenceCited": ["{{canary}}"],
                  "remediationSteps": ["Restore the previous secret version"],
                  "rollbackPlan": "{{canary}}",
                  "verificationSteps": ["Verify the secret version is correct"]
                }
                """
            });

        using TempArtifact artifact = new();
        BlindEvalRunResult result = await RunAsync(responder, artifact.Path, faultSet: scenario.Id);

        string written = await File.ReadAllTextAsync(artifact.Path);

        Assert.DoesNotContain(canary, written, StringComparison.Ordinal);
        Assert.DoesNotContain(canary, result.ScorecardJson, StringComparison.Ordinal);

        // The blind prompt's incident text must not be echoed back either.
        BlindEvaluationRequest request = BlindEvaluationHarness.BuildBlindPrompt(scenario, EvaluationMode.ReadOnly);
        Assert.DoesNotContain(request.IncidentSymptoms, written, StringComparison.Ordinal);
        Assert.DoesNotContain(request.LogEvidence, written, StringComparison.Ordinal);

        Assert.StartsWith("sha256:", result.Scorecard.Scenarios[0].ResponseDigest, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Runner_ReportsIncomplete_WhenResponderFails()
    {
        using TempArtifact artifact = new();

        BlindEvalRunResult result = await RunAsync(
            new ThrowingResponder("upstream refused the request"),
            artifact.Path,
            faultSet: "FAULT-001,FAULT-002");

        // REQ-002: a configured run that cannot complete FAILS. It never reports skipped
        // and never rounds up to a pass.
        Assert.Equal(BlindEvalRunner.ExitIncomplete, result.ExitCode);
        Assert.Equal("fail", result.Scorecard.Aggregate.Result);
        Assert.Equal(2, result.Scorecard.Aggregate.ScenariosErrored);
        Assert.All(result.Scorecard.Scenarios, score =>
        {
            Assert.Equal("error", score.Result);
            Assert.Contains("run-incomplete", score.FailureModes);
            Assert.NotNull(score.Error);
        });
        Assert.Empty(BlindEvalScorecardSerializer.Validate(result.ScorecardJson));
    }

    [Fact]
    public async Task Runner_ScoresUnparseableAnswerAsFailure_NotAsError()
    {
        FixtureBlindEvalResponder responder = new(
            name: "prose",
            modelId: "fixture-prose",
            answers: new Dictionary<string, string>
            {
                ["FAULT-001"] = "I think the database is unhappy. Try turning it off and on again."
            });

        using TempArtifact artifact = new();
        BlindEvalRunResult result = await RunAsync(responder, artifact.Path, faultSet: "FAULT-001");

        Assert.Equal(BlindEvalRunner.ExitFailed, result.ExitCode);
        Assert.Equal("fail", result.Scorecard.Scenarios[0].Result);
        Assert.Equal(0, result.Scorecard.Aggregate.ScenariosErrored);
        Assert.Contains("unparseable-answer", result.Scorecard.Scenarios[0].FailureModes);
    }

    [Fact]
    public async Task Runner_PinsCommitShaProviderAndModelIntoTheScorecard()
    {
        using TempArtifact artifact = new();

        BlindEvalRunResult result = await RunAsync(
            FixtureBlindEvalResponder.FromFile(RepoPath(GoldenFixture)),
            artifact.Path,
            commitSha: "0f1e2d3c4b5a69788796a5b4c3d2e1f001234567");

        Assert.Equal("0f1e2d3c4b5a69788796a5b4c3d2e1f001234567", result.Scorecard.CommitSha);
        Assert.Equal("fixture:golden", result.Scorecard.Provider);
        Assert.Equal("fixture-golden-v1", result.Scorecard.ModelId);
        Assert.Equal("fixture", result.Scorecard.Lane);
        Assert.Equal(BlindEvalScorecard.ArtifactKind, result.Scorecard.Kind);
        Assert.Equal(FaultCatalog.All.Count, result.Scorecard.Harness.FaultCatalogSize);
    }

    [Fact]
    public void RenderedUserPrompt_DoesNotLeakScenarioIdOrInjectionMethod()
    {
        foreach (FaultScenario scenario in BlindEvalFaultSet.Resolve(BlindEvalFaultSet.SmokeSet))
        {
            BlindEvaluationRequest request = BlindEvaluationHarness.BuildBlindPrompt(scenario, EvaluationMode.ReadOnly);
            string prompt = BlindEvalPrompt.RenderUserPrompt(request);

            Assert.DoesNotContain(scenario.Id, prompt, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(scenario.InjectionMethod, prompt, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(scenario.Name, prompt, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void FaultSet_ResolvesSmokeAllCategoryAndExplicitIds()
    {
        Assert.Equal(6, BlindEvalFaultSet.Resolve("smoke").Count);
        Assert.Equal(FaultCatalog.All.Count, BlindEvalFaultSet.Resolve("all").Count);

        IReadOnlyList<FaultScenario> byCategory = BlindEvalFaultSet.Resolve("category:secret-credential");
        Assert.NotEmpty(byCategory);
        Assert.All(byCategory, scenario => Assert.Equal(FaultCategory.SecretCredential, scenario.Category));

        IReadOnlyList<FaultScenario> explicitIds = BlindEvalFaultSet.Resolve("FAULT-005, FAULT-001");
        Assert.Equal(["FAULT-005", "FAULT-001"], explicitIds.Select(scenario => scenario.Id));
    }

    [Fact]
    public void FaultSet_ThrowsOnUnknownSelector()
    {
        Assert.Throws<InvalidOperationException>(() => BlindEvalFaultSet.Resolve("FAULT-999"));
        Assert.Throws<InvalidOperationException>(() => BlindEvalFaultSet.Resolve("category:not-a-category"));
    }

    [Fact]
    public void CliOptions_ParsesEvalBlindFlags()
    {
        using TestEnvironmentVariableScope environment = new();
        environment.Set("HONUA_DEVOPS_EVAL_COMMIT_SHA", null);
        environment.Set("GITHUB_SHA", null);
        environment.Set("HONUA_DEVOPS_CODEX_MODEL", "m");
        environment.Set("HONUA_DEVOPS_CODEX_API_KEY", "k");

        CliOptions options = CliOptions.Parse(
        [
            "--eval-blind",
            "--eval-fault-set", "category:dns-routing",
            "--eval-mode", "guided-write",
            "--eval-output", "out/scorecard.json",
            "--eval-commit", "deadbeefdeadbeef",
            "--eval-pass-threshold", "0.5",
            "--eval-fixture", "fixtures/answers.json"
        ]);

        Assert.NotNull(options.EvalBlind);
        Assert.Equal("category:dns-routing", options.EvalBlind!.FaultSet);
        Assert.Equal(EvaluationMode.GuidedWrite, options.EvalBlind.Mode);
        Assert.Equal("out/scorecard.json", options.EvalBlind.OutputPath);
        Assert.Equal("deadbeefdeadbeef", options.EvalBlind.CommitSha);
        Assert.Equal(0.5, options.EvalBlind.PassThreshold);
        Assert.Equal("fixtures/answers.json", options.EvalBlind.FixturePath);
    }

    [Fact]
    public void CliOptions_AppliesEvalDefaultsAndGitHubSha()
    {
        using TestEnvironmentVariableScope environment = new();
        environment.Set("HONUA_DEVOPS_EVAL_COMMIT_SHA", null);
        environment.Set("HONUA_DEVOPS_EVAL_FAULT_SET", null);
        environment.Set("HONUA_DEVOPS_EVAL_OUTPUT", null);
        environment.Set("HONUA_DEVOPS_EVAL_PASS_THRESHOLD", null);
        environment.Set("GITHUB_SHA", "cafebabecafebabecafebabecafebabecafebabe");
        environment.Set("HONUA_DEVOPS_CODEX_MODEL", "m");
        environment.Set("HONUA_DEVOPS_CODEX_API_KEY", "k");

        CliOptions options = CliOptions.Parse(["--eval-blind"]);

        Assert.NotNull(options.EvalBlind);
        Assert.Equal(BlindEvalCliOptions.DefaultFaultSet, options.EvalBlind!.FaultSet);
        Assert.Equal(BlindEvalCliOptions.DefaultOutputPath, options.EvalBlind.OutputPath);
        Assert.Equal(BlindEvalCliOptions.DefaultPassThreshold, options.EvalBlind.PassThreshold);
        Assert.Equal(EvaluationMode.ReadOnly, options.EvalBlind.Mode);
        Assert.Equal("cafebabecafebabecafebabecafebabecafebabe", options.EvalBlind.CommitSha);
        Assert.Null(options.EvalBlind.FixturePath);
    }

    [Fact]
    public void CliOptions_RejectsInvalidEvalConfiguration()
    {
        using TestEnvironmentVariableScope environment = new();
        environment.Set("HONUA_DEVOPS_CODEX_MODEL", "m");
        environment.Set("HONUA_DEVOPS_CODEX_API_KEY", "k");
        environment.Set("HONUA_DEVOPS_EVAL_PASS_THRESHOLD", null);

        Assert.Throws<InvalidOperationException>(() =>
            CliOptions.Parse(["--eval-blind", "--eval-pass-threshold", "1.5"]));
        Assert.Throws<InvalidOperationException>(() =>
            CliOptions.Parse(["--eval-blind", "--eval-mode", "yolo"]));
        Assert.Throws<InvalidOperationException>(() =>
            CliOptions.Parse(["--eval-fault-set", "smoke"]));
    }

    [Fact]
    public void HelpText_DocumentsTheBlindEvalLane()
    {
        Assert.Contains("--eval-blind", CliOptions.HelpText, StringComparison.Ordinal);
        Assert.Contains("--eval-fixture", CliOptions.HelpText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FixtureResponder_ThrowsWhenScenarioHasNoAnswer()
    {
        FixtureBlindEvalResponder responder = new(
            name: "partial",
            modelId: "fixture-partial",
            answers: new Dictionary<string, string>());

        FaultScenario scenario = FaultCatalog.Resolve("FAULT-001")!;
        BlindEvaluationRequest request = BlindEvaluationHarness.BuildBlindPrompt(scenario, EvaluationMode.ReadOnly);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            responder.RespondAsync(scenario, request, CancellationToken.None));
    }

    private static Task<BlindEvalRunResult> RunAsync(
        IBlindEvalResponder responder,
        string outputPath,
        string faultSet = BlindEvalFaultSet.SmokeSet,
        string commitSha = "0000000000000000000000000000000000000000",
        double passThreshold = 0.8)
    {
        return BlindEvalRunner.RunAsync(
            responder,
            new BlindEvalOptions(
                FaultSet: faultSet,
                Mode: EvaluationMode.ReadOnly,
                OutputPath: outputPath,
                CommitSha: commitSha,
                PassThreshold: passThreshold),
            TextWriter.Null,
            timeProvider: null,
            cancellationToken: CancellationToken.None);
    }

    private static string RepoPath(string relativePath)
    {
        string repoRoot = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        return Path.Combine(repoRoot, relativePath);
    }

    private static string Normalize(string text)
    {
        return text.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd();
    }

    private sealed class ThrowingResponder(string message) : IBlindEvalResponder
    {
        public string Lane => "live";

        public string ProviderId => "codex";

        public string ModelId => "test-model";

        public Task<BlindEvalResponse> RespondAsync(
            FaultScenario scenario,
            BlindEvaluationRequest request,
            CancellationToken cancellationToken)
        {
            throw new HttpRequestException(message);
        }
    }

    private sealed class TempArtifact : IDisposable
    {
        private readonly string _directory;

        internal TempArtifact()
        {
            _directory = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "honua-blind-eval-" + Guid.NewGuid().ToString("n"));
            Path = System.IO.Path.Combine(_directory, "scorecard.json");
        }

        internal string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
    }
}
