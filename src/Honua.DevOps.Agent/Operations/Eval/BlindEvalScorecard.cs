using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.DevOps.Agent.Operations.Eval;

/// <summary>
/// The published blind-eval scorecard artifact. The wire shape is pinned by
/// <c>contracts/blind-eval-scorecard.v1.schema.json</c>, which is embedded in this
/// assembly and validated against on every write.
/// </summary>
/// <remarks>
/// NFR-001: this record carries digests, counts, and scores only. No prompt text, no
/// model transcript, and no credential ever reaches the artifact.
/// </remarks>
internal sealed record BlindEvalScorecard(
    string SchemaVersion,
    string Kind,
    string RunId,
    string Lane,
    string Provider,
    string ModelId,
    string FaultSet,
    string EvaluationMode,
    string CommitSha,
    string StartedAt,
    string CompletedAt,
    BlindEvalHarnessInfo Harness,
    BlindEvalThresholds Thresholds,
    IReadOnlyList<BlindEvalScenarioScore> Scenarios,
    BlindEvalAggregate Aggregate)
{
    internal const string CurrentSchemaVersion = "1";
    internal const string ArtifactKind = "honua-devops.blind-eval-scorecard";
}

internal sealed record BlindEvalHarnessInfo(
    string PromptBuilder,
    string Grader,
    string GraderVersion,
    int FaultCatalogSize);

internal sealed record BlindEvalThresholds(double PassRate);

internal sealed record BlindEvalScenarioScore(
    string ScenarioId,
    string ScenarioName,
    string Category,
    string RemediationScope,
    string PromptDigest,
    string ResponseDigest,
    int ResponseChars,
    double LatencySeconds,
    bool DiagnosisCorrect,
    double EvidenceQuality,
    bool RemediationSafe,
    bool PolicyCompliant,
    bool RollbackGuidanceCorrect,
    bool RecoveryVerified,
    bool ServiceHealthRestored,
    double CompositeScore,
    string Result,
    IReadOnlyList<string> FailureModes,
    string? Error = null);

internal sealed record BlindEvalAggregate(
    int ScenariosTotal,
    int ScenariosPassed,
    int ScenariosFailed,
    int ScenariosErrored,
    double PassRate,
    double MeanCompositeScore,
    string Result);

internal static class BlindEvalScorecardSerializer
{
    internal const string SchemaResourceName = "Honua.DevOps.Agent.contracts.blind-eval-scorecard.v1.schema.json";
    internal const string SchemaRelativePath = "contracts/blind-eval-scorecard.v1.schema.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };

    internal static string Serialize(BlindEvalScorecard scorecard)
    {
        return JsonSerializer.Serialize(scorecard, Options) + Environment.NewLine;
    }

    internal static string ReadSchema()
    {
        using Stream? stream = typeof(BlindEvalScorecardSerializer).Assembly
            .GetManifestResourceStream(SchemaResourceName);

        if (stream is null)
        {
            throw new InvalidOperationException(
                $"Embedded scorecard schema `{SchemaResourceName}` is missing from the assembly. "
                + $"It is linked from `{SchemaRelativePath}` by the project file.");
        }

        using StreamReader reader = new(stream);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Serializes and schema-validates the scorecard. A schema violation is a harness
    /// bug, not a model failure, so it throws rather than writing an artifact that
    /// downstream evidence aggregation cannot trust.
    /// </summary>
    internal static string SerializeValidated(BlindEvalScorecard scorecard)
    {
        string json = Serialize(scorecard);
        IReadOnlyList<string> errors = Validate(json);

        if (errors.Count > 0)
        {
            throw new InvalidOperationException(
                "Blind-eval scorecard failed contract validation against "
                + $"`{SchemaRelativePath}`: {string.Join("; ", errors)}");
        }

        return json;
    }

    internal static IReadOnlyList<string> Validate(string scorecardJson)
    {
        return JsonSchemaValidator.Validate(scorecardJson, ReadSchema());
    }
}
