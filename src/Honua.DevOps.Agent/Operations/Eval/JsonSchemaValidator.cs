using System.Text.Json;
using System.Text.RegularExpressions;

namespace Honua.DevOps.Agent.Operations.Eval;

/// <summary>
/// A deliberately small JSON Schema validator covering the keyword subset the
/// honua-devops <c>contracts/</c> schemas use. It exists so a scorecard is
/// validated against its published contract before it is written, without
/// taking a new NuGet dependency (the repo restores in locked mode).
/// </summary>
/// <remarks>
/// <para>
/// Supported keywords: <c>$ref</c> (local <c>#/...</c> pointers only), <c>$defs</c>,
/// <c>type</c> (string or array of strings), <c>const</c>, <c>enum</c>,
/// <c>required</c>, <c>properties</c>, <c>additionalProperties</c> (boolean form
/// only), <c>items</c>, <c>minItems</c>, <c>minLength</c>, <c>minimum</c>,
/// <c>maximum</c>, and <c>pattern</c>.
/// </para>
/// <para>
/// <b>Fail-closed contract.</b> This validator never silently ignores a keyword it
/// does not implement. Before any instance is checked, the schema document is walked
/// and every property is required to be either a supported keyword or a
/// non-validating annotation on the explicit allowlist (<c>$schema</c>, <c>$id</c>,
/// <c>$comment</c>, <c>title</c>, <c>description</c>, <c>examples</c>,
/// <c>default</c>, <c>deprecated</c>, <c>readOnly</c>, <c>writeOnly</c>). Anything
/// else — <c>oneOf</c>, <c>format</c>, <c>maxLength</c>, <c>exclusiveMinimum</c>, an
/// object-form <c>additionalProperties</c>, a <c>$ref</c> with validation siblings —
/// is reported as a validation error and the instance check does not run. The
/// default posture for an unrecognized keyword is refusal, so "schema-validated on
/// write" can never quietly degrade into "partially validated" when a contract
/// outgrows this subset. <c>BlindEvalLaneTests</c> additionally walks the embedded
/// contract at test time, so such a schema edit fails <c>dotnet test</c> rather than
/// waiting until an artifact is written.
/// </para>
/// </remarks>
internal static class JsonSchemaValidator
{
    private static readonly TimeSpan PatternTimeout = TimeSpan.FromSeconds(1);

    /// <summary>Keywords this validator actually enforces.</summary>
    internal static readonly IReadOnlySet<string> SupportedKeywords = new HashSet<string>(StringComparer.Ordinal)
    {
        "$ref",
        "$defs",
        "type",
        "const",
        "enum",
        "required",
        "properties",
        "additionalProperties",
        "items",
        "minItems",
        "minLength",
        "minimum",
        "maximum",
        "pattern"
    };

    /// <summary>
    /// Non-validating annotations that are safe to ignore. Nothing may be added here
    /// that can change whether an instance is valid.
    /// </summary>
    internal static readonly IReadOnlySet<string> AllowedAnnotations = new HashSet<string>(StringComparer.Ordinal)
    {
        "$schema",
        "$id",
        "$comment",
        "title",
        "description",
        "examples",
        "default",
        "deprecated",
        "readOnly",
        "writeOnly"
    };

    internal static IReadOnlyList<string> Validate(JsonElement instance, JsonElement schema)
    {
        // Fail closed: refuse to report "valid" against a schema carrying keywords
        // this validator does not enforce. A partially-understood schema would make
        // the artifact pipeline's validation claim false without failing anything.
        IReadOnlyList<string> unsupported = CheckSchemaSupport(schema);
        if (unsupported.Count > 0)
        {
            return unsupported;
        }

        List<string> errors = [];
        ValidateNode(instance, schema, schema, "$", errors);
        return errors;
    }

    internal static IReadOnlyList<string> Validate(string instanceJson, string schemaJson)
    {
        using JsonDocument instance = JsonDocument.Parse(instanceJson);
        using JsonDocument schema = JsonDocument.Parse(schemaJson);
        return Validate(instance.RootElement, schema.RootElement);
    }

    /// <summary>
    /// Walks a schema document and reports every construct this validator cannot
    /// enforce. An empty result means the schema is fully covered by the subset.
    /// </summary>
    internal static IReadOnlyList<string> CheckSchemaSupport(JsonElement schema)
    {
        List<string> errors = [];
        CheckSchemaNode(schema, "#", errors);
        return errors;
    }

    internal static IReadOnlyList<string> CheckSchemaSupport(string schemaJson)
    {
        using JsonDocument schema = JsonDocument.Parse(schemaJson);
        return CheckSchemaSupport(schema.RootElement);
    }

