using System.Security.Cryptography;
using System.Text;

namespace Honua.DevOps.Agent.Operations;

/// <summary>
/// The signing modes an approval receipt may declare (honua-devops#175).
/// </summary>
/// <remarks>
/// <para>
/// The primitive is unchanged: an HMAC-SHA-256 over the canonical newline-joined
/// receipt fields. What changes is who can compute it.
/// </para>
/// <para>
/// <c>local-hmac-dev</c> keeps the raw symmetric key in the verifier's own process,
/// so the verifier can compute any signature it is willing to accept. That is a
/// usable development mode and it is NOT evidence: a receipt its own verifier could
/// have forged proves nothing about who approved the mutation. Receipts in this mode
/// are marked non-evidentiary wherever they are reported.
/// </para>
/// <para>
/// <c>kms-mac</c> moves the key into AWS KMS and splits the capability across two
/// principals: the issuer holds <c>kms:GenerateMac</c> and nothing else, the verifier
/// holds <c>kms:VerifyMac</c> and nothing else, and no principal can export the key
/// (a KMS HMAC key has no export operation at all). A verifier holding only
/// VerifyMac cannot produce a receipt it would accept, which is what makes the
/// receipt evidence.
/// </para>
/// </remarks>
internal static class ApprovalSigningModes
{
    /// <summary>Symmetric HMAC with the key in this process. Development only; never evidence.</summary>
    internal const string LocalHmacDev = "local-hmac-dev";

    /// <summary>KMS-backed HMAC with GenerateMac/VerifyMac split across principals.</summary>
    internal const string KmsMac = "kms-mac";

    internal static readonly IReadOnlyList<string> All = [LocalHmacDev, KmsMac];

    internal static bool IsKnown(string? mode)
        => mode is not null && All.Contains(mode, StringComparer.Ordinal);

    /// <summary>
    /// Whether a receipt signed in this mode may be cited as evidence. Only a mode whose
    /// verifier provably cannot sign qualifies; everything else, including an unknown
    /// mode, is non-evidentiary by default.
    /// </summary>
    internal static bool IsEvidentiary(string? mode)
        => string.Equals(mode, KmsMac, StringComparison.Ordinal);
}

/// <summary>
/// The canonical bytes an approval receipt is signed over. One definition, shared by
/// whatever signs and whatever verifies — a second copy is a signature scheme that can
/// drift into mutual unintelligibility without any test noticing.
/// </summary>
internal static class ApprovalReceiptCanonicalization
{
    internal static string Payload(
        string schemaVersion,
        string approvalReceiptId,
        string issuer,
        string keyId,
        string provisioningOperationId,
        string planSha256,
        string planMetadataDigest,
        string action,
        string stack,
        string environment,
        string decision,
        DateTimeOffset issuedAtUtc,
        DateTimeOffset expiresAtUtc,
        string signingMode)
        => string.Join('\n',
            schemaVersion,
            approvalReceiptId,
            issuer,
            keyId,
            provisioningOperationId,
            planSha256.ToLowerInvariant(),
            planMetadataDigest.ToLowerInvariant(),
            action,
            stack,
            environment,
            decision,
            issuedAtUtc.ToUniversalTime().ToString("O"),
            expiresAtUtc.ToUniversalTime().ToString("O"),
            signingMode);
}

internal sealed record ApprovalSignature(string KeyId, string Signature, string SigningMode);

internal sealed record ApprovalVerificationResult(
    bool Verified,
    string SigningMode,
    bool Evidentiary,
    string Detail);

/// <summary>
/// Computes and checks approval-receipt signatures. Implementations differ in WHERE the
/// key lives and therefore in whether a verifier can forge what it accepts.
/// </summary>
internal interface IApprovalSignatureProvider
{
    /// <summary>The mode value a receipt this provider signs must declare.</summary>
    string SigningMode { get; }

    /// <summary>
    /// The key id a receipt from <paramref name="issuer"/> must name, or null when the
    /// issuer is not configured for this provider. Derived without exporting key
    /// material in either mode.
    /// </summary>
    string? ResolveKeyId(string issuer);

    Task<ApprovalSignature> SignAsync(
        string issuer,
        string canonicalPayload,
        CancellationToken cancellationToken = default);

