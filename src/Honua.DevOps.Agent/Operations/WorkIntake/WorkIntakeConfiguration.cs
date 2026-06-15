using System.Net;

namespace Honua.DevOps.Agent.Operations.WorkIntake;

/// <summary>
/// Selected work-intake provider. Default is <see cref="None"/> so the intake
/// capability is off until explicitly configured, matching the repo's
/// default-safe posture.
/// </summary>
internal enum IntakeProvider
{
    None = 0,
    Jira = 1
}

/// <summary>
/// Environment-driven configuration for the work-intake connector. Mirrors the
/// env-var conventions of <see cref="BackendConfiguration"/> and
/// <see cref="WebhookListenerConfiguration"/>: typed loader, validation, and
/// https-required-for-non-local on the Jira base URL.
///
/// Default-off: when <see cref="Provider"/> is <see cref="IntakeProvider.None"/>
/// the intake listener is "intake-disabled" and must not start.
/// </summary>
internal sealed record WorkIntakeConfiguration(
    IntakeProvider Provider,
    string WebhookSecret,
    int Port,
    string Path,
    IReadOnlyList<string> AllowedHosts,
    bool AutoDraft,
    Uri? JiraBaseUri,
    string? JiraApiToken,
    string? JiraUserEmail,
    string? ProjectFilter)
{
    private const string ProviderVariable = "HONUA_DEVOPS_INTAKE_PROVIDER";
    private const string WebhookSecretVariable = "HONUA_DEVOPS_INTAKE_WEBHOOK_SECRET";
    private const string PortVariable = "HONUA_DEVOPS_INTAKE_PORT";
    private const string PathVariable = "HONUA_DEVOPS_INTAKE_PATH";
    private const string AllowedHostsVariable = "HONUA_DEVOPS_INTAKE_ALLOWED_HOSTS";
    private const string AutoDraftVariable = "HONUA_DEVOPS_INTAKE_AUTO_DRAFT";

    private const string JiraBaseUrlVariable = "HONUA_DEVOPS_JIRA_BASE_URL";
    private const string JiraApiTokenVariable = "HONUA_DEVOPS_JIRA_API_TOKEN";
    private const string JiraUserEmailVariable = "HONUA_DEVOPS_JIRA_USER_EMAIL";
    private const string JiraProjectFilterVariable = "HONUA_DEVOPS_JIRA_PROJECT_FILTER";

    internal const int DefaultPort = 8091;
    internal const string DefaultPath = "/intake";
    internal const bool DefaultAutoDraft = false;

    internal bool IsEnabled => Provider != IntakeProvider.None;

    internal static WorkIntakeConfiguration Load()
    {
        IntakeProvider provider = ParseProvider(Environment.GetEnvironmentVariable(ProviderVariable));

        string? rawSecret = Environment.GetEnvironmentVariable(WebhookSecretVariable);
        if (provider != IntakeProvider.None && string.IsNullOrWhiteSpace(rawSecret))
        {
            throw new InvalidOperationException(
                $"Environment variable `{WebhookSecretVariable}` must be set to the shared work-intake webhook secret when `{ProviderVariable}` is `jira`.");
        }

        int port = ParsePort(Environment.GetEnvironmentVariable(PortVariable));
        string path = ParsePath(Environment.GetEnvironmentVariable(PathVariable));
        IReadOnlyList<string> allowedHosts = ParseAllowedHosts(Environment.GetEnvironmentVariable(AllowedHostsVariable));
        bool autoDraft = ParseBoolean(Environment.GetEnvironmentVariable(AutoDraftVariable), DefaultAutoDraft, AutoDraftVariable);

        Uri? jiraBaseUri = ParseOptionalBaseUri(Environment.GetEnvironmentVariable(JiraBaseUrlVariable), JiraBaseUrlVariable);
        string? jiraApiToken = Normalize(Environment.GetEnvironmentVariable(JiraApiTokenVariable));
        string? jiraUserEmail = Normalize(Environment.GetEnvironmentVariable(JiraUserEmailVariable));
        string? projectFilter = Normalize(Environment.GetEnvironmentVariable(JiraProjectFilterVariable));

        return new WorkIntakeConfiguration(
            Provider: provider,
            WebhookSecret: rawSecret?.Trim() ?? string.Empty,
            Port: port,
            Path: path,
            AllowedHosts: allowedHosts,
            AutoDraft: autoDraft,
            JiraBaseUri: jiraBaseUri,
            JiraApiToken: jiraApiToken,
            JiraUserEmail: jiraUserEmail,
            ProjectFilter: projectFilter);
    }

    private static IntakeProvider ParseProvider(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return IntakeProvider.None;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "none" => IntakeProvider.None,
            "jira" => IntakeProvider.Jira,
            _ => throw new InvalidOperationException(
                $"Environment variable `{ProviderVariable}` must be `jira` or `none`.")
        };
    }

    private static int ParsePort(string? rawPort)
    {
        if (string.IsNullOrWhiteSpace(rawPort))
        {
            return DefaultPort;
        }

        if (!int.TryParse(rawPort.Trim(), out int port) || port is < 1 or > 65535)
        {
            throw new InvalidOperationException(
                $"Environment variable `{PortVariable}` must be a TCP port between 1 and 65535.");
        }

        return port;
    }

    private static string ParsePath(string? rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            return DefaultPath;
        }

        string trimmedPath = rawPath.Trim();
        if (!trimmedPath.StartsWith('/'))
        {
            throw new InvalidOperationException(
                $"Environment variable `{PathVariable}` must start with `/`.");
        }

        if (trimmedPath.Contains("//", StringComparison.Ordinal) || trimmedPath.Contains('\\'))
        {
            throw new InvalidOperationException(
                $"Environment variable `{PathVariable}` must be a single normalized URL path.");
        }

        return trimmedPath;
    }

    private static bool ParseBoolean(string? value, bool fallback, string variableName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        if (bool.TryParse(value.Trim(), out bool parsed))
        {
            return parsed;
        }

        throw new InvalidOperationException(
            $"Environment variable `{variableName}` must be `true` or `false`.");
    }

    private static IReadOnlyList<string> ParseAllowedHosts(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Array.Empty<string>();
        }

        return value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(host => host.ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static Uri? ParseOptionalBaseUri(string? value, string variableName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string candidate = value.Trim();
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out Uri? uri))
        {
            throw new InvalidOperationException(
                $"Environment variable `{variableName}` must be a valid absolute URL.");
        }

        if (!uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Environment variable `{variableName}` must use http or https.");
        }

        if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new InvalidOperationException(
                $"Environment variable `{variableName}` must not include query string or fragment.");
        }

        if (!IsLocalUri(uri) && !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Environment variable `{variableName}` must use https for non-local endpoints.");
        }

        return uri;
    }

    private static bool IsLocalUri(Uri uri)
    {
        if (uri.IsLoopback)
        {
            return true;
        }

        if (IPAddress.TryParse(uri.Host, out IPAddress? address))
        {
            return IPAddress.IsLoopback(address);
        }

        return uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase);
    }

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
