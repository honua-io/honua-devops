namespace Honua.DevOps.Agent.Operations.WorkIntake;

/// <summary>
/// Provider seam for verifying an inbound work-intake webhook. Each work-item
/// provider (Jira Cloud, Jira Data Center, ServiceNow) signs its webhooks
/// differently, so the intake handler depends on this interface rather than a
/// concrete recipe. Verification MUST be constant-time over raw bytes.
/// </summary>
internal interface IIntakeSignatureVerifier
{
    /// <summary>
    /// Returns true when the supplied signature header authenticates the raw
    /// request body under the provider's configured secret.
    /// </summary>
    bool Verify(byte[] body, string? signatureHeader);
}

/// <summary>
/// Jira Cloud verifier: a configured shared secret HMAC-SHA256 of the raw body,
/// carried as <c>X-Hub-Signature: sha256=&lt;hex&gt;</c>. Reuses the proven
/// constant-time recipe from <see cref="WebhookSignatureVerifier"/> so the
/// escalation listener and the intake listener share one verification primitive.
///
/// Jira Cloud does not natively HMAC-sign webhooks; the shared secret is
/// expected to be applied by the same ingress/proxy that fronts the listener,
/// exactly as honua-support signs the escalation webhook. This keeps the
/// default-safe posture: no secret configured means the listener never starts.
/// </summary>
internal sealed class JiraCloudSignatureVerifier : IIntakeSignatureVerifier
{
    private readonly byte[] _secret;

    internal JiraCloudSignatureVerifier(string secret)
    {
        if (string.IsNullOrEmpty(secret))
        {
            throw new ArgumentException("Secret must not be empty.", nameof(secret));
        }

        _secret = System.Text.Encoding.UTF8.GetBytes(secret);
    }

    public bool Verify(byte[] body, string? signatureHeader)
        => WebhookSignatureVerifier.TryVerify(_secret, body, signatureHeader);
}

/// <summary>
/// TODO (follow-up PR): Jira Data Center variant. Data Center signs webhooks
/// with a per-webhook secret and may carry the signature under a different
/// header/algorithm than Jira Cloud's shared-secret scheme. This stub exists to
/// hold the provider seam open; it is intentionally not wired into configuration
/// or the CLI yet and must not be selected at runtime.
/// </summary>
internal sealed class JiraDataCenterSignatureVerifier : IIntakeSignatureVerifier
{
    public bool Verify(byte[] body, string? signatureHeader)
        => throw new NotImplementedException(
            "Jira Data Center webhook verification is not implemented yet (tracked for a follow-up PR). "
            + "Use the Jira Cloud provider (HONUA_DEVOPS_INTAKE_PROVIDER=jira) for now.");
}
