namespace Honua.DevOps.Agent.Operations.WorkIntake;

/// <summary>
/// Lightweight HTTP listener for the work-intake webhook. Inherits the shared
/// BCL-only transport from <see cref="WebhookListenerBase"/> — a direct sibling
/// of <see cref="EscalationWebhookListener"/>. Binds to localhost only; front it
/// with a tunnel/proxy for remote delivery.
/// </summary>
internal sealed class WorkIntakeWebhookListener : WebhookListenerBase
{
    private readonly WorkIntakeConfiguration _configuration;
    private readonly WorkIntakeWebhookHandler _handler;

    internal WorkIntakeWebhookListener(
        WorkIntakeConfiguration configuration,
        WorkIntakeWebhookHandler handler,
        TextWriter? stdout = null,
        TextWriter? stderr = null)
        : base(configuration.Port, configuration.Path, stdout, stderr)
    {
        _configuration = configuration;
        _handler = handler;
    }

    protected override string SignatureHeaderName => WorkIntakeWebhookPayload.SignatureHeader;

    protected override string ListenerLabel => "work-intake";

    protected override async Task<(int StatusCode, string Reason)> HandleAsync(
        byte[] body,
        string? signatureHeader,
        CancellationToken cancellationToken)
    {
        WorkIntakeHandlerResult result = await _handler.HandleAsync(body, signatureHeader, cancellationToken);
        return (result.StatusCode, result.Reason);
    }

    protected override void WriteStartupBanner(Uri endpoint)
    {
        Stdout.WriteLine(
            $"honua-devops work-intake listener bound to {endpoint} (provider={_configuration.Provider.ToString().ToLowerInvariant()}, auto-draft={_configuration.AutoDraft.ToString().ToLowerInvariant()}).");
        Stdout.WriteLine("Awaiting signed work-intake webhooks. Press Ctrl+C to exit.");
    }
}
