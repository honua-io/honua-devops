using Amazon.KeyManagementService;
using Amazon.KeyManagementService.Model;

namespace Honua.DevOps.Agent.Operations;

internal static class KmsMacAlgorithms
{
    internal const string HmacSha256 = "HMAC_SHA_256";
}

internal sealed record KmsMacRequest(string KeyId, string MacAlgorithm, byte[] Message);

internal sealed record KmsMacResult(string KeyId, string MacAlgorithm, byte[] Mac);

internal sealed record KmsVerifyMacRequest(string KeyId, string MacAlgorithm, byte[] Message, byte[] Mac);

internal sealed record KmsVerifyMacResult(string KeyId, bool MacValid);

/// <summary>
/// Raised when IAM refused the call. This is the permission split becoming visible: a
/// verifier principal calling GenerateMac, or a signer principal calling VerifyMac, gets
/// this rather than a result.
/// </summary>
internal sealed class KmsMacAccessDeniedException(string message) : Exception(message);

/// <summary>
/// The two KMS operations an approval receipt needs. Narrowed to exactly GenerateMac and
/// VerifyMac on purpose: the seam should not be able to express key export, key
/// description, or any other operation, because the point of the design is that neither
/// principal has them.
/// </summary>
internal interface IKmsMacClient
{
    Task<KmsMacResult> GenerateMacAsync(KmsMacRequest request, CancellationToken cancellationToken = default);

    Task<KmsVerifyMacResult> VerifyMacAsync(KmsVerifyMacRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// Production adapter over the AWS SDK. It is a pure translation layer — every behaviour
/// worth asserting (the permission split, invalid-MAC refusal, fail-closed on denial)
/// lives in <see cref="KmsMacApprovalSignatureProvider"/> and is tested against a stub.
/// This class is the part that requires a live-AWS proof; see honua-devops#175.
/// </summary>
internal sealed class AwsKmsMacClient : IKmsMacClient
{
    private readonly Lazy<IAmazonKeyManagementService> _client;

    internal AwsKmsMacClient(Func<IAmazonKeyManagementService>? factory = null)
    {
        _client = new Lazy<IAmazonKeyManagementService>(
            factory ?? (() => new AmazonKeyManagementServiceClient()));
    }

    internal static AwsKmsMacClient Instance { get; } = new();

    public async Task<KmsMacResult> GenerateMacAsync(
        KmsMacRequest request,
        CancellationToken cancellationToken = default)
    {
        using MemoryStream message = new(request.Message, writable: false);
        GenerateMacResponse response;
        try
        {
            response = await _client.Value.GenerateMacAsync(
                new GenerateMacRequest
                {
                    KeyId = request.KeyId,
                    MacAlgorithm = MacAlgorithmSpec.FindValue(request.MacAlgorithm),
                    Message = message,
                },
                cancellationToken);
        }
        catch (AmazonKeyManagementServiceException ex) when (IsAccessDenied(ex))
        {
            throw new KmsMacAccessDeniedException(
                $"kms:GenerateMac was denied on {request.KeyId}: {ex.Message}");
        }

        return new KmsMacResult(response.KeyId, request.MacAlgorithm, response.Mac.ToArray());
    }

    public async Task<KmsVerifyMacResult> VerifyMacAsync(
        KmsVerifyMacRequest request,
        CancellationToken cancellationToken = default)
    {
        using MemoryStream message = new(request.Message, writable: false);
        try
        {
            VerifyMacResponse response = await _client.Value.VerifyMacAsync(
                new VerifyMacRequest
                {
                    KeyId = request.KeyId,
                    MacAlgorithm = MacAlgorithmSpec.FindValue(request.MacAlgorithm),
                    Message = message,
                    Mac = new MemoryStream(request.Mac, writable: false),
                },
                cancellationToken);

            return new KmsVerifyMacResult(response.KeyId, response.MacValid ?? false);
        }
        catch (KMSInvalidMacException)
        {
            // KMS signals a wrong MAC with an exception, not a false flag. A receipt
            // signed under a different key lands here.
            return new KmsVerifyMacResult(request.KeyId, MacValid: false);
        }
        catch (AmazonKeyManagementServiceException ex) when (IsAccessDenied(ex))
        {
            throw new KmsMacAccessDeniedException(
                $"kms:VerifyMac was denied on {request.KeyId}: {ex.Message}");
        }
    }

    private static bool IsAccessDenied(AmazonKeyManagementServiceException ex)
        => string.Equals(ex.ErrorCode, "AccessDeniedException", StringComparison.Ordinal)
            || ex.StatusCode == System.Net.HttpStatusCode.Forbidden;
}
