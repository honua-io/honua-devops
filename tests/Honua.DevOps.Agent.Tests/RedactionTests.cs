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
}
