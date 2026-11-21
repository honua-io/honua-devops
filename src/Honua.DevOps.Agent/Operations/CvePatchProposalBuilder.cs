using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Honua.DevOps.Agent.Operations.Audit;

namespace Honua.DevOps.Agent.Operations;

internal sealed record CvePatchProposalInput(
    string TicketId,
    string CveId,
    string ReleaseVersion,
    string ServerImageDigest,
    string SigstoreReleaseLockRef,
    string? WorkerImageDigest = null,
    string? CloudFormationServerImageUri = null,
    string? CloudFormationStackName = null,
    string? Rationale = null,
    string? ProposedBy = null);

internal sealed record CvePatchProposal(
    string ProposalKind,
    string TicketId,
    string CveId,
    string ReleaseVersion,
    string DesiredRevision,
    string? WorkerImageDigest,
    string SigstoreReleaseLockRef,
    string CloudFormationServerImageUri,
    IReadOnlyDictionary<string, string> CloudFormationParameters,
    string ChangeSetInstructions,
    string Rationale,
    string ProposedBy,
    string ProposalDigest,
    DateTimeOffset BuiltAt);

internal sealed record CustomerFixProposalResult(
    bool IsSuccess,
    BackendCallResult BackendResult,
    string? ProposalId,
    string AuditEventId,
    string AuditEntryHash,
    string ProposalDigest);

internal sealed record CveProposalAuditVerification(
    bool IsIntact,
    int EntryCount,
    string? HeadHash,
    int? FirstCorruptedIndex,
    string? FailureReason);

internal static class CvePatchProposalBuilder
{
    internal const string ProposalKindValue = "cve-image-patch";
    internal const string ActionIdValue = "UpdateServerImage";
    internal const string AuditToolName = "support.propose_customer_fix";
    internal const string GenesisChainHash = "0000000000000000000000000000000000000000000000000000000000000000";

    private static readonly Regex ImmutableImageDigestPattern =
        new(@"^[^\s@]+@sha256:[0-9a-f]{64}$", RegexOptions.Compiled);

    internal static bool IsImmutableImageDigest(string? value) =>
        !string.IsNullOrWhiteSpace(value) && ImmutableImageDigestPattern.IsMatch(value.Trim());

    internal static CvePatchProposal Build(CvePatchProposalInput input, DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(input);

        string ticketId = RequireNonBlank(input.TicketId, nameof(input.TicketId));
        string cveId = RequireNonBlank(input.CveId, nameof(input.CveId));
        string releaseVersion = RequireNonBlank(input.ReleaseVersion, nameof(input.ReleaseVersion));
        string serverDigest = ValidateDigest(input.ServerImageDigest, nameof(input.ServerImageDigest));
        string releaseLockRef = RequireNonBlank(input.SigstoreReleaseLockRef, nameof(input.SigstoreReleaseLockRef));

        string? workerDigest = string.IsNullOrWhiteSpace(input.WorkerImageDigest)
            ? null
            : ValidateDigest(input.WorkerImageDigest, nameof(input.WorkerImageDigest));

        string cfnServerImageUri = string.IsNullOrWhiteSpace(input.CloudFormationServerImageUri)
            ? serverDigest
            : ValidateDigest(input.CloudFormationServerImageUri, nameof(input.CloudFormationServerImageUri));

        if (!string.Equals(cfnServerImageUri, serverDigest, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "CloudFormationServerImageUri must match the verified ServerImageDigest to prevent image drift.",
                nameof(input.CloudFormationServerImageUri));
        }

        string stackName = string.IsNullOrWhiteSpace(input.CloudFormationStackName)
            ? "<stack-name>"
            : input.CloudFormationStackName.Trim();

        Dictionary<string, string> cfnParameters = new(StringComparer.Ordinal)
        {
            ["ServerImageUri"] = cfnServerImageUri
        };
        if (workerDigest is not null)
        {
            cfnParameters["WorkerImageUri"] = workerDigest;
        }

        string changeSetInstructions =
            $"aws cloudformation create-change-set --stack-name {stackName} " +
            $"--use-previous-template --change-set-name honua-{SanitizeSegment(cveId)}-{SanitizeSegment(releaseVersion)} " +
            $"--parameters ParameterKey=ServerImageUri,ParameterValue={cfnServerImageUri}";

        string rationale = string.IsNullOrWhiteSpace(input.Rationale)
            ? $"Urgent patch proposal for {cveId} ({releaseVersion}) pinned to immutable digest {serverDigest}."
            : input.Rationale.Trim();

        string proposedBy = string.IsNullOrWhiteSpace(input.ProposedBy)
            ? "honua-devops-agent"
            : input.ProposedBy.Trim();

        string proposalDigest = ComputeProposalDigest(
            ticketId, cveId, releaseVersion, serverDigest, workerDigest, releaseLockRef, cfnServerImageUri);

        return new CvePatchProposal(
            ProposalKind: ProposalKindValue,
            TicketId: ticketId,
            CveId: cveId,
            ReleaseVersion: releaseVersion,
            DesiredRevision: serverDigest,
            WorkerImageDigest: workerDigest,
            SigstoreReleaseLockRef: releaseLockRef,
            CloudFormationServerImageUri: cfnServerImageUri,
            CloudFormationParameters: cfnParameters,
            ChangeSetInstructions: changeSetInstructions,
            Rationale: rationale,
            ProposedBy: proposedBy,
            ProposalDigest: proposalDigest,
            BuiltAt: now ?? DateTimeOffset.UtcNow);
    }

