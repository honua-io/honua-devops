using Honua.DevOps.Agent.Operations.BugReport;

namespace Honua.DevOps.Agent.Tests;

public class ComponentRepoAllowlistTests
{
    [Fact]
    public void Parse_EmptyInput_YieldsEmptyAllowlist()
    {
        ComponentRepoAllowlist allowlist = ComponentRepoAllowlist.Parse(null);

        Assert.Equal(0, allowlist.Count);
        Assert.False(allowlist.TryResolve("sdk-js", out _));
    }

    [Fact]
    public void Parse_ResolvesComponentToRepo_CaseInsensitive()
    {
        ComponentRepoAllowlist allowlist = ComponentRepoAllowlist.Parse(
            "sdk-js=honua-io/honua-sdk-js, Server=honua-io/honua-server");

        Assert.Equal(2, allowlist.Count);

        Assert.True(allowlist.TryResolve("SDK-JS", out RepoRef sdk));
        Assert.Equal("honua-io", sdk.Owner);
        Assert.Equal("honua-sdk-js", sdk.Name);
        Assert.Equal("honua-io/honua-sdk-js", sdk.FullName);

        Assert.True(allowlist.TryResolve("server", out RepoRef server));
        Assert.Equal("honua-io/honua-server", server.FullName);
    }

    [Fact]
    public void TryResolve_UnknownOrBlankComponent_ReturnsFalse()
    {
        ComponentRepoAllowlist allowlist = ComponentRepoAllowlist.Parse("sdk-js=honua-io/honua-sdk-js");

        Assert.False(allowlist.TryResolve("unknown", out _));
        Assert.False(allowlist.TryResolve("", out _));
        Assert.False(allowlist.TryResolve("   ", out _));
        Assert.False(allowlist.TryResolve(null, out _));
    }

    [Theory]
    [InlineData("sdk-js")]                      // no '='
    [InlineData("sdk-js=")]                     // empty repo
    [InlineData("sdk-js=honua-sdk-js")]         // repo missing owner
    [InlineData("sdk-js=honua-io/repo/extra")]  // too many segments
    [InlineData("=honua-io/repo")]              // empty component
    public void Parse_MalformedEntry_Throws(string raw)
    {
        Assert.Throws<InvalidOperationException>(() => ComponentRepoAllowlist.Parse(raw));
    }

    [Fact]
    public void Parse_DuplicateComponent_Throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
            ComponentRepoAllowlist.Parse("sdk-js=honua-io/a,SDK-JS=honua-io/b"));
    }
}
