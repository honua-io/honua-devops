using System.Text.RegularExpressions;

namespace Honua.DevOps.Agent.Operations;

/// <summary>
/// The typed refusal the honua-iac exact-plan/apply wrappers emit when they fail
/// closed before a mutation.
/// </summary>
/// <remarks>
/// <para>
/// The wrappers exit <c>3</c> and print <c>REFUSED[&lt;reason&gt;]: &lt;message&gt;</c>
/// on stderr for every row of the fail-closed matrix in
/// <c>docs/devops/terraform-exact-plan-contract.md</c>. Those reason codes are the
/// whole point of the substrate: `state-serial-drift` and `account-mismatch` are
/// different operator problems with different fixes, and collapsing them into one
/// "terraform failed" string throws away the only signal that distinguishes them.
/// </para>
/// <para>
/// So a refusal is surfaced as its own status — <c>iac-refused-&lt;reason&gt;</c> —
/// rather than as free text inside a generic failure.
/// </para>
/// </remarks>
internal sealed record TerraformExactRefusal(string Reason, string Message)
{
    private static readonly Regex RefusalPattern = new(
        @"REFUSED\[(?<reason>[a-z0-9-]{1,64})\](?::\s*(?<message>.*))?",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>Exit status the wrappers use for a fail-closed refusal.</summary>
    internal const int RefusedExitCode = 3;

    /// <summary>Exit status the wrappers use for a caller usage error.</summary>
    internal const int UsageExitCode = 2;

    /// <summary>
    /// Every reason code the substrate documents. A refusal carrying an unknown
    /// reason is still surfaced (the substrate may add rows before this list is
    /// updated), but a known reason is one honua-devops can explain, so the roster is
    /// asserted by tests against the published contract document.
    /// </summary>
    internal static readonly IReadOnlySet<string> KnownReasons = new HashSet<string>(StringComparer.Ordinal)
    {
        "saved-plan-missing",
        "plan-metadata-missing",
        "metadata-tampered",
        "approval-digest-mismatch",
        "approval-binding-missing",
        "action-mismatch",
        "plan-expired",
        "saved-plan-tampered",
        "unqualified-plan-refused",
        "concurrent-claim",
        "plan-already-claimed",
        "terraform-version-changed",
        "provider-lock-changed",
        "source-changed",
        "mutable-source",
        "provider-lock-missing",
        "backend-substituted",
        "local-state-refused",
        "lock-posture-missing",
        "lock-primitive-unsupported",
        "workspace-mismatch",
        "account-mismatch",
        "role-mismatch",
        "long-lived-credential-refused",
        "input-digest-changed",
        "state-lineage-changed",
        "state-serial-drift",
        // Emitted by the shared contract library rather than the matrix table.
        "artifact-missing",
        "backend-uninitialized",
        "identity-unavailable",
        "identity-unrecognized",
        "input-missing"
    };

    /// <summary>
    /// Parses a wrapper result into a typed refusal. Returns false when the process
    /// failed for some other reason (a genuine Terraform error, a timeout, a crash),
    /// which must not be dressed up as a governed refusal.
    /// </summary>
    internal static bool TryParse(ProvisioningProcessResult result, out TerraformExactRefusal? refusal)
    {
        refusal = null;
        if (result.TimedOut)
        {
            return false;
        }

        // stderr first: `refuse` logs there. stdout is checked too so a wrapper that
        // is ever run with 2>&1 merged still yields a typed reason.
        foreach (string stream in new[] { result.StandardError, result.StandardOutput })
        {
            Match match = RefusalPattern.Match(stream ?? string.Empty);
            if (!match.Success)
            {
                continue;
            }

            string reason = match.Groups["reason"].Value;
            string message = Redaction.Scrub(match.Groups["message"].Value).Trim();
            refusal = new TerraformExactRefusal(reason, message);
            return true;
        }

        return false;
    }

    /// <summary>The stable, greppable tool status for this refusal.</summary>
    internal string Status => $"iac-refused-{Reason}";

    internal bool IsKnown => KnownReasons.Contains(Reason);
}
