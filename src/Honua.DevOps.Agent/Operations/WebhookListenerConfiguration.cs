namespace Honua.DevOps.Agent.Operations;

/// <summary>
/// Environment-driven configuration for the escalation webhook listener.
/// Mirrors the env-var pattern used by <see cref="BackendConfiguration"/>.
///
/// Honua-support fires HMAC-SHA256-signed POSTs on every ticket transition to
/// EscalationRequested. The shared secret on both sides MUST match.
/// </summary>
internal sealed record WebhookListenerConfiguration(
    string Secret,
    int Port,
    string Path,
    bool AutoTriage)
{
    private const string SecretVariable = "HONUA_DEVOPS_WEBHOOK_SECRET";
    private const string PortVariable = "HONUA_DEVOPS_WEBHOOK_PORT";
    private const string PathVariable = "HONUA_DEVOPS_WEBHOOK_PATH";
    private const string AutoTriageVariable = "HONUA_DEVOPS_WEBHOOK_AUTO_TRIAGE";

    internal const int DefaultPort = 8090;
    internal const string DefaultPath = "/escalations";
    internal const bool DefaultAutoTriage = true;

    internal static WebhookListenerConfiguration Load()
    {
        string? rawSecret = Environment.GetEnvironmentVariable(SecretVariable);
        if (string.IsNullOrWhiteSpace(rawSecret))
        {
            throw new InvalidOperationException(
                $"Environment variable `{SecretVariable}` must be set to the shared escalation webhook secret.");
        }

        int port = DefaultPort;
        string? rawPort = Environment.GetEnvironmentVariable(PortVariable);
        if (!string.IsNullOrWhiteSpace(rawPort))
        {
            if (!int.TryParse(rawPort.Trim(), out port) || port is < 1 or > 65535)
            {
                throw new InvalidOperationException(
                    $"Environment variable `{PortVariable}` must be a TCP port between 1 and 65535.");
            }
        }

        string path = DefaultPath;
        string? rawPath = Environment.GetEnvironmentVariable(PathVariable);
        if (!string.IsNullOrWhiteSpace(rawPath))
        {
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

            path = trimmedPath;
        }

        bool autoTriage = DefaultAutoTriage;
        string? rawAutoTriage = Environment.GetEnvironmentVariable(AutoTriageVariable);
        if (!string.IsNullOrWhiteSpace(rawAutoTriage))
        {
            if (!bool.TryParse(rawAutoTriage.Trim(), out autoTriage))
            {
                throw new InvalidOperationException(
                    $"Environment variable `{AutoTriageVariable}` must be `true` or `false`.");
            }
        }

        return new WebhookListenerConfiguration(
            Secret: rawSecret.Trim(),
            Port: port,
            Path: path,
            AutoTriage: autoTriage);
    }
}
