namespace Honua.DevOps.Agent.Operations;

/// <summary>
/// Lightweight HTTP listener for the escalation webhook. Inherits the shared
/// BCL-only transport (accept loop, body-size cap, response writer) from
/// <see cref="WebhookListenerBase"/> so honua-devops stays on the plain
/// Microsoft.NET.Sdk console SDK with zero added dependencies.
/// </summary>
internal sealed class EscalationWebhookListener : WebhookListenerBase
{
    private readonly WebhookListenerConfiguration _configuration;
    private readonly EscalationWebhookHandler _handler;

    internal EscalationWebhookListener(
        WebhookListenerConfiguration configuration,
        EscalationWebhookHandler handler,
        TextWriter? stdout = null,
        TextWriter? stderr = null)
        : base(configuration.Port, configuration.Path, stdout, stderr)
    {
        _configuration = configuration;
        _handler = handler;
    }

    protected override string SignatureHeaderName => EscalationWebhookPayload.SignatureHeader;

    protected override string ListenerLabel => "webhook";

    protected override async Task<(int StatusCode, string Reason)> HandleAsync(
        byte[] body,
        string? signatureHeader,
        CancellationToken cancellationToken)
    {
        WebhookHandlerResult result = await _handler.HandleAsync(body, signatureHeader, cancellationToken);
        return (result.StatusCode, result.Reason);
    }

    protected override void WriteStartupBanner(Uri endpoint)
    {
        Stdout.WriteLine($"honua-devops listener bound to {endpoint} (auto-triage={_configuration.AutoTriage.ToString().ToLowerInvariant()}).");
        Stdout.WriteLine("Awaiting signed escalation webhooks. Press Ctrl+C to exit.");
    }
}
