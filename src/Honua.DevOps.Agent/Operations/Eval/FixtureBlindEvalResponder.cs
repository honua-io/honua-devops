using System.Text.Json;
using Honua.DevOps.Agent.Operations.Troubleshooting;

namespace Honua.DevOps.Agent.Operations.Eval;

/// <summary>
/// Answers the blind evaluation from a local fixture file instead of a provider.
/// This is the seam that keeps the lane deterministically testable — including the
/// known-bad gate (a deliberately wrong fixture must produce a FAILING scorecard) —
/// and it is why a scorecard records <c>lane: "fixture"</c>: a fixture run is
/// contract evidence, never model-behavior evidence.
/// </summary>
internal sealed class FixtureBlindEvalResponder : IBlindEvalResponder
{
    private readonly IReadOnlyDictionary<string, string> _answers;
    private readonly string? _defaultAnswer;

    internal FixtureBlindEvalResponder(
        string name,
        string modelId,
        IReadOnlyDictionary<string, string> answers,
        string? defaultAnswer = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        ArgumentNullException.ThrowIfNull(answers);

        ProviderId = $"fixture:{name}";
        ModelId = modelId;
        _answers = answers;
        _defaultAnswer = defaultAnswer;
    }

    public string Lane => "fixture";

    public string ProviderId { get; }

    public string ModelId { get; }

    public Task<BlindEvalResponse> RespondAsync(
        FaultScenario scenario,
        BlindEvaluationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scenario);

        if (!_answers.TryGetValue(scenario.Id, out string? answer))
        {
            answer = _defaultAnswer;
        }

        if (answer is null)
        {
            throw new InvalidOperationException(
                $"Fixture responder `{ProviderId}` has no answer for scenario `{scenario.Id}` and no default answer.");
        }

        return Task.FromResult(new BlindEvalResponse(answer, TimeSpan.Zero));
    }

    /// <summary>
    /// Loads a fixture file of the shape
    /// <c>{ "name", "modelId", "defaultAnswer": {...}, "answers": { "FAULT-001": {...} } }</c>.
    /// An answer value may be the structured answer object or a raw string (used to
    /// exercise unparseable-answer handling).
    /// </summary>
    internal static FixtureBlindEvalResponder FromFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new InvalidOperationException($"Blind-eval fixture `{fullPath}` was not found.");
        }

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(fullPath));
        JsonElement root = document.RootElement;

        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException($"Blind-eval fixture `{fullPath}` must be a JSON object.");
        }

        string name = ReadRequiredString(root, "name", fullPath);
        string modelId = ReadRequiredString(root, "modelId", fullPath);

        Dictionary<string, string> answers = new(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("answers", out JsonElement answersElement) &&
            answersElement.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in answersElement.EnumerateObject())
            {
                answers[property.Name] = RenderAnswer(property.Value);
            }
        }

        string? defaultAnswer = root.TryGetProperty("defaultAnswer", out JsonElement defaultElement)
            ? RenderAnswer(defaultElement)
            : null;

        if (answers.Count == 0 && defaultAnswer is null)
        {
            throw new InvalidOperationException(
                $"Blind-eval fixture `{fullPath}` defines neither `answers` nor `defaultAnswer`.");
        }

        return new FixtureBlindEvalResponder(name, modelId, answers, defaultAnswer);
    }

    private static string RenderAnswer(JsonElement element)
    {
        return element.ValueKind == JsonValueKind.String
            ? element.GetString() ?? string.Empty
            : element.GetRawText();
    }

    private static string ReadRequiredString(JsonElement root, string propertyName, string path)
    {
        if (!root.TryGetProperty(propertyName, out JsonElement value) || value.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException(
                $"Blind-eval fixture `{path}` is missing required string property `{propertyName}`.");
        }

        string? text = value.GetString();
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidOperationException(
                $"Blind-eval fixture `{path}` property `{propertyName}` must not be empty.");
        }

        return text;
    }
}
