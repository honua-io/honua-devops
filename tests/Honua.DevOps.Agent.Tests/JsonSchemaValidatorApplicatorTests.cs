using Honua.DevOps.Agent.Operations.Eval;

namespace Honua.DevOps.Agent.Tests;

/// <summary>
/// The applicator and <c>format</c> keywords added so honua-devops can validate the
/// documents the honua-iac substrate emits against honua-iac's own published
/// schemas.
/// </summary>
/// <remarks>
/// The validator's contract is that it never reports "valid" against a schema it
/// only partially understands, so each new keyword needs a case proving it actually
/// rejects something — otherwise adding it to the supported set would have widened
/// what passes without widening what is checked.
/// </remarks>
public sealed class JsonSchemaValidatorApplicatorTests
{
    [Fact]
    public void AllOfWithIfThen_AppliesTheConditionalBranch()
    {
        // The shape honua-iac's operator contract uses: when status is `qualified`,
        // every immutable pin must be a real digest.
        const string schema = """
        {
          "type": "object",
          "properties": { "status": { "type": "string" }, "digest": {} },
          "allOf": [
            {
              "if": { "properties": { "status": { "const": "qualified" } }, "required": ["status"] },
              "then": { "properties": { "digest": { "type": "string", "pattern": "^[0-9a-f]{4}$" } } }
            }
          ]
        }
        """;

        Assert.Empty(JsonSchemaValidator.CheckSchemaSupport(schema));
        Assert.Empty(JsonSchemaValidator.Validate("""{"status":"qualified","digest":"abcd"}""", schema));

        // The branch fires and rejects a null pin under `qualified`...
        Assert.NotEmpty(JsonSchemaValidator.Validate("""{"status":"qualified","digest":null}""", schema));

        // ...and does not fire when the condition does not hold.
        Assert.Empty(JsonSchemaValidator.Validate("""{"status":"unqualified","digest":null}""", schema));
    }

    [Fact]
    public void PropertyNames_ConstrainsKeysRatherThanValues()
    {
        const string schema = """
        { "type": "object", "propertyNames": { "pattern": "^[a-z][a-z0-9_]*$" } }
        """;

        Assert.Empty(JsonSchemaValidator.CheckSchemaSupport(schema));
        Assert.Empty(JsonSchemaValidator.Validate("""{"admin_password":"x"}""", schema));
        Assert.NotEmpty(JsonSchemaValidator.Validate("""{"Admin-Password":"x"}""", schema));
    }

    [Fact]
    public void AdditionalPropertiesSubschema_ValidatesEveryUnlistedProperty()
    {
        const string schema = """
        {
          "type": "object",
          "properties": { "known": { "type": "string" } },
          "additionalProperties": { "type": "string", "pattern": "^arn:" }
        }
        """;

        Assert.Empty(JsonSchemaValidator.CheckSchemaSupport(schema));
        Assert.Empty(JsonSchemaValidator.Validate("""{"known":"anything","extra":"arn:aws:x"}""", schema));

        // Previously the object form was refused outright; now it is enforced.
        Assert.NotEmpty(JsonSchemaValidator.Validate("""{"extra":"not-an-arn"}""", schema));
    }

    [Fact]
    public void Format_IsEnforcedRatherThanAnnotated()
    {
        const string schema = """
        {
          "type": "object",
          "properties": {
            "when": { "type": "string", "format": "date-time" },
            "where": { "type": "string", "format": "uri" }
          }
        }
        """;

        Assert.Empty(JsonSchemaValidator.CheckSchemaSupport(schema));
        Assert.Empty(JsonSchemaValidator.Validate(
            """{"when":"2026-08-28T21:00:00Z","where":"https://honua.example.com"}""", schema));
        Assert.NotEmpty(JsonSchemaValidator.Validate("""{"when":"last tuesday"}""", schema));
        Assert.NotEmpty(JsonSchemaValidator.Validate("""{"where":"/relative/path"}""", schema));
    }

    [Fact]
    public void MaxLength_IsEnforced()
    {
        const string schema = """{ "type": "string", "maxLength": 4 }""";

        Assert.Empty(JsonSchemaValidator.CheckSchemaSupport(schema));
        Assert.Empty(JsonSchemaValidator.Validate("\"abcd\"", schema));
        Assert.NotEmpty(JsonSchemaValidator.Validate("\"abcde\"", schema));
    }

    [Fact]
    public void UnenforceableConstructsAreStillRefused()
    {
        // The fail-closed posture must survive the additions: anything the validator
        // cannot apply is reported instead of ignored.
        Assert.NotEmpty(JsonSchemaValidator.CheckSchemaSupport("""{ "oneOf": [ { "type": "string" } ] }"""));
        Assert.NotEmpty(JsonSchemaValidator.CheckSchemaSupport("""{ "anyOf": [ { "type": "string" } ] }"""));
        Assert.NotEmpty(JsonSchemaValidator.CheckSchemaSupport("""{ "not": { "type": "string" } }"""));
        Assert.NotEmpty(JsonSchemaValidator.CheckSchemaSupport("""{ "type": "number", "exclusiveMinimum": 1 }"""));

        // An unrecognized `format` is refused rather than silently unchecked.
        Assert.NotEmpty(JsonSchemaValidator.CheckSchemaSupport("""{ "type": "string", "format": "ipv6" }"""));

        // A `then` with no `if` would never be applied, so the shape is refused.
        Assert.NotEmpty(JsonSchemaValidator.CheckSchemaSupport("""{ "then": { "type": "string" } }"""));

        // A $ref with validation siblings still drops those siblings, so it stays refused.
        Assert.NotEmpty(JsonSchemaValidator.CheckSchemaSupport(
            """{ "$defs": { "a": { "type": "string" } }, "$ref": "#/$defs/a", "minLength": 2 }"""));
    }
}
