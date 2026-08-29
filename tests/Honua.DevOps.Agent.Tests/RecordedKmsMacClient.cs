using System.Security.Cryptography;
using Honua.DevOps.Agent.Operations;

namespace Honua.DevOps.Agent.Tests;

/// <summary>
/// A stubbed KMS MAC client. It never opens a connection; it computes the MAC locally
/// from a per-key secret and records every request so a test can assert what was sent.
/// </summary>
/// <remarks>
/// <para>
/// The point of the stub is not the arithmetic — it is the IAM boundary. Each instance is
/// constructed with the set of actions its principal holds, and calling outside that set
/// raises <see cref="KmsMacAccessDeniedException"/> exactly as KMS would. That is what
/// makes "the signer cannot verify" and "the verifier cannot sign" testable offline: the
/// split is modelled, not assumed.
/// </para>
/// <para>
/// Nothing here talks to AWS, and nothing here is evidence that the live split is
/// configured correctly — that proof is the live-AWS remainder on honua-devops#175.
/// </para>
/// </remarks>
internal sealed class RecordedKmsMacClient : IKmsMacClient
{
    private readonly bool _canGenerate;
    private readonly bool _canVerify;
    private readonly IReadOnlyDictionary<string, byte[]> _keys;

    private RecordedKmsMacClient(
        bool canGenerate,
        bool canVerify,
        IReadOnlyDictionary<string, byte[]>? keys = null)
    {
        _canGenerate = canGenerate;
        _canVerify = canVerify;
        _keys = keys ?? DefaultKeys();
    }

    /// <summary>A principal holding kms:GenerateMac and nothing else — the issuer.</summary>
    internal static RecordedKmsMacClient Signer(IReadOnlyDictionary<string, byte[]>? keys = null)
        => new(canGenerate: true, canVerify: false, keys);

    /// <summary>A principal holding kms:VerifyMac and nothing else — the verifier.</summary>
    internal static RecordedKmsMacClient Verifier(IReadOnlyDictionary<string, byte[]>? keys = null)
        => new(canGenerate: false, canVerify: true, keys);

    /// <summary>
    /// Both capabilities on one principal. This is the posture the ticket exists to
    /// eliminate; it is here only so a test can demonstrate the difference.
    /// </summary>
    internal static RecordedKmsMacClient Unsplit(IReadOnlyDictionary<string, byte[]>? keys = null)
        => new(canGenerate: true, canVerify: true, keys);

    internal List<KmsMacRequest> GenerateMacRequests { get; } = [];

    internal List<KmsVerifyMacRequest> VerifyMacRequests { get; } = [];

    public Task<KmsMacResult> GenerateMacAsync(
        KmsMacRequest request,
        CancellationToken cancellationToken = default)
    {
        GenerateMacRequests.Add(request);
        if (!_canGenerate)
        {
            throw new KmsMacAccessDeniedException(
                $"User is not authorized to perform kms:GenerateMac on {request.KeyId}");
        }

        return Task.FromResult(new KmsMacResult(
            request.KeyId,
            request.MacAlgorithm,
            Mac(request.KeyId, request.Message)));
    }

    public Task<KmsVerifyMacResult> VerifyMacAsync(
        KmsVerifyMacRequest request,
        CancellationToken cancellationToken = default)
    {
        VerifyMacRequests.Add(request);
        if (!_canVerify)
        {
            throw new KmsMacAccessDeniedException(
                $"User is not authorized to perform kms:VerifyMac on {request.KeyId}");
        }

        byte[] expected = Mac(request.KeyId, request.Message);
        return Task.FromResult(new KmsVerifyMacResult(
            request.KeyId,
            CryptographicOperations.FixedTimeEquals(expected, request.Mac)));
    }

    private byte[] Mac(string keyId, byte[] message)
    {
        if (!_keys.TryGetValue(keyId, out byte[]? key))
        {
            throw new KmsMacAccessDeniedException($"Key {keyId} is not available to this principal.");
        }

        return HMACSHA256.HashData(key, message);
    }

    private static IReadOnlyDictionary<string, byte[]> DefaultKeys()
        => new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [ProvisioningSubstrateFixtures.ApprovalKeyArn] =
                SHA256.HashData("kms-mac-test-key-a"u8.ToArray()),
            [ProvisioningSubstrateFixtures.OtherApprovalKeyArn] =
                SHA256.HashData("kms-mac-test-key-b"u8.ToArray()),
        };
}
