using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Honua.DevOps.Agent.Operations.Troubleshooting;

namespace Honua.DevOps.Agent.Operations.Eval;

internal sealed record BlindEvalOptions(
    string FaultSet,
    EvaluationMode Mode,
    string OutputPath,
    string CommitSha,
    double PassThreshold);

internal sealed record BlindEvalRunResult(int ExitCode, BlindEvalScorecard Scorecard, string ScorecardJson);

/// <summary>
/// Runs the blind fault-injection corpus through a provider seam, scores each answer
/// with <see cref="BlindEvalGrader"/>, and writes a schema-validated scorecard.
/// </summary>
/// <remarks>
/// Exit codes: <c>0</c> the aggregate passed, <c>1</c> the aggregate failed (the model
/// under-performed), <c>2</c> the run could not complete (a scenario errored). The lane
/// never reports "skipped": a configured run that cannot finish is a failure, per
/// honua-devops#155 REQ-002.
/// </remarks>
internal static class BlindEvalRunner
{
    internal const int ExitPassed = 0;
    internal const int ExitFailed = 1;
    internal const int ExitIncomplete = 2;

    internal static async Task<BlindEvalRunResult> RunAsync(
        IBlindEvalResponder responder,
        BlindEvalOptions options,
        TextWriter log,
        TimeProvider? timeProvider = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(responder);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(log);

        TimeProvider clock = timeProvider ?? TimeProvider.System;
        IReadOnlyList<FaultScenario> scenarios = BlindEvalFaultSet.Resolve(options.FaultSet);

        DateTimeOffset startedAt = clock.GetUtcNow();
        log.WriteLine(
            $"blind-eval: lane={responder.Lane} provider={responder.ProviderId} model={responder.ModelId} "
            + $"fault-set={options.FaultSet} scenarios={scenarios.Count} mode={options.Mode.ToConfigValue()}");

        List<BlindEvalScenarioScore> scores = new(scenarios.Count);

        foreach (FaultScenario scenario in scenarios)
        {
            cancellationToken.ThrowIfCancellationRequested();
            scores.Add(await ScoreScenarioAsync(responder, scenario, options, log, cancellationToken));
        }

        DateTimeOffset completedAt = clock.GetUtcNow();

        BlindEvalAggregate aggregate = Aggregate(scores, options.PassThreshold);

        BlindEvalScorecard scorecard = new(
            SchemaVersion: BlindEvalScorecard.CurrentSchemaVersion,
            Kind: BlindEvalScorecard.ArtifactKind,
            RunId: Guid.NewGuid().ToString("n"),
            Lane: responder.Lane,
            Provider: responder.ProviderId,
            ModelId: responder.ModelId,
            FaultSet: options.FaultSet,
            EvaluationMode: options.Mode.ToConfigValue(),
            CommitSha: options.CommitSha,
            StartedAt: FormatTimestamp(startedAt),
            CompletedAt: FormatTimestamp(completedAt),
            Harness: new BlindEvalHarnessInfo(
                PromptBuilder: nameof(BlindEvaluationHarness),
                Grader: BlindEvalGrader.GraderName,
                GraderVersion: BlindEvalGrader.GraderVersion,
                FaultCatalogSize: FaultCatalog.All.Count),
            Thresholds: new BlindEvalThresholds(options.PassThreshold),
            Scenarios: scores,
            Aggregate: aggregate);

        string json = BlindEvalScorecardSerializer.SerializeValidated(scorecard);
        WriteArtifact(options.OutputPath, json);

        log.WriteLine(
            $"blind-eval: {aggregate.ScenariosPassed}/{aggregate.ScenariosTotal} passed "
            + $"(failed={aggregate.ScenariosFailed} errored={aggregate.ScenariosErrored} "
            + $"passRate={aggregate.PassRate.ToString("0.00", CultureInfo.InvariantCulture)} "
            + $"threshold={options.PassThreshold.ToString("0.00", CultureInfo.InvariantCulture)}) "
            + $"result={aggregate.Result}");
        log.WriteLine($"blind-eval: scorecard written to {options.OutputPath}");

        int exitCode = aggregate.ScenariosErrored > 0
            ? ExitIncomplete
            : aggregate.Result == "pass" ? ExitPassed : ExitFailed;

        return new BlindEvalRunResult(exitCode, scorecard, json);
    }

