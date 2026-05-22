using System.Security.Cryptography;
using System.Text;

namespace Honua.DevOps.Agent.Operations;

/// <summary>
/// Verifies HMAC-SHA256 signatures emitted by honua-support's escalation webhook
/// per the cross-repo contract:
///
///   X-Honua-Signature: sha256=&lt;lowercase-hex of HMAC-SHA256(secret, body)&gt;
///
/// Verification MUST be constant-time, so we always compare via
/// <see cref="CryptographicOperations.FixedTimeEquals(System.ReadOnlySpan{byte}, System.ReadOnlySpan{byte})"/>.
/// </summary>
internal static class WebhookSignatureVerifier
{
    internal const string SignaturePrefix = "sha256=";

    internal static string ComputeSignatureHeader(byte[] secret, byte[] body)
    {
        byte[] hash = HMACSHA256.HashData(secret, body);
        return SignaturePrefix + Convert.ToHexString(hash).ToLowerInvariant();
    }

    internal static string ComputeSignatureHeader(string secret, string body)
    {
        return ComputeSignatureHeader(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(body));
    }

    /// <summary>
    /// Returns true when the X-Honua-Signature header matches the HMAC of the body
    /// under the shared secret. Comparison is constant-time on the raw HMAC bytes —
    /// we never compare hex strings directly.
    /// </summary>
    internal static bool TryVerify(byte[] secret, byte[] body, string? signatureHeader)
    {
        if (string.IsNullOrWhiteSpace(signatureHeader))
        {
            return false;
        }

        string trimmed = signatureHeader.Trim();
        if (!trimmed.StartsWith(SignaturePrefix, StringComparison.Ordinal))
        {
            return false;
        }

        string hex = trimmed[SignaturePrefix.Length..];
        if (hex.Length != 64)
        {
            return false;
        }

        byte[] presented;
        try
        {
            presented = Convert.FromHexString(hex);
        }
        catch (FormatException)
        {
            return false;
        }

        byte[] expected = HMACSHA256.HashData(secret, body);
        return CryptographicOperations.FixedTimeEquals(presented, expected);
    }
}
