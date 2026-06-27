using Honua.DevOps.Agent.Operations;

namespace Honua.DevOps.Agent.Tests;

public class RedactionTests
{
    [Theory]
    [InlineData("apiKey=hunter2", "hunter2")]
    [InlineData("api_key: hunter2", "hunter2")]
    [InlineData("X-API-Key=hunter2", "hunter2")]
    [InlineData("\"apiKey\":\"hunter2\"", "hunter2")]
    [InlineData("authorization=Bearer abc.def.ghi", "abc.def.ghi")]
    [InlineData("password=letmein", "letmein")]
    [InlineData("token: shortsecret", "shortsecret")]
    public void Scrub_RemovesSensitiveValues(string input, string secret)
    {
        string scrubbed = Redaction.Scrub(input);
        Assert.DoesNotContain(secret, scrubbed, StringComparison.Ordinal);
        Assert.Contains("<redacted>", scrubbed, StringComparison.Ordinal);
    }

    [Fact]
    public void Scrub_RedactsQueryStringSecrets()
    {
        string input = "GET https://example.com/path?service=roads&api_key=hunter2&token=xyz HTTP/1.1";
        string scrubbed = Redaction.Scrub(input);
        Assert.DoesNotContain("hunter2", scrubbed);
        Assert.DoesNotContain("xyz", scrubbed);
        Assert.Contains("api_key=<redacted>", scrubbed);
        Assert.Contains("token=<redacted>", scrubbed);
        Assert.Contains("service=roads", scrubbed);
    }

    [Fact]
    public void Scrub_RedactsBearerHeader()
    {
        string scrubbed = Redaction.Scrub("Authorization: Bearer eyJhbGciOiJIUzI1NiJ9.PAYLOAD.sig");
        Assert.DoesNotContain("eyJhbGciOiJIUzI1NiJ9", scrubbed);
        Assert.DoesNotContain("PAYLOAD", scrubbed);
        Assert.Contains("<redacted>", scrubbed);
    }

    [Fact]
    public void Scrub_LeavesUnrelatedTextAlone()
    {
        const string input = "service=roads-api environment=prod errors=124";
        Assert.Equal(input, Redaction.Scrub(input));
    }

    [Fact]
    public void Scrub_HandlesNullAndEmpty()
    {
        Assert.Equal(string.Empty, Redaction.Scrub(null));
        Assert.Equal(string.Empty, Redaction.Scrub(string.Empty));
    }

    [Theory]
    [InlineData("apiKey")]
    [InlineData("api_key")]
    [InlineData("X-API-Key")]
    [InlineData("token")]
    [InlineData("access_token")]
    [InlineData("secret")]
    [InlineData("password")]
    [InlineData("authorization")]
    public void ScrubValue_RedactsBareValueUnderSensitiveKey(string key)
    {
        // A value passed under a sensitive key has no inline `key=` prefix for the
        // content scrubber to match, so the key itself must trigger redaction.
        const string secret = "rawsecretvalue";
        string scrubbed = Redaction.ScrubValue(key, secret);
        Assert.DoesNotContain(secret, scrubbed, StringComparison.Ordinal);
        Assert.Equal("<redacted>", scrubbed);
    }

    [Fact]
    public void ScrubValue_LeavesNonSensitiveKeyValueButStillScrubsInlineSecrets()
    {
        Assert.Equal("roads-api", Redaction.ScrubValue("service", "roads-api"));
        Assert.DoesNotContain("hunter2", Redaction.ScrubValue("note", "api_key=hunter2"));
    }

    [Fact]
    public void IsSensitiveKey_DistinguishesSecretKeysFromOrdinaryOnes()
    {
        Assert.True(Redaction.IsSensitiveKey("apiKey"));
        Assert.True(Redaction.IsSensitiveKey("PASSWORD"));
        Assert.False(Redaction.IsSensitiveKey("service"));
        Assert.False(Redaction.IsSensitiveKey(null));
    }
}