    private static async Task<BlindEvalScenarioScore> ScoreScenarioAsync(
        IBlindEvalResponder responder,
        FaultScenario scenario,
        BlindEvalOptions options,
        TextWriter log,
        CancellationToken cancellationToken)
    {
        BlindEvaluationRequest request = BlindEvaluationHarness.BuildBlindPrompt(scenario, options.Mode);

        // The blindness invariant is the whole point of the corpus: refuse to send a
        // prompt that leaks the scenario id or the injection method to the model.
        if (!BlindEvaluationHarness.ValidateBlindness(request, scenario))
        {
            throw new InvalidOperationException(
                $"Blind prompt for `{scenario.Id}` leaks the scenario id or injection method; refusing to run.");
        }

        string promptDigest = Digest(BlindEvalPrompt.RenderUserPrompt(request));

        BlindEvalResponse response;
        try
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            response = await responder.RespondAsync(scenario, request, cancellationToken);
            stopwatch.Stop();

            if (response.Latency <= TimeSpan.Zero && stopwatch.Elapsed > TimeSpan.Zero)
            {
                response = response with { Latency = stopwatch.Elapsed };
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            // Scrub before recording: a provider exception can echo an endpoint or a
            // credential, and the artifact is published as a workflow artifact.
            string reason = Redaction.Scrub(exception.Message);
            if (string.IsNullOrWhiteSpace(reason))
            {
                reason = exception.GetType().Name;
            }
            log.WriteLine($"blind-eval: {scenario.Id} errored — {reason}");

            return new BlindEvalScenarioScore(
                ScenarioId: scenario.Id,
                ScenarioName: scenario.Name,
                Category: scenario.Category.ToConfigValue(),
                RemediationScope: scenario.RemediationScope.ToConfigValue(),
                PromptDigest: promptDigest,
                ResponseDigest: Digest(string.Empty),
                ResponseChars: 0,
                LatencySeconds: 0,
                DiagnosisCorrect: false,
                EvidenceQuality: 0,
                RemediationSafe: false,
                PolicyCompliant: false,
                RollbackGuidanceCorrect: false,
                RecoveryVerified: false,
                ServiceHealthRestored: false,
                CompositeScore: 0,
                Result: "error",
                FailureModes: ["run-incomplete"],
                Error: Truncate(reason, 300));
        }

        BlindEvalAnswer? answer = BlindEvalAnswerParser.Parse(response.RawAnswer);
        DiagnosisScorecard scorecard = BlindEvalGrader.Grade(scenario, answer, options.Mode, response.Latency);

        List<string> failureModes = [.. scorecard.FailureModes];
        if (answer is null)
        {
            failureModes.Insert(0, "unparseable-answer");
        }

        log.WriteLine(
            $"blind-eval: {scenario.Id} {scorecard.OverallResult} "
            + $"(composite={scorecard.CompositeScore.ToString("0.0", CultureInfo.InvariantCulture)} "
            + $"latency={scorecard.DiagnosisLatency})");

        return new BlindEvalScenarioScore(
            ScenarioId: scenario.Id,
            ScenarioName: scenario.Name,
            Category: scenario.Category.ToConfigValue(),
            RemediationScope: scenario.RemediationScope.ToConfigValue(),
            PromptDigest: promptDigest,
            ResponseDigest: Digest(response.RawAnswer),
            ResponseChars: response.RawAnswer?.Length ?? 0,
            LatencySeconds: Math.Round(Math.Max(response.Latency.TotalSeconds, 0), 3, MidpointRounding.AwayFromZero),
            DiagnosisCorrect: scorecard.DiagnosisCorrect,
            EvidenceQuality: scorecard.EvidenceQuality,
            RemediationSafe: scorecard.RemediationSafe,
            PolicyCompliant: scorecard.PolicyCompliant,
            RollbackGuidanceCorrect: scorecard.RollbackGuidanceCorrect,
            RecoveryVerified: scorecard.RecoveryVerified,
            ServiceHealthRestored: scorecard.ServiceHealthRestored,
            CompositeScore: Math.Round(scorecard.CompositeScore, 2, MidpointRounding.AwayFromZero),
            Result: scorecard.OverallResult,
            FailureModes: failureModes);
    }

    private static BlindEvalAggregate Aggregate(IReadOnlyList<BlindEvalScenarioScore> scores, double passThreshold)
    {
        int total = scores.Count;
        int passed = scores.Count(score => score.Result == "pass");
        int errored = scores.Count(score => score.Result == "error");
        int failed = total - passed - errored;

        double passRate = total == 0 ? 0 : (double)passed / total;
        double meanComposite = total == 0 ? 0 : scores.Average(score => score.CompositeScore);

        // Errored scenarios never round up to a pass: an incomplete run fails.
        string result = errored == 0 && passRate >= passThreshold ? "pass" : "fail";

        return new BlindEvalAggregate(
            ScenariosTotal: total,
            ScenariosPassed: passed,
            ScenariosFailed: failed,
            ScenariosErrored: errored,
            PassRate: Math.Round(passRate, 4, MidpointRounding.AwayFromZero),
            MeanCompositeScore: Math.Round(meanComposite, 2, MidpointRounding.AwayFromZero),
            Result: result);
    }

    private static void WriteArtifact(string outputPath, string json)
    {
        string fullPath = Path.GetFullPath(outputPath);
        string? directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(fullPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    internal static string Digest(string? value)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(value ?? string.Empty));
        return "sha256:" + Convert.ToHexStringLower(hash);
    }

    private static string FormatTimestamp(DateTimeOffset timestamp)
    {
        return timestamp.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
    }

    private static string Truncate(string value, int maxLength)
    {
        return value.Length <= maxLength ? value : value[..maxLength];
    }
}
