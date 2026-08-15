using Honua.DevOps.Agent.Operations.WorkIntake;

namespace Honua.DevOps.Agent.Tests;

public class WorkIntakeConfigurationTests
{
    [Fact]
    public void Load_DefaultsToDisabledWhenProviderUnset()
    {
        using TestEnvironmentVariableScope scope = NewScope();
        scope.Set("HONUA_DEVOPS_INTAKE_PROVIDER", null);

        WorkIntakeConfiguration configuration = WorkIntakeConfiguration.Load();

        Assert.Equal(IntakeProvider.None, configuration.Provider);
        Assert.False(configuration.IsEnabled);
        Assert.Equal(WorkIntakeConfiguration.DefaultPort, configuration.Port);
        Assert.Equal(WorkIntakeConfiguration.DefaultPath, configuration.Path);
        Assert.False(configuration.AutoDraft);
        Assert.Empty(configuration.AllowedHosts);
    }

    [Fact]
    public void Load_RequiresWebhookSecretWhenJiraSelected()
    {
        using TestEnvironmentVariableScope scope = NewScope();
        scope.Set("HONUA_DEVOPS_INTAKE_PROVIDER", "jira");
        scope.Set("HONUA_DEVOPS_INTAKE_WEBHOOK_SECRET", null);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(WorkIntakeConfiguration.Load);
        Assert.Contains("HONUA_DEVOPS_INTAKE_WEBHOOK_SECRET", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_ParsesJiraConfiguration()
    {
        using TestEnvironmentVariableScope scope = NewScope();
        scope.Set("HONUA_DEVOPS_INTAKE_PROVIDER", "jira");
        scope.Set("HONUA_DEVOPS_INTAKE_WEBHOOK_SECRET", "s3cret");
        scope.Set("HONUA_DEVOPS_INTAKE_PORT", "9099");
        scope.Set("HONUA_DEVOPS_INTAKE_PATH", "/jira-intake");
        scope.Set("HONUA_DEVOPS_INTAKE_ALLOWED_HOSTS", "acme.atlassian.net, Other.Atlassian.NET");
        scope.Set("HONUA_DEVOPS_INTAKE_AUTO_DRAFT", "true");
        scope.Set("HONUA_DEVOPS_JIRA_BASE_URL", "https://acme.atlassian.net");
        scope.Set("HONUA_DEVOPS_JIRA_API_TOKEN", "tok");
        scope.Set("HONUA_DEVOPS_JIRA_USER_EMAIL", "ops@acme.test");
        scope.Set("HONUA_DEVOPS_JIRA_PROJECT_FILTER", "GIS");

        WorkIntakeConfiguration configuration = WorkIntakeConfiguration.Load();

        Assert.Equal(IntakeProvider.Jira, configuration.Provider);
        Assert.True(configuration.IsEnabled);
        Assert.Equal("s3cret", configuration.WebhookSecret);
        Assert.Equal(9099, configuration.Port);
        Assert.Equal("/jira-intake", configuration.Path);
        Assert.Equal(2, configuration.AllowedHosts.Count);
        Assert.Contains("acme.atlassian.net", configuration.AllowedHosts);
        Assert.Contains("other.atlassian.net", configuration.AllowedHosts);
        Assert.True(configuration.AutoDraft);
        Assert.Equal(new Uri("https://acme.atlassian.net"), configuration.JiraBaseUri);
        Assert.Equal("tok", configuration.JiraApiToken);
        Assert.Equal("ops@acme.test", configuration.JiraUserEmail);
        Assert.Equal("GIS", configuration.ProjectFilter);
    }

    [Fact]
    public void Load_RejectsUnknownProvider()
    {
        using TestEnvironmentVariableScope scope = NewScope();
        scope.Set("HONUA_DEVOPS_INTAKE_PROVIDER", "servicenow");

        Assert.Throws<InvalidOperationException>(WorkIntakeConfiguration.Load);
    }

    [Fact]
    public void Load_RejectsRelativePath()
    {
        using TestEnvironmentVariableScope scope = NewScope();
        scope.Set("HONUA_DEVOPS_INTAKE_PROVIDER", "jira");
        scope.Set("HONUA_DEVOPS_INTAKE_WEBHOOK_SECRET", "x");
        scope.Set("HONUA_DEVOPS_INTAKE_PATH", "intake");

        Assert.Throws<InvalidOperationException>(WorkIntakeConfiguration.Load);
    }

    [Fact]
    public void Load_RejectsInvalidPort()
    {
        using TestEnvironmentVariableScope scope = NewScope();
        scope.Set("HONUA_DEVOPS_INTAKE_PROVIDER", "jira");
        scope.Set("HONUA_DEVOPS_INTAKE_WEBHOOK_SECRET", "x");
        scope.Set("HONUA_DEVOPS_INTAKE_PORT", "0");

        Assert.Throws<InvalidOperationException>(WorkIntakeConfiguration.Load);
    }

    [Fact]
    public void Load_RejectsNonHttpsJiraBaseUrlForNonLocalHost()
    {
        using TestEnvironmentVariableScope scope = NewScope();
        scope.Set("HONUA_DEVOPS_INTAKE_PROVIDER", "jira");
        scope.Set("HONUA_DEVOPS_INTAKE_WEBHOOK_SECRET", "x");
        scope.Set("HONUA_DEVOPS_JIRA_BASE_URL", "http://acme.atlassian.net");

        Assert.Throws<InvalidOperationException>(WorkIntakeConfiguration.Load);
    }

    // Reset every intake-related variable so ambient env/.env state never leaks
    // into a test's expectations.
    private static TestEnvironmentVariableScope NewScope()
    {
        TestEnvironmentVariableScope scope = new();
        scope.Set("HONUA_DEVOPS_INTAKE_PROVIDER", null);
        scope.Set("HONUA_DEVOPS_INTAKE_WEBHOOK_SECRET", null);
        scope.Set("HONUA_DEVOPS_INTAKE_PORT", null);
        scope.Set("HONUA_DEVOPS_INTAKE_PATH", null);
        scope.Set("HONUA_DEVOPS_INTAKE_ALLOWED_HOSTS", null);
        scope.Set("HONUA_DEVOPS_INTAKE_AUTO_DRAFT", null);
        scope.Set("HONUA_DEVOPS_JIRA_BASE_URL", null);
        scope.Set("HONUA_DEVOPS_JIRA_API_TOKEN", null);
        scope.Set("HONUA_DEVOPS_JIRA_USER_EMAIL", null);
        scope.Set("HONUA_DEVOPS_JIRA_PROJECT_FILTER", null);
        return scope;
    }
}
