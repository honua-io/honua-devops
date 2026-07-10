namespace Honua.DevOps.Agent.Operations.BugReport;

/// <summary>
/// Lightweight HTTP listener for the <c>ticket.bug_report.v1</c> webhook. Inherits
/// the shared BCL-only transport (accept loop, body-size cap, response writer)
/// from <see cref="WebhookListenerBase"/> — a direct sibling of
/// <see cref="EscalationWebhookListener"/> and
/// <see cref="Honua.DevOps.Agent.Operations.WorkIntake.WorkIntakeWebhookListener"/>.
/// Binds to localhost only; front it with a tunnel/proxy for remote delivery.
/// </summary>
internal sealed class BugReportWebhookListener : WebhookListenerBase
{
    private readonly BugReportConfiguration _configuration;
    private readonly BugReportWebhookHandler _handler;

    internal BugReportWebhookListener(
        BugReportConfiguration configuration,
        BugReportWebhookHandler handler,
        TextWriter? stdout = null,
        TextWriter? stderr = null)
        : base(configuration.Port, configuration.Path, stdout, stderr)
    {
        _configuration = configuration;
        _handler = handler;
    }

    protected override string SignatureHeaderName => BugReportWebhookPayload.SignatureHeader;

    protected override string ListenerLabel => "bug-report";

    protected override async Task<(int StatusCode, string Reason)> HandleAsync(
        byte[] body,
        string? signatureHeader,
        CancellationToken cancellationToken)
    {
        BugReportHandlerResult result = await _handler.HandleAsync(body, signatureHeader, cancellationToken);
        return (result.StatusCode, result.Reason);
    }

    protected override void WriteStartupBanner(Uri endpoint)
    {
        bool filingEnabled = _configuration.GitHubApiBaseUri is not null
            && !string.IsNullOrWhiteSpace(_configuration.GitHubToken)
            && _configuration.AllowedHosts.Count > 0;

        Stdout.WriteLine(
            $"honua-devops bug-report listener bound to {endpoint} "
            + $"(components={_configuration.Allowlist.Count}, filing={(filingEnabled ? "enabled" : "report-only")}).");
        Stdout.WriteLine("Awaiting signed ticket.bug_report.v1 webhooks. Press Ctrl+C to exit.");
    }
}