    internal static string ComputeChainHash(
        string auditEventId,
        DateTimeOffset timestamp,
        string ticketId,
        string cveId,
        string desiredRevision,
        string proposalDigest,
        string? proposalId,
        string endpoint,
        string previousChainHash,
        string? chainKey = null)
    {
        string canonical = string.Join(
            "\n",
            auditEventId,
            timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            ticketId,
            cveId,
            desiredRevision,
            proposalDigest,
            proposalId ?? string.Empty,
            endpoint,
            previousChainHash);

        byte[] data = Encoding.UTF8.GetBytes(canonical);
        byte[] digest = string.IsNullOrWhiteSpace(chainKey)
            ? SHA256.HashData(data)
            : HMACSHA256.HashData(Encoding.UTF8.GetBytes(chainKey), data);
        return Convert.ToHexStringLower(digest);
    }

    internal static CveProposalAuditVerification VerifyJournalChain(string auditHookTarget, string? chainKey = null)
    {
        if (!OperationJournal.TryResolveJournalPath(auditHookTarget, out string path, out string? reason))
        {
            return new CveProposalAuditVerification(false, 0, null, 0, reason);
        }

        if (!File.Exists(path))
        {
            return new CveProposalAuditVerification(true, 0, GenesisChainHash, null, null);
        }

        string expectedPrevious = GenesisChainHash;
        string head = GenesisChainHash;
        int index = 0;

        foreach (string line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            using JsonDocument doc = JsonDocument.Parse(line);
            JsonElement root = doc.RootElement;
            if (!TryGetString(root, "ToolName", out string toolName) ||
                !string.Equals(toolName, AuditToolName, StringComparison.Ordinal))
            {
                continue;
            }

            index++;
            if (!root.TryGetProperty("Arguments", out JsonElement args) || args.ValueKind != JsonValueKind.Object)
            {
                return new CveProposalAuditVerification(false, index - 1, head, index, "Missing Arguments object.");
            }

            TryGetString(root, "auditEventId", out string eventId);
            if (string.IsNullOrEmpty(eventId))
            {
                TryGetString(root, "AuditEventId", out eventId);
            }

            if (!TryGetString(root, "Timestamp", out string tsRaw) ||
                !DateTimeOffset.TryParse(tsRaw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset ts))
            {
                return new CveProposalAuditVerification(false, index - 1, head, index, "Invalid Timestamp.");
            }

            TryGetString(args, "ticketId", out string ticketId);
            TryGetString(args, "cveId", out string cveId);
            TryGetString(args, "desiredRevision", out string desiredRevision);
            TryGetString(args, "proposalDigest", out string proposalDigest);
            TryGetString(args, "proposalId", out string proposalId);
            TryGetString(args, "endpoint", out string endpoint);
            TryGetString(args, "previousChainHash", out string previousHash);
            TryGetString(args, "entryChainHash", out string entryHash);

            if (!string.Equals(previousHash, expectedPrevious, StringComparison.Ordinal))
            {
                return new CveProposalAuditVerification(
                    false, index - 1, head, index,
                    $"Chain link broken at entry {index}: expected previous hash {expectedPrevious}.");
            }

            string recomputed = ComputeChainHash(
                eventId, ts, ticketId, cveId, desiredRevision, proposalDigest,
                string.IsNullOrEmpty(proposalId) ? null : proposalId,
                endpoint, previousHash, chainKey);

            if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(entryHash), Encoding.UTF8.GetBytes(recomputed)))
            {
                return new CveProposalAuditVerification(
                    false, index - 1, head, index,
                    $"Chain hash tampered at entry {index}.");
            }

            head = entryHash;
            expectedPrevious = entryHash;
        }

        return new CveProposalAuditVerification(true, index, head, null, null);
    }

    private static string ComputeProposalDigest(
        string ticketId,
        string cveId,
        string releaseVersion,
        string serverDigest,
        string? workerDigest,
        string releaseLockRef,
        string cfnServerImageUri)
    {
        string canonical = string.Join(
            "|",
            ProposalKindValue,
            ticketId,
            cveId,
            releaseVersion,
            serverDigest,
            workerDigest ?? string.Empty,
            releaseLockRef,
            cfnServerImageUri);

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static string RequireNonBlank(string? value, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"{paramName} is required.", paramName);
        }

        return value.Trim();
    }

    private static string ValidateDigest(string? value, string paramName)
    {
        if (!IsImmutableImageDigest(value))
        {
            throw new ArgumentException(
                $"{paramName} must be an immutable image digest in the format 'registry/repo@sha256:<64hex>'.",
                paramName);
        }

        return value!.Trim();
    }

    private static string SanitizeSegment(string value)
    {
        char[] chars = value.ToLowerInvariant()
            .Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-')
            .ToArray();
        return new string(chars).Trim('-');
    }

    private static bool TryGetString(JsonElement obj, string name, out string value)
    {
        if (obj.TryGetProperty(name, out JsonElement prop) && prop.ValueKind == JsonValueKind.String)
        {
            value = prop.GetString() ?? string.Empty;
            return true;
        }

        value = string.Empty;
        return false;
    }
}
