using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Honua.DevOps.Agent.Operations;

/// <summary>The IaC v1 Python json.dumps(sort_keys=True, separators=(',', ':'), ensure_ascii=True) digest.</summary>
internal static class ExactPlanCanonicalDigest
{
    internal static string Compute(JsonElement metadata)
    {
        if (metadata.ValueKind != JsonValueKind.Object ||
            !metadata.TryGetProperty("plan_metadata_digest", out var field) ||
            field.ValueKind != JsonValueKind.String)
            throw new JsonException("Exact-plan metadata has no digest field.");
        var canonical = new StringBuilder();
        WriteObject(metadata, canonical, omitDigest: true);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }

    internal static bool Matches(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            string? claimed = document.RootElement.GetProperty("plan_metadata_digest").GetString();
            return claimed is not null && string.Equals(Compute(document.RootElement), claimed, StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return false;
        }
    }

    private static void Write(JsonElement element, StringBuilder output)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                WriteObject(element, output, false);
                break;
            case JsonValueKind.Array:
                output.Append('[');
                bool first = true;
                foreach (var item in element.EnumerateArray())
                {
                    if (!first) output.Append(',');
                    first = false;
                    Write(item, output);
                }
                output.Append(']');
                break;
            case JsonValueKind.String:
                WriteString(element.GetString()!, output);
                break;
            case JsonValueKind.Number:
                string raw = element.GetRawText();
                int start = raw.Length > 0 && raw[0] == '-' ? 1 : 0;
                if (raw.Length <= start || !raw.AsSpan(start).ToString().All(char.IsAsciiDigit) ||
                    !element.TryGetInt64(out long number))
                    throw new JsonException("Exact-plan metadata has a non-integer number.");
                output.Append(number.ToString(CultureInfo.InvariantCulture));
                break;
            case JsonValueKind.True: output.Append("true"); break;
            case JsonValueKind.False: output.Append("false"); break;
            case JsonValueKind.Null: output.Append("null"); break;
            default: throw new JsonException("Exact-plan metadata has unsupported JSON.");
        }
    }

    private static void WriteObject(JsonElement element, StringBuilder output, bool omitDigest)
    {
        output.Append('{');
        bool first = true;
        foreach (var property in element.EnumerateObject()
                     .Where(property => !omitDigest || property.Name != "plan_metadata_digest")
                     .OrderBy(property => property.Name, StringComparer.Ordinal))
        {
            if (!first) output.Append(',');
            first = false;
            WriteString(property.Name, output);
            output.Append(':');
            Write(property.Value, output);
        }
        output.Append('}');
    }

    private static void WriteString(string value, StringBuilder output)
    {
        output.Append('"');
        foreach (char character in value)
        {
            switch (character)
            {
                case '"': output.Append("\\\""); break;
                case '\\': output.Append("\\\\"); break;
                case '\b': output.Append("\\b"); break;
                case '\f': output.Append("\\f"); break;
                case '\n': output.Append("\\n"); break;
                case '\r': output.Append("\\r"); break;
                case '\t': output.Append("\\t"); break;
                default:
                    if (character < 0x20 || character > 0x7e)
                    {
                        output.Append("\\u");
                        output.Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else output.Append(character);
                    break;
            }
        }
        output.Append('"');
    }
}