    private static void CheckSchemaNode(JsonElement schema, string path, List<string> errors)
    {
        if (schema.ValueKind != JsonValueKind.Object)
        {
            // Boolean schemas and tuple-form `items` arrays are valid JSON Schema but
            // are not implemented here, so they are refused rather than skipped.
            errors.Add($"unsupported schema form `{schema.ValueKind}` at {path}; expected an object schema.");
            return;
        }

        bool hasRef = schema.TryGetProperty("$ref", out _);

        foreach (JsonProperty property in schema.EnumerateObject())
        {
            string name = property.Name;
            string childPath = $"{path}/{name}";

            if (AllowedAnnotations.Contains(name))
            {
                continue;
            }

            if (!SupportedKeywords.Contains(name))
            {
                errors.Add($"unsupported schema keyword `{name}` at {path}.");
                continue;
            }

            // `$ref` short-circuits to the referenced schema here, so a sibling
            // validation keyword would be dropped on the floor. Refuse the shape.
            if (hasRef && name != "$ref")
            {
                errors.Add(
                    $"unsupported schema keyword `{name}` alongside `$ref` at {path}; "
                    + "sibling keywords next to `$ref` are not applied.");
                continue;
            }

            switch (name)
            {
                case "properties":
                case "$defs":
                    if (property.Value.ValueKind != JsonValueKind.Object)
                    {
                        errors.Add($"`{name}` at {path} must be an object of subschemas.");
                        break;
                    }

                    foreach (JsonProperty subSchema in property.Value.EnumerateObject())
                    {
                        CheckSchemaNode(subSchema.Value, $"{childPath}/{subSchema.Name}", errors);
                    }

                    break;

                case "items":
                    CheckSchemaNode(property.Value, childPath, errors);
                    break;

                case "additionalProperties":
                    // Only the boolean form is enforced; the object (subschema) form
                    // would silently permit unvalidated properties.
                    if (property.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    {
                        errors.Add(
                            $"unsupported `additionalProperties` form at {path}; "
                            + "only the boolean form is enforced.");
                    }

                    break;
            }
        }
    }

    private static void ValidateNode(
        JsonElement instance,
        JsonElement schema,
        JsonElement root,
        string path,
        List<string> errors)
    {
        if (schema.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (schema.TryGetProperty("$ref", out JsonElement refElement) &&
            refElement.ValueKind == JsonValueKind.String)
        {
            string pointer = refElement.GetString() ?? string.Empty;
            if (!TryResolvePointer(root, pointer, out JsonElement resolved))
            {
                errors.Add($"{path}: unresolvable $ref `{pointer}`.");
                return;
            }

            ValidateNode(instance, resolved, root, path, errors);
            return;
        }

        if (schema.TryGetProperty("type", out JsonElement typeElement) &&
            !MatchesType(instance, typeElement))
        {
            errors.Add($"{path}: expected type {DescribeType(typeElement)} but found {instance.ValueKind}.");
            return;
        }

        if (schema.TryGetProperty("const", out JsonElement constElement) &&
            !JsonEquals(instance, constElement))
        {
            errors.Add($"{path}: expected const {constElement.GetRawText()}.");
        }

        if (schema.TryGetProperty("enum", out JsonElement enumElement) &&
            enumElement.ValueKind == JsonValueKind.Array &&
            !enumElement.EnumerateArray().Any(candidate => JsonEquals(instance, candidate)))
        {
            errors.Add($"{path}: value {instance.GetRawText()} is not one of {enumElement.GetRawText()}.");
        }

        switch (instance.ValueKind)
        {
            case JsonValueKind.Object:
                ValidateObject(instance, schema, root, path, errors);
                break;
            case JsonValueKind.Array:
                ValidateArray(instance, schema, root, path, errors);
                break;
            case JsonValueKind.String:
                ValidateString(instance, schema, path, errors);
                break;
            case JsonValueKind.Number:
                ValidateNumber(instance, schema, path, errors);
                break;
        }
    }

    private static void ValidateObject(
        JsonElement instance,
        JsonElement schema,
        JsonElement root,
        string path,
        List<string> errors)
    {
        if (schema.TryGetProperty("required", out JsonElement requiredElement) &&
            requiredElement.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement required in requiredElement.EnumerateArray())
            {
                string? name = required.GetString();
                if (name is not null && !instance.TryGetProperty(name, out _))
                {
                    errors.Add($"{path}: missing required property `{name}`.");
                }
            }
        }

        bool hasProperties = schema.TryGetProperty("properties", out JsonElement propertiesElement) &&
                             propertiesElement.ValueKind == JsonValueKind.Object;

        bool additionalAllowed = !schema.TryGetProperty("additionalProperties", out JsonElement additionalElement) ||
                                 additionalElement.ValueKind != JsonValueKind.False;

        foreach (JsonProperty property in instance.EnumerateObject())
        {
            if (hasProperties && propertiesElement.TryGetProperty(property.Name, out JsonElement propertySchema))
            {
                ValidateNode(property.Value, propertySchema, root, $"{path}.{property.Name}", errors);
                continue;
            }

            if (!additionalAllowed)
            {
                errors.Add($"{path}: property `{property.Name}` is not allowed by the schema.");
            }
        }
    }

    private static void ValidateArray(
        JsonElement instance,
        JsonElement schema,
        JsonElement root,
        string path,
        List<string> errors)
    {
        int length = instance.GetArrayLength();

        if (schema.TryGetProperty("minItems", out JsonElement minItems) &&
            minItems.TryGetInt32(out int minimumItems) &&
            length < minimumItems)
        {
            errors.Add($"{path}: expected at least {minimumItems} items but found {length}.");
        }

        if (!schema.TryGetProperty("items", out JsonElement itemsSchema))
        {
            return;
        }

        int index = 0;
        foreach (JsonElement item in instance.EnumerateArray())
        {
            ValidateNode(item, itemsSchema, root, $"{path}[{index}]", errors);
            index++;
        }
    }

    private static void ValidateString(JsonElement instance, JsonElement schema, string path, List<string> errors)
    {
        string value = instance.GetString() ?? string.Empty;

        if (schema.TryGetProperty("minLength", out JsonElement minLength) &&
            minLength.TryGetInt32(out int minimumLength) &&
            value.Length < minimumLength)
        {
            errors.Add($"{path}: expected minLength {minimumLength} but found {value.Length}.");
        }

        if (schema.TryGetProperty("pattern", out JsonElement pattern) &&
            pattern.ValueKind == JsonValueKind.String)
        {
            string expression = pattern.GetString() ?? string.Empty;
            if (!Regex.IsMatch(value, expression, RegexOptions.None, PatternTimeout))
            {
                errors.Add($"{path}: value does not match pattern `{expression}`.");
            }
        }
    }

    private static void ValidateNumber(JsonElement instance, JsonElement schema, string path, List<string> errors)
    {
        double value = instance.GetDouble();

        if (schema.TryGetProperty("minimum", out JsonElement minimum) &&
            minimum.TryGetDouble(out double minimumValue) &&
            value < minimumValue)
        {
            errors.Add($"{path}: expected minimum {minimumValue} but found {value}.");
        }

        if (schema.TryGetProperty("maximum", out JsonElement maximum) &&
            maximum.TryGetDouble(out double maximumValue) &&
            value > maximumValue)
        {
            errors.Add($"{path}: expected maximum {maximumValue} but found {value}.");
        }
    }

    private static bool MatchesType(JsonElement instance, JsonElement typeElement)
    {
        if (typeElement.ValueKind == JsonValueKind.String)
        {
            return MatchesTypeName(instance, typeElement.GetString());
        }

        if (typeElement.ValueKind == JsonValueKind.Array)
        {
            return typeElement.EnumerateArray().Any(candidate => MatchesTypeName(instance, candidate.GetString()));
        }

        return true;
    }

    private static bool MatchesTypeName(JsonElement instance, string? typeName)
    {
        return typeName switch
        {
            "object" => instance.ValueKind == JsonValueKind.Object,
            "array" => instance.ValueKind == JsonValueKind.Array,
            "string" => instance.ValueKind == JsonValueKind.String,
            "boolean" => instance.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "null" => instance.ValueKind == JsonValueKind.Null,
            "number" => instance.ValueKind == JsonValueKind.Number,
            "integer" => instance.ValueKind == JsonValueKind.Number && instance.TryGetInt64(out _),
            _ => true
        };
    }

    private static string DescribeType(JsonElement typeElement)
    {
        return typeElement.ValueKind == JsonValueKind.String
            ? typeElement.GetString() ?? "unknown"
            : typeElement.GetRawText();
    }

    private static bool JsonEquals(JsonElement left, JsonElement right)
    {
        if (left.ValueKind != right.ValueKind)
        {
            return false;
        }

        return left.ValueKind switch
        {
            JsonValueKind.String => string.Equals(left.GetString(), right.GetString(), StringComparison.Ordinal),
            JsonValueKind.Number => left.GetDouble().Equals(right.GetDouble()),
            JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null => true,
            _ => string.Equals(left.GetRawText(), right.GetRawText(), StringComparison.Ordinal)
        };
    }

    private static bool TryResolvePointer(JsonElement root, string pointer, out JsonElement resolved)
    {
        resolved = root;
        if (!pointer.StartsWith("#/", StringComparison.Ordinal))
        {
            return pointer == "#";
        }

        foreach (string rawSegment in pointer[2..].Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            string segment = rawSegment.Replace("~1", "/", StringComparison.Ordinal)
                                       .Replace("~0", "~", StringComparison.Ordinal);
            if (resolved.ValueKind != JsonValueKind.Object || !resolved.TryGetProperty(segment, out JsonElement next))
            {
                return false;
            }

            resolved = next;
        }

        return true;
    }
}
