namespace Honua.DevOps.Agent.Operations.Actuation;

// The response-level actuation invariant shared by runbook, remediation, rollback, deploy,
// and every future write tool (issue #151).
//
// Models, Console, audit consumers, and release evidence reason from the top-level status.
// So a status token is a claim about what the ACTUATOR AUTHORITY did, and this guard is the
// one place that maps an authoritative ActuationResult onto a status token — and refuses to
// emit a contradictory pairing.
//
// The invariant, stated once:
//
//   executed/applied  <=>  a typed actuator identity and target
//                     AND  a durable operation/action or canonical backend receipt
//                     AND  Mutated == true
//                     AND  at least one successful BackendStep with MutatesState == true
//                     AND  a terminal success outcome from the actuator authority
//
// Everything else — in-progress, awaiting-approval, refused, unsupported, backend-error,
// unknown, indeterminate — keeps a distinct non-success status.
// A tool's status token family plus the established tokens it must keep. Overrides exist so
// this guard can become the single source of status truth WITHOUT renaming tokens that were
// already truthful — only the false claims change.
internal sealed record ActuationStatusVocabulary(
    string Family,
    string? Executed = null,
    string? RolledBack = null,
    string? Observed = null,
    string? PlanOnly = null,
    string? AwaitingApproval = null,
    string? ApprovalRequired = null);

internal static class ActuationResponseGuard
{
    // Tokens a tool may use to say a mutation completed. Every one of them must be backed by
    // an authoritative ActuationResult; StatusVocabularyTests enumerates them and requires
    // an actuator-backed test for each.
    internal static readonly IReadOnlySet<string> ExecutedStatusTokens =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "executed",
            "execute-succeeded",
            "runbook-executed",
            "auto-remediation-applied",
            "rolled-back",
            "rollback-executed"
        };

    // Tokens that assert an action is ready to run. These require a resolved typed actuator.
    internal static readonly IReadOnlySet<string> ReadyStatusTokens =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "runbook-execute-ready",
            "auto-remediation-ready",
            "execute-enabled"
        };

    // Map the authoritative result onto the caller's status vocabulary. Each tool supplies
    // its token family and any established tokens it must keep; this method supplies the
    // truth about which one applies.
    internal static string ResolveStatus(ActuationStatusVocabulary vocabulary, ActuationResult result)
    {
        ArgumentNullException.ThrowIfNull(vocabulary);
        ArgumentNullException.ThrowIfNull(result);
        string family = vocabulary.Family;

        string status = result.Outcome switch
        {
            ActuationOutcome.UnsupportedAction => ActuationOutcome.UnsupportedAction,
            ActuationOutcome.ExperimentalDisabled => ActuationOutcome.ExperimentalDisabled,
            ActuationOutcome.PlanOnly => vocabulary.PlanOnly ?? $"{family}-plan-only",
            ActuationOutcome.Observed => vocabulary.Observed ?? $"{family}-observed",
            ActuationOutcome.AwaitingApproval => vocabulary.AwaitingApproval ?? $"{family}-awaiting-approval",
            ActuationOutcome.ApprovalRequired => vocabulary.ApprovalRequired ?? $"{family}-approval-required",
            ActuationOutcome.InProgress => $"{family}-in-progress",
            ActuationOutcome.Executed => vocabulary.Executed ?? $"{family}-executed",
            ActuationOutcome.RolledBack => vocabulary.RolledBack ?? $"{family}-rolled-back",
            ActuationOutcome.Failed => $"{family}-failed",
            ActuationOutcome.Indeterminate => $"{family}-indeterminate",
            ActuationOutcome.ContractUnavailable => "contract-unavailable",
            _ => "backend-error"
        };

        Validate(status, result);
        return status;
    }

    // Reject contradictory response construction. This runs on every write-capable response
    // so a contradiction fails loudly here rather than becoming machine-readable evidence
    // that something was applied when it was not.
    internal static void Validate(string status, ActuationResult? result)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            throw new InvalidOperationException("An actuation response must carry a status token.");
        }

        bool claimsExecuted = ClaimsExecution(status);
        bool claimsReady = ReadyStatusTokens.Contains(status);

        if (!claimsExecuted && !claimsReady)
        {
            // Non-success, non-ready statuses still must not carry a mutated result that
            // says otherwise (e.g. "failed" alongside a successful mutating receipt).
            if (result is not null && result.IsAuthoritativeSuccess)
            {
                throw new InvalidOperationException(
                    $"Status `{status}` contradicts the actuator result: the authority reported terminal success with a receipt " +
                    $"({result.Receipt!.ReceiptId}) and a successful mutating backend step.");
            }

            return;
        }

        if (result is null)
        {
            throw new InvalidOperationException(
                $"Status `{status}` claims an action is executed or ready, but no actuator result was produced. " +
                "Policy configuration or caller intent is not an implemented action.");
        }

        if (claimsReady)
        {
            if (result.Outcome == ActuationOutcome.UnsupportedAction)
            {
                throw new InvalidOperationException(
                    $"Status `{status}` claims readiness, but no typed actuator is registered for `{result.Action}`.");
            }

            return;
        }

        if (result.Outcome == ActuationOutcome.UnsupportedAction)
        {
            throw new InvalidOperationException(
                $"Status `{status}` claims execution, but no typed actuator is registered for `{result.Action}`.");
        }

        if (!ActuationOutcome.SuccessOutcomes.Contains(result.Outcome)
            && result.Outcome != ActuationOutcome.RolledBack)
        {
            throw new InvalidOperationException(
                $"Status `{status}` claims execution, but the actuator authority reported `{result.Outcome}`.");
        }

        if (!result.Mutated)
        {
            throw new InvalidOperationException(
                $"Status `{status}` claims execution, but the actuator result reports Mutated=false.");
        }

        if (result.Receipt is null)
        {
            throw new InvalidOperationException(
                $"Status `{status}` claims execution without a durable operation/action receipt. " +
                "DevOps never invents a receipt when the upstream authority did not return one.");
        }

        if (!result.BackendSteps.Any(step => step.MutatesState && step.Success))
        {
            throw new InvalidOperationException(
                $"Status `{status}` claims execution without a successful mutating backend step.");
        }
    }

    // The audit `Mutated` flag and the response status must come from the SAME authoritative
    // result. This is the only sanctioned way to derive it.
    internal static bool ResolveMutated(ActuationResult? result)
        => result?.Mutated == true;

    private static bool ClaimsExecution(string status)
        => ExecutedStatusTokens.Contains(status)
            || status.EndsWith("-executed", StringComparison.Ordinal)
            || status.EndsWith("-applied", StringComparison.Ordinal);
}
