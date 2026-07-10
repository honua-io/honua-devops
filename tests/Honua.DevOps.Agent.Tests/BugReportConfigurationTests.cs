using Honua.DevOps.Agent.Operations.BugReport;

namespace Honua.DevOps.Agent.Tests;

public class BugReportConfigurationTests
{
    [Fact]
    public void Load_DefaultsToDisabledWhenSecretUnset()
    {
        using TestEnvironmentVariableScope scope = NewScope();

        BugReportConfiguration configuration = BugReportConfiguration.Load();

        Assert.False(configuration.IsEnabled);
        Assert.Equal(BugReportConfiguration.DefaultPort, configuration.Port);
        Assert.Equal(BugReportConfiguration.DefaultPath, configuration.Path);
        Assert.Equal(TimeSpan.FromSeconds(BugReportConfiguration.DefaultReplayWindowSeconds), configuration.ReplayWindow);
        Assert.Equal(0, configuration.Allowlist.Count);
        Assert.Equal(BugReportConfiguration.DefaultLabels, configuration.Labels);
        Assert.Null(configuration.GitHubApiBaseUri);
        Assert.Null(configuration.GitHubToken);
        Assert.Empty(configuration.AllowedHosts);
    }

    [Fact]
    public void Load_ParsesFullConfiguration()
    {
        using TestEnvironmentVariableScope scope = NewScope();
        scope.Set("HONUA_DEVOPS_BUGREPORT_WEBHOOK_SECRET", "s3cret");
        scope.Set("HONUA_DEVOPS_BUGREPORT_PORT", "9192");
        scope.Set("HONUA_DEVOPS_BUGREPORT_PATH", "/bugs");
        scope.Set("HONUA_DEVOPS_BUGREPORT_REPLAY_WINDOW_SECONDS", "120");
        scope.Set("HONUA_DEVOPS_BUGREPORT_COMPONENT_MAP", "sdk-js=honua-io/honua-sdk-js,server=honua-io/honua-server");
        scope.Set("HONUA_DEVOPS_BUGREPORT_LABELS", "bug, support-routed");
        scope.Set("HONUA_DEVOPS_GITHUB_API_BASE_URL", "https://api.github.com");
        scope.Set("HONUA_DEVOPS_GITHUB_TOKEN", "ghp_x");
        scope.Set("HONUA_DEVOPS_BUGREPORT_ALLOWED_HOSTS", "api.github.com, GHE.example.com");

        BugReportConfiguration configuration = BugReportConfiguration.Load();

        Assert.True(configuration.IsEnabled);
        Assert.Equal("s3cret", configuration.WebhookSecret);
        Assert.Equal(9192, configuration.Port);
        Assert.Equal("/bugs", configuration.Path);
        Assert.Equal(TimeSpan.FromSeconds(120), configuration.ReplayWindow);
        Assert.Equal(2, configuration.Allowlist.Count);
        Assert.True(configuration.Allowlist.TryResolve("sdk-js", out RepoRef repo));
        Assert.Equal("honua-io/honua-sdk-js", repo.FullName);
        Assert.Equal(new[] { "bug", "support-routed" }, configuration.Labels);
        Assert.Equal(new Uri("https://api.github.com"), configuration.GitHubApiBaseUri);
        Assert.Equal("ghp_x", configuration.GitHubToken);
        Assert.Contains("api.github.com", configuration.AllowedHosts);
        Assert.Contains("ghe.example.com", configuration.AllowedHosts);
    }

    [Fact]
    public void Load_RejectsRelativePath()
    {
        using TestEnvironmentVariableScope scope = NewScope();
        scope.Set("HONUA_DEVOPS_BUGREPORT_WEBHOOK_SECRET", "x");
        scope.Set("HONUA_DEVOPS_BUGREPORT_PATH", "bugs");

        Assert.Throws<InvalidOperationException>(BugReportConfiguration.Load);
    }

    [Fact]
    public void Load_RejectsInvalidPort()
    {
        using TestEnvironmentVariableScope scope = NewScope();
        scope.Set("HONUA_DEVOPS_BUGREPORT_WEBHOOK_SECRET", "x");
        scope.Set("HONUA_DEVOPS_BUGREPORT_PORT", "0");

        Assert.Throws<InvalidOperationException>(BugReportConfiguration.Load);
    }

    [Fact]
    public void Load_RejectsNonHttpsGitHubUrlForNonLocalHost()
    {
        using TestEnvironmentVariableScope scope = NewScope();
        scope.Set("HONUA_DEVOPS_BUGREPORT_WEBHOOK_SECRET", "x");
        scope.Set("HONUA_DEVOPS_GITHUB_API_BASE_URL", "http://api.github.com");

        Assert.Throws<InvalidOperationException>(BugReportConfiguration.Load);
    }

    [Fact]
    public void Load_RejectsMalformedComponentMap()
    {
        using TestEnvironmentVariableScope scope = NewScope();
        scope.Set("HONUA_DEVOPS_BUGREPORT_WEBHOOK_SECRET", "x");
        scope.Set("HONUA_DEVOPS_BUGREPORT_COMPONENT_MAP", "sdk-js");

        Assert.Throws<InvalidOperationException>(BugReportConfiguration.Load);
    }

    private static TestEnvironmentVariableScope NewScope()
    {
        TestEnvironmentVariableScope scope = new();
        scope.Set("HONUA_DEVOPS_BUGREPORT_WEBHOOK_SECRET", null);
        scope.Set("HONUA_DEVOPS_BUGREPORT_PORT", null);
        scope.Set("HONUA_DEVOPS_BUGREPORT_PATH", null);
        scope.Set("HONUA_DEVOPS_BUGREPORT_REPLAY_WINDOW_SECONDS", null);
        scope.Set("HONUA_DEVOPS_BUGREPORT_COMPONENT_MAP", null);
        scope.Set("HONUA_DEVOPS_BUGREPORT_LABELS", null);
        scope.Set("HONUA_DEVOPS_GITHUB_API_BASE_URL", null);
        scope.Set("HONUA_DEVOPS_GITHUB_TOKEN", null);
        scope.Set("HONUA_DEVOPS_BUGREPORT_ALLOWED_HOSTS", null);
        return scope;
    }
}