    Task<ApprovalVerificationResult> VerifyAsync(
        string issuer,
        string canonicalPayload,
        string signatureBase64,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Development provider: the symmetric key sits in this process, so this provider can
/// both sign and verify. That is precisely why its receipts are non-evidentiary.
/// </summary>
internal sealed class LocalHmacApprovalSignatureProvider : IApprovalSignatureProvider
{
    private readonly IReadOnlyDictionary<string, string> _issuerKeys;

    internal LocalHmacApprovalSignatureProvider(IReadOnlyDictionary<string, string>? issuerKeys)
    {
        _issuerKeys = issuerKeys ?? new Dictionary<string, string>(StringComparer.Ordinal);
    }

    public string SigningMode => ApprovalSigningModes.LocalHmacDev;

    public string? ResolveKeyId(string issuer)
        => TryResolveKey(issuer, out byte[]? key) ? KeyIdFor(key!) : null;

    public Task<ApprovalSignature> SignAsync(
        string issuer,
        string canonicalPayload,
        CancellationToken cancellationToken = default)
    {
        if (!TryResolveKey(issuer, out byte[]? key))
        {
            throw new InvalidOperationException(
                $"Approval issuer `{issuer}` has no configured local HMAC key.");
        }

        byte[] mac = HMACSHA256.HashData(key!, Encoding.UTF8.GetBytes(canonicalPayload));
        return Task.FromResult(new ApprovalSignature(
            KeyIdFor(key!),
            Convert.ToBase64String(mac),
            SigningMode));
    }

    public Task<ApprovalVerificationResult> VerifyAsync(
        string issuer,
        string canonicalPayload,
        string signatureBase64,
        CancellationToken cancellationToken = default)
    {
        if (!TryResolveKey(issuer, out byte[]? key))
        {
            return Task.FromResult(Failed(
                $"Approval issuer `{issuer}` is not in the configured trusted-issuer allowlist."));
        }

        byte[] actual;
        try
        {
            actual = Convert.FromBase64String(signatureBase64);
        }
        catch (FormatException)
        {
            return Task.FromResult(Failed("The approval receipt signature is malformed."));
        }

        byte[] expected = HMACSHA256.HashData(key!, Encoding.UTF8.GetBytes(canonicalPayload));
        if (!CryptographicOperations.FixedTimeEquals(expected, actual))
        {
            return Task.FromResult(Failed(
                "The approval receipt signature could not be verified by its trusted issuer key."));
        }

        return Task.FromResult(new ApprovalVerificationResult(
            Verified: true,
            SigningMode: SigningMode,
            // Never evidence: this verifier holds the signing key.
            Evidentiary: false,
            Detail: "Verified with a local symmetric HMAC key; this receipt is NOT evidence because its verifier could have produced it."));
    }

    private ApprovalVerificationResult Failed(string detail)
        => new(Verified: false, SigningMode: SigningMode, Evidentiary: false, Detail: detail);

    private bool TryResolveKey(string issuer, out byte[]? key)
    {
        key = null;
        if (!_issuerKeys.TryGetValue(issuer, out string? encoded))
        {
            return false;
        }

        try
        {
            key = Convert.FromBase64String(encoded);
        }
        catch (FormatException)
        {
            return false;
        }

        return true;
    }

    /// <summary>First 16 hex characters of the SHA-256 of the key, per the receipt schema.</summary>
    private static string KeyIdFor(byte[] key)
        => Convert.ToHexString(SHA256.HashData(key)).ToLowerInvariant()[..16];
}

/// <summary>
/// KMS-backed provider. It never sees key material: signing is a <c>GenerateMac</c> call
/// and verification is a <c>VerifyMac</c> call, and which of the two succeeds is decided
/// by the caller's IAM policy, not by this class.
/// </summary>
internal sealed class KmsMacApprovalSignatureProvider : IApprovalSignatureProvider
{
    private readonly IKmsMacClient _client;
    private readonly IReadOnlyDictionary<string, string> _issuerKeyArns;

    internal KmsMacApprovalSignatureProvider(
        IKmsMacClient client,
        IReadOnlyDictionary<string, string>? issuerKeyArns)
    {
        _client = client;
        _issuerKeyArns = issuerKeyArns ?? new Dictionary<string, string>(StringComparer.Ordinal);
    }

    public string SigningMode => ApprovalSigningModes.KmsMac;

    /// <summary>
    /// Derived from the key ARN, not from key material — a KMS HMAC key cannot be
    /// exported, so the symmetric-key derivation the local mode uses is unavailable to
    /// either side. Both signer and verifier can compute this from the ARN alone.
    /// </summary>
    public string? ResolveKeyId(string issuer)
        => _issuerKeyArns.TryGetValue(issuer, out string? arn) ? KeyIdForArn(arn) : null;

    internal static string KeyIdForArn(string keyArn)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(keyArn))).ToLowerInvariant()[..16];

