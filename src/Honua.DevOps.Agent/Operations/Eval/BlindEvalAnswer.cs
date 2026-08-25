using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.DevOps.Agent.Operations.Eval;

/// <summary>
/// The structured answer the blind evaluation asks a model for. Free-form prose is
/// not gradeable deterministically, so the eval prompt pins this shape and the
/// grader scores its fields against the fault catalog.
/// </summary>
internal sealed record BlindEvalAnswer(
    string RootCause,
    IReadOnlyList<string> EvidenceCited,
    IReadOnlyList<string> RemediationSteps,
    string RollbackPlan,
    IReadOnlyList<string> VerificationSteps)
{
    internal static BlindEvalAnswer Empty { get; } = new(
        RootCause: string.Empty,
        EvidenceCited: [],
        RemediationSteps: [],
        RollbackPlan: string.Empty,
        VerificationSteps: []);
}

internal static class BlindEvalAnswerParser
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Parses the raw model answer. Returns <c>null</c> when the answer is not a
    /// usable JSON object — an unparseable answer is a model failure, not a run
    /// failure, so the caller scores it as a failed scenario.
    /// </summary>
    internal static BlindEvalAnswer? Parse(string? rawAnswer)
    {
        if (string.IsNullOrWhiteSpace(rawAnswer))
        {
            return null;
        }

        string candidate = ExtractJsonObject(rawAnswer);
        if (candidate.Length == 0)
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(candidate);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            return new BlindEvalAnswer(
                RootCause: ReadString(document.RootElement, "rootCause"),
                EvidenceCited: ReadStringArray(document.RootElement, "evidenceCited"),
                RemediationSteps: ReadStringArray(document.RootElement, "remediationSteps"),
                RollbackPlan: ReadString(document.RootElement, "rollbackPlan"),
                VerificationSteps: ReadStringArray(document.RootElement, "verificationSteps"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static string Serialize(BlindEvalAnswer answer)
    {
        return JsonSerializer.Serialize(answer, Options);
    }

    // Models routinely wrap JSON in prose or a ```json fence. Take the outermost
    // balanced object so a well-formed answer is not failed for its packaging.
    private static string ExtractJsonObject(string rawAnswer)
    {
        int start = rawAnswer.IndexOf('{');
        if (start < 0)
        {
            return string.Empty;
        }

        int depth = 0;
        bool inString = false;
        bool escaped = false;

        for (int index = start; index < rawAnswer.Length; index++)
        {
            char character = rawAnswer[index];

            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (character == '\\')
                {
                    escaped = true;
                }
                else if (character == '"')
                {
                    inString = false;
                }

                continue;
            }

            switch (character)
            {
                case '"':
                    inString = true;
                    break;
                case '{':
                    depth++;
                    break;
                case '}':
                    depth--;
                    if (depth == 0)
                    {
                        return rawAnswer[start..(index + 1)];
                    }

                    break;
            }
        }

        return string.Empty;
    }

    private static string ReadString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement value))
        {
            return [];
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            string? single = value.GetString();
            return string.IsNullOrWhiteSpace(single) ? [] : [single];
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return value.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString() ?? string.Empty)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .ToArray();
    }
}
