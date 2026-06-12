using System.Net.Http;
using System.Text.Json;

namespace Honua.DevOps.Agent.Operations;

/// <summary>
/// Probes the connected Honua backend capabilities endpoint to detect the licensed edition.
/// Falls back to the safest assumption (community) when the probe fails so edition-gated
/// tools stay gated rather than over-granting.
/// </summary>
internal static class EditionDetector
{
    internal const string FallbackEdition = "community";

    internal static async Task<string> DetectAsync(BackendGateway gateway, CancellationToken cancellationToken)
    {
        try
        {
            using BackendJsonResult capabilities = await gateway.GetCapabilitySnapshotAsync(cancellationToken);
            if (capabilities.CallResult.IsSuccess && capabilities.Payload is not null)
            {
                string? detected = BackendGateway.ExtractEditionFromCapabilities(capabilities.Payload);
                if (!string.IsNullOrWhiteSpace(detected))
                {
                    return detected!.Trim().ToLowerInvariant();
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            Console.Error.WriteLine($"warn: capability probe failed ({exception.GetType().Name}): {exception.Message}");
        }

        return FallbackEdition;
    }
}