    public async Task<ApprovalSignature> SignAsync(
        string issuer,
        string canonicalPayload,
        CancellationToken cancellationToken = default)
    {
        if (!_issuerKeyArns.TryGetValue(issuer, out string? keyArn))
        {
            throw new InvalidOperationException(
                $"Approval issuer `{issuer}` has no configured KMS MAC key ARN.");
        }

        KmsMacResult result = await _client.GenerateMacAsync(
            new KmsMacRequest(keyArn, KmsMacAlgorithms.HmacSha256, Encoding.UTF8.GetBytes(canonicalPayload)),
            cancellationToken);

        return new ApprovalSignature(
            KeyIdForArn(keyArn),
            Convert.ToBase64String(result.Mac),
            SigningMode);
    }

    public async Task<ApprovalVerificationResult> VerifyAsync(
        string issuer,
        string canonicalPayload,
        string signatureBase64,
        CancellationToken cancellationToken = default)
    {
        if (!_issuerKeyArns.TryGetValue(issuer, out string? keyArn))
        {
            return Failed($"Approval issuer `{issuer}` is not in the configured trusted-issuer allowlist.");
        }

        byte[] mac;
        try
        {
            mac = Convert.FromBase64String(signatureBase64);
        }
        catch (FormatException)
        {
            return Failed("The approval receipt signature is malformed.");
        }

        KmsVerifyMacResult result;
        try
        {
            result = await _client.VerifyMacAsync(
                new KmsVerifyMacRequest(
                    keyArn,
                    KmsMacAlgorithms.HmacSha256,
                    Encoding.UTF8.GetBytes(canonicalPayload),
                    mac),
                cancellationToken);
        }
        catch (KmsMacAccessDeniedException denied)
        {
            // Fail closed. A verifier that has lost kms:VerifyMac must refuse the
            // mutation, never wave it through.
            return Failed($"kms:VerifyMac was denied for the receipt key: {denied.Message}");
        }

        if (!result.MacValid)
        {
            return Failed("The approval receipt signature could not be verified by its trusted issuer key.");
        }

        return new ApprovalVerificationResult(
            Verified: true,
            SigningMode: SigningMode,
            Evidentiary: true,
            Detail: $"Verified by kms:VerifyMac against {keyArn}; the verifying principal holds no kms:GenerateMac on that key.");
    }

    private ApprovalVerificationResult Failed(string detail)
        => new(Verified: false, SigningMode: SigningMode, Evidentiary: false, Detail: detail);
}

/// <summary>Selects the configured provider for a runtime.</summary>
internal static class ApprovalSignatureProviders
{
    /// <summary>
    /// Resolves the provider the operator configured. Defaults to the local development
    /// mode, which can only ever REMOVE evidentiary weight from a receipt — never add it.
    /// </summary>
    internal static IApprovalSignatureProvider FromRuntime(
        OperationRuntime runtime,
        IKmsMacClient? kmsMacClient = null)
    {
        if (string.Equals(runtime.ProvisionApprovalSigningMode, ApprovalSigningModes.KmsMac, StringComparison.Ordinal))
        {
            return new KmsMacApprovalSignatureProvider(
                kmsMacClient ?? AwsKmsMacClient.Instance,
                runtime.ProvisionApprovalIssuerKeyArns);
        }

        return new LocalHmacApprovalSignatureProvider(runtime.ProvisionApprovalIssuerKeys);
    }
}
