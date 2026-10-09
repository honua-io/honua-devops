using Honua.DevOps.Agent.Configuration;

namespace Honua.DevOps.Agent.Operations;

/// <summary>
/// <c>honua-devops --issue-provision-approval</c>: reads a saved plan response, signs a
/// receipt as the approving principal, and writes the receipt JSON — and nothing else —
/// to stdout so it can be piped or captured as an artifact. Diagnostics go to stderr.
/// </summary>
internal static class IssueProvisionApprovalCommand
{
    /// <summary>Exit 0 = receipt written; 1 = refused; 2 = input could not be read.</summary>
    internal static async Task<int> RunAsync(
        IssueProvisionApprovalCliOptions options,
        TextWriter stdout,
        TextWriter stderr,
        Func<string, string?>? readVariable = null,
        IKmsMacClient? kmsMacClient = null,
        DateTimeOffset? now = null,
        CancellationToken cancellationToken = default)
    {
        readVariable ??= Environment.GetEnvironmentVariable;

        string planResponseJson;
        try
        {
            planResponseJson = await File.ReadAllTextAsync(options.PlanResponsePath, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            await stderr.WriteLineAsync($"plan-response-unreadable: {Redaction.Scrub(exception.Message)}");
            return 2;
        }

        IApprovalSignatureProvider provider;
        try
        {
            provider = ApprovalSignatureProviders.ForIssuer(options.SigningMode, readVariable, kmsMacClient);
        }
        catch (InvalidOperationException exception)
        {
            await stderr.WriteLineAsync($"issuer-configuration-invalid: {exception.Message}");
            return 1;
        }

        IssuedApprovalReceipt issued;
        try
        {
            issued = await ApprovalReceiptIssuer.IssueAsync(
                new ApprovalIssueRequest(
                    planResponseJson,
                    options.Action,
                    options.Issuer,
                    options.SigningMode,
                    options.TtlMinutes,
                    options.Decision,
                    options.Stack,
                    options.Environment),
                provider,
                now,
                cancellationToken);
        }
        catch (ApprovalIssueRefusedException refused)
        {
            await stderr.WriteLineAsync($"{refused.Code}: {refused.Message}");
            return 1;
        }

        await stdout.WriteLineAsync(issued.ReceiptJson);
        await stderr.WriteLineAsync(issued.Evidentiary
            ? $"Issued {issued.ApprovalReceiptId} ({options.SigningMode}, {options.Decision}, ttl {options.TtlMinutes}m)."
            : $"Issued {issued.ApprovalReceiptId} ({options.SigningMode}, {options.Decision}, ttl {options.TtlMinutes}m). NON-EVIDENTIARY: development mode; certification requires {ApprovalSigningModes.KmsMac}.");
        return 0;
    }
}
