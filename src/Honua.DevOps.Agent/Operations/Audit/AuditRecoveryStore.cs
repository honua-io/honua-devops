using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.DevOps.Agent.Operations.Audit;

/// <summary>
/// Durable, redacted recovery state for a mutation whose final audit append was not
/// acknowledged. This is deliberately separate from the audit sink: the sink is the
/// unavailable dependency being diagnosed, while this small local journal is the
/// process-restart hand-off for reconciliation.
/// </summary>
internal sealed class AuditRecoveryStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _root;

    internal AuditRecoveryStore(string root)
    {
        _root = Path.GetFullPath(root ?? throw new ArgumentNullException(nameof(root)));
    }

    internal static AuditRecoveryStore Default { get; } = new(
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "honua-devops",
            "audit-recovery"));

    internal void Record(AuditRecoveryEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        Directory.CreateDirectory(_root);
        ProtectDirectory(_root);

        string key = !string.IsNullOrWhiteSpace(evidence.IdempotencyKey)
            ? evidence.IdempotencyKey!
            : evidence.OperationId ?? evidence.AuditEventId;
        string destination = Path.Combine(_root, Hash(key) + ".json");
        string temporary = destination + $".{Guid.NewGuid():n}.tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(evidence, SerializerOptions) + Environment.NewLine);
        ProtectFile(temporary);
        File.Move(temporary, destination, overwrite: true);
        ProtectFile(destination);
    }

    internal bool HasPending(string? idempotencyKey)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return false;
        }

        return TryRead(idempotencyKey, out AuditRecoveryEvidence? evidence)
            && evidence!.RecoveryState == "indeterminate/reconciliation-required";
    }

    internal bool TryRead(string key, out AuditRecoveryEvidence? evidence)
    {
        evidence = null;
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        try
        {
            string path = Path.Combine(_root, Hash(key) + ".json");
            if (!File.Exists(path))
            {
                return false;
            }

            evidence = JsonSerializer.Deserialize<AuditRecoveryEvidence>(File.ReadAllText(path), SerializerOptions);
            return evidence is not null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    private static string Hash(string value)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static void ProtectDirectory(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private static void ProtectFile(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }
}

/// <summary>Only identifiers and redacted summaries cross the recovery boundary.</summary>
internal sealed record AuditRecoveryEvidence(
    [property: JsonPropertyName("recoveryState")] string RecoveryState,
    [property: JsonPropertyName("recordedAtUtc")] DateTimeOffset RecordedAtUtc,
    [property: JsonPropertyName("auditEventId")] string AuditEventId,
    [property: JsonPropertyName("toolName")] string ToolName,
    [property: JsonPropertyName("route")] string Route,
    [property: JsonPropertyName("operationId")] string? OperationId,
    [property: JsonPropertyName("idempotencyKey")] string? IdempotencyKey,
    [property: JsonPropertyName("provisioningOperationId")] string? ProvisioningOperationId,
    [property: JsonPropertyName("approvalReference")] string? ApprovalReference,
    [property: JsonPropertyName("sinkFailure")] string SinkFailure,
    [property: JsonPropertyName("returnedStatus")] string ReturnedStatus,
    [property: JsonPropertyName("mutationAttempted")] bool MutationAttempted,
    [property: JsonPropertyName("backendAcknowledged")] bool BackendAcknowledged,
    [property: JsonPropertyName("backendSteps")] IReadOnlyList<AuditRecoveryStep> BackendSteps);

internal sealed record AuditRecoveryStep(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("endpoint")] string Endpoint,
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("detail")] string Detail,
    [property: JsonPropertyName("mutatesState")] bool MutatesState);
