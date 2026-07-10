using System.Net;

namespace Honua.DevOps.Agent.Operations.BugReport;

/// <summary>
/// Environment-driven configuration for the bug-report issue adapter. Mirrors the
/// conventions of <see cref="WebhookListenerConfiguration"/> and
/// <see cref="Honua.DevOps.Agent.Operations.WorkIntake.WorkIntakeConfiguration"/>:
/// typed loader, path/port validation, https-required-for-non-local URLs, and a
/// default-off posture.
///
/// Default-off: the adapter is enabled only once the shared webhook secret is
/// configured. Issue <em>filing</em> is a further, independent opt-in — the
/// server-owned allowlist plus a GitHub token and host allowlist must all be set
/// or the adapter stays in a plan/report posture (sanitized issue prepared and
/// logged, never filed).
/// </summary>
internal sealed record BugReportConfiguration(
    string WebhookSecret,
    int Port,
    string Path,
    TimeSpan ReplayWindow,
    ComponentRepoAllowlist Allowlist,
    IReadOnlyList<string> Labels,
    Uri? GitHubApiBaseUri,
    string? GitHubToken,
    IReadOnlyList<string> AllowedHosts)
{
    private const string WebhookSecretVariable = "HONUA_DEVOPS_BUGREPORT_WEBHOOK_SECRET";
    private const string PortVariable = "HONUA_DEVOPS_BUGREPORT_PORT";
    private const string PathVariable = "HONUA_DEVOPS_BUGREPORT_PATH";
    private const string ReplayWindowVariable = "HONUA_DEVOPS_BUGREPORT_REPLAY_WINDOW_SECONDS";
    private const string ComponentMapVariable = "HONUA_DEVOPS_BUGREPORT_COMPONENT_MAP";
    private const string LabelsVariable = "HONUA_DEVOPS_BUGREPORT_LABELS";
    private const string AllowedHostsVariable = "HONUA_DEVOPS_BUGREPORT_ALLOWED_HOSTS";
    private const string GitHubBaseUrlVariable = "HONUA_DEVOPS_GITHUB_API_BASE_URL";
    private const string GitHubTokenVariable = "HONUA_DEVOPS_GITHUB_TOKEN";

    internal const int DefaultPort = 8092;
    internal const string DefaultPath = "/bug-reports";
    internal const int DefaultReplayWindowSeconds = 300;
    internal const string DefaultGitHubApiBaseUrl = "https://api.github.com";
    internal static readonly IReadOnlyList<string> DefaultLabels = ["bug", "honua-support"];

    /// <summary>True once the shared webhook secret is set; the listener may bind.</summary>
    internal bool IsEnabled => !string.IsNullOrWhiteSpace(WebhookSecret);

    internal static BugReportConfiguration Load()
    {
        string webhookSecret = Environment.GetEnvironmentVariable(WebhookSecretVariable)?.Trim() ?? string.Empty;
        int port = ParsePort(Environment.GetEnvironmentVariable(PortVariable));
        string path = ParsePath(Environment.GetEnvironmentVariable(PathVariable));
        TimeSpan replayWindow = ParseReplayWindow(Environment.GetEnvironmentVariable(ReplayWindowVariable));
        ComponentRepoAllowlist allowlist = ComponentRepoAllowlist.Parse(
            Environment.GetEnvironmentVariable(ComponentMapVariable), ComponentMapVariable);
        IReadOnlyList<string> labels = ParseLabels(Environment.GetEnvironmentVariable(LabelsVariable));
        Uri? gitHubBaseUri = ParseGitHubBaseUri(Environment.GetEnvironmentVariable(GitHubBaseUrlVariable));
        string? gitHubToken = Normalize(Environment.GetEnvironmentVariable(GitHubTokenVariable));
        IReadOnlyList<string> allowedHosts = ParseAllowedHosts(Environment.GetEnvironmentVariable(AllowedHostsVariable));

        return new BugReportConfiguration(
            WebhookSecret: webhookSecret,
            Port: port,
            Path: path,
            ReplayWindow: replayWindow,
            Allowlist: allowlist,
            Labels: labels,
            GitHubApiBaseUri: gitHubBaseUri,
            GitHubToken: gitHubToken,
            AllowedHosts: allowedHosts);
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

    private static TimeSpan ParseReplayWindow(string? rawSeconds)
    {
        if (string.IsNullOrWhiteSpace(rawSeconds))
        {
            return TimeSpan.FromSeconds(DefaultReplayWindowSeconds);
        }

        if (!int.TryParse(rawSeconds.Trim(), out int seconds) || seconds < 1)
        {
            throw new InvalidOperationException(
                $"Environment variable `{ReplayWindowVariable}` must be a positive number of seconds.");
        }

        return TimeSpan.FromSeconds(seconds);
    }

    private static IReadOnlyList<string> ParseLabels(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return DefaultLabels;
        }

        string[] labels = value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return labels.Length == 0 ? DefaultLabels : labels;
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

    private static Uri? ParseGitHubBaseUri(string? value)
    {
        // Absent value ⇒ filing disabled (report-only). Default to the public API
        // host only when explicitly requested via an empty-but-present marker is
        // not supported; keep it simple: unset means null (disabled).
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string candidate = value.Trim();
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out Uri? uri))
        {
            throw new InvalidOperationException(
                $"Environment variable `{GitHubBaseUrlVariable}` must be a valid absolute URL.");
        }

        if (!uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Environment variable `{GitHubBaseUrlVariable}` must use http or https.");
        }

        if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new InvalidOperationException(
                $"Environment variable `{GitHubBaseUrlVariable}` must not include query string or fragment.");
        }

        if (!IsLocalUri(uri) && !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Environment variable `{GitHubBaseUrlVariable}` must use https for non-local endpoints.");
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
