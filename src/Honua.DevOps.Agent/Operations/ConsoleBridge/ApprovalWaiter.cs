using System.Globalization;
using System.Text.Json;
using Honua.DevOps.Agent.Operations.OperatorPolicy;
using OperatorPolicyModel = Honua.DevOps.Agent.Operations.OperatorPolicy.OperatorPolicy;

namespace Honua.DevOps.Agent.Operations.ConsoleBridge;

// Agent-side human-in-the-loop approval waiter (Phase 1 actuation spine).
//
// The agent creates-and-pauses a deploy-control operation on `pr-first` (it records the
// operation with submitImmediately=false and never advances it). This waiter is the
// *approve* side from the agent's perspective: it polls the honua-server deploy-control
// operation until the operation leaves AwaitingApproval — either because an operator
// approved it in Console (Submitted / Reconciling / a terminal state) or, under
// direct-allowed policy, because the waiter itself submitted it per policy. It honors a
// bounded timeout (HONUA_DEVOPS_APPROVAL_TIMEOUT_SECONDS, default 3600) and tolerates
// transient backend errors with capped exponential backoff so a flaky read does not abort
// a long approval wait.
//
// Safety: in pr-first / break-glass-only the waiter is strictly read-only — it waits for a
// human, it never submits. Only direct-allowed may submit, and even then only when the
// operation is genuinely AwaitingApproval. The server's OperatorApprovalGate remains the
// real gate; the waiter submits through the same governed /submit endpoint.
internal sealed class ApprovalWaiter
{
    internal const string ApprovalTimeoutSecondsVariable = "HONUA_DEVOPS_APPROVAL_TIMEOUT_SECONDS";
    internal const int DefaultApprovalTimeoutSeconds = 3600;

    private readonly BackendGateway _gateway;
    private readonly OperatorPolicyModel _policy;
    private readonly TimeSpan _timeout;
    private readonly TimeSpan _initialPollInterval;
    private readonly TimeSpan _maxPollInterval;
    private readonly int _maxConsecutiveTransientErrors;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly TimeProvider _timeProvider;

    internal ApprovalWaiter(
        BackendGateway gateway,
        OperatorPolicyModel policy,
        TimeSpan? timeout = null,
        TimeSpan? initialPollInterval = null,
        TimeSpan? maxPollInterval = null,
        int maxConsecutiveTransientErrors = 10,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        TimeProvider? timeProvider = null)
    {
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _timeout = timeout ?? ResolveTimeout();
        _initialPollInterval = initialPollInterval ?? TimeSpan.FromSeconds(5);
        _maxPollInterval = maxPollInterval ?? TimeSpan.FromSeconds(30);
        _maxConsecutiveTransientErrors = Math.Max(1, maxConsecutiveTransientErrors);
        _delay = delay ?? ((interval, token) => Task.Delay(interval, token));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    internal TimeSpan Timeout => _timeout;

    internal static TimeSpan ResolveTimeout()
    {
        string? raw = Environment.GetEnvironmentVariable(ApprovalTimeoutSecondsVariable);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return TimeSpan.FromSeconds(DefaultApprovalTimeoutSeconds);
        }

        if (!int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int seconds) || seconds < 1)
        {
            throw new InvalidOperationException(
                $"Environment variable `{ApprovalTimeoutSecondsVariable}` must be a positive integer (seconds).");
        }

        return TimeSpan.FromSeconds(seconds);
    }

    // Polls the deploy-control operation until it leaves AwaitingApproval (or a terminal
    // state is reached, or the timeout elapses). Returns a structured outcome the caller can
    // report and turn into a process exit code.
    internal async Task<ApprovalWaitResult> WaitAsync(string operationId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(operationId))
        {
            throw new ArgumentException("Operation id must not be empty.", nameof(operationId));
        }

        string normalizedOperationId = operationId.Trim();
        DateTimeOffset deadline = _timeProvider.GetUtcNow() + _timeout;
        TimeSpan pollInterval = _initialPollInterval;
        int consecutiveTransientErrors = 0;
        bool submitAttempted = false;
        int pollCount = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            pollCount++;
            ReadOutcome read = await ReadStatusAsync(normalizedOperationId, cancellationToken).ConfigureAwait(false);

            if (read.Success)
            {
                consecutiveTransientErrors = 0;
                string? status = read.Status;

                if (status is null)
                {
                    // The operation could not be located (deleted, or never created). This is
                    // terminal for the wait — there is nothing to approve.
                    return ApprovalWaitResult.NotFound(normalizedOperationId, pollCount);
                }

                if (!IsAwaitingApproval(status))
                {
                    // Left AwaitingApproval: Submitted / Reconciling / Succeeded / Failed /
                    // RolledBack / etc. The wait is satisfied; report the resolved status.
                    return ApprovalWaitResult.Resolved(normalizedOperationId, status, submitAttempted, pollCount);
                }

                // Still awaiting approval. Under direct-allowed policy the waiter is permitted
                // to advance the operation itself (one attempt); pr-first / break-glass-only
                // strictly wait for a human to approve in Console.
                if (!submitAttempted && _policy.ApprovalMode == ApprovalMode.DirectAllowed)
                {
                    submitAttempted = true;
                    bool submitted = await TrySubmitAsync(normalizedOperationId, cancellationToken).ConfigureAwait(false);
                    if (submitted)
                    {
                        // Re-read promptly so the next loop reflects the post-submit status
                        // rather than waiting a full backoff interval.
                        pollInterval = _initialPollInterval;
                        continue;
                    }
                    // Submit failed (e.g. the server gate denied it); fall through and keep
                    // waiting for a human decision rather than retrying the submit in a loop.
                }
            }
            else
            {
                consecutiveTransientErrors++;
                if (consecutiveTransientErrors >= _maxConsecutiveTransientErrors)
                {
                    return ApprovalWaitResult.Errored(
                        normalizedOperationId,
                        $"Gave up after {consecutiveTransientErrors} consecutive transient errors reading the operation.",
                        pollCount);
                }
            }

            if (_timeProvider.GetUtcNow() >= deadline)
            {
                return ApprovalWaitResult.TimedOut(normalizedOperationId, _timeout, pollCount);
            }

            // Bounded wait before the next poll, never overshooting the deadline.
            TimeSpan remaining = deadline - _timeProvider.GetUtcNow();
            if (remaining <= TimeSpan.Zero)
            {
                return ApprovalWaitResult.TimedOut(normalizedOperationId, _timeout, pollCount);
            }

            TimeSpan wait = pollInterval < remaining ? pollInterval : remaining;
            await _delay(wait, cancellationToken).ConfigureAwait(false);

            // Exponential backoff (capped) only after a transient error; steady-state polling
            // stays at the configured interval so an operator approval is reflected promptly.
            pollInterval = consecutiveTransientErrors > 0
                ? Min(_maxPollInterval, pollInterval + pollInterval)
                : _initialPollInterval;
        }
    }

    private async Task<ReadOutcome> ReadStatusAsync(string operationId, CancellationToken cancellationToken)
    {
        try
        {
            using BackendJsonResult result = await _gateway
                .GetDeployOperationJsonAsync(operationId, cancellationToken)
                .ConfigureAwait(false);

            if (!result.CallResult.IsSuccess)
            {
                // A 404 means the operation does not exist (terminal); other non-success
                // results are treated as transient and retried.
                if (LooksLikeNotFound(result.CallResult.Detail))
                {
                    return ReadOutcome.Located(status: null);
                }

                return ReadOutcome.Transient();
            }

            JsonElement? root = result.Payload?.RootElement;
            if (root is null)
            {
                return ReadOutcome.Transient();
            }

            string? status = ExtractStatus(root.Value);
            return ReadOutcome.Located(status);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // Network/serialization hiccup — tolerate as transient.
            return ReadOutcome.Transient();
        }
    }

    private async Task<bool> TrySubmitAsync(string operationId, CancellationToken cancellationToken)
    {
        try
        {
            using BackendJsonResult result = await _gateway
                .SubmitDeployOperationJsonAsync(
                    operationId,
                    reason: "honua-devops --await-approval direct-allowed auto-submit",
                    cancellationToken)
                .ConfigureAwait(false);
            return result.CallResult.IsSuccess;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool IsAwaitingApproval(string status)
        => ServerOperationStatusParser.IsAwaitingApproval(status);

    private static bool LooksLikeNotFound(string? detail)
        => detail is not null
            && (detail.Contains("404", StringComparison.Ordinal)
                || detail.Contains("not found", StringComparison.OrdinalIgnoreCase));

    private static string? ExtractStatus(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (JsonProperty property in root.EnumerateObject())
        {
            if ((string.Equals(property.Name, "status", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(property.Name, "state", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(property.Name, "workflowStatus", StringComparison.OrdinalIgnoreCase))
                && property.Value.ValueKind == JsonValueKind.String)
            {
                string? value = property.Value.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value.Trim();
                }
            }
        }

        return null;
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    private readonly record struct ReadOutcome(bool Success, string? Status)
    {
        internal static ReadOutcome Located(string? status) => new(Success: true, Status: status);

        internal static ReadOutcome Transient() => new(Success: false, Status: null);
    }
}

// Structured outcome of an approval wait, kept provider-neutral so Program.cs can report it
// and map it to an exit code without re-deriving status semantics.
internal enum ApprovalWaitOutcome
{
    // Operation left AwaitingApproval into Submitted/Reconciling/terminal.
    Resolved,

    // The configured timeout elapsed while still AwaitingApproval.
    TimedOut,

    // The operation could not be found (deleted or never created).
    NotFound,

    // Repeated transient errors prevented the wait from completing.
    Errored
}

internal sealed record ApprovalWaitResult(
    ApprovalWaitOutcome Outcome,
    string OperationId,
    string? FinalStatus,
    bool SubmittedByWaiter,
    int Polls,
    string Summary)
{
    internal bool IsTerminalSuccess =>
        Outcome == ApprovalWaitOutcome.Resolved
        && FinalStatus is not null
        && IsSuccessTerminal(FinalStatus);

    internal static ApprovalWaitResult Resolved(string operationId, string status, bool submittedByWaiter, int polls)
        => new(
            ApprovalWaitOutcome.Resolved,
            operationId,
            status,
            submittedByWaiter,
            polls,
            submittedByWaiter
                ? $"Operation `{operationId}` left AwaitingApproval (now `{status}`) after the waiter submitted it under direct-allowed policy."
                : $"Operation `{operationId}` was approved and left AwaitingApproval (now `{status}`).");

    internal static ApprovalWaitResult TimedOut(string operationId, TimeSpan timeout, int polls)
        => new(
            ApprovalWaitOutcome.TimedOut,
            operationId,
            FinalStatus: "AwaitingApproval",
            SubmittedByWaiter: false,
            polls,
            $"Timed out after {timeout.TotalSeconds:0}s waiting for operation `{operationId}` to leave AwaitingApproval.");

    internal static ApprovalWaitResult NotFound(string operationId, int polls)
        => new(
            ApprovalWaitOutcome.NotFound,
            operationId,
            FinalStatus: null,
            SubmittedByWaiter: false,
            polls,
            $"No durable deploy-control operation was found for `{operationId}`.");

    internal static ApprovalWaitResult Errored(string operationId, string detail, int polls)
        => new(
            ApprovalWaitOutcome.Errored,
            operationId,
            FinalStatus: null,
            SubmittedByWaiter: false,
            polls,
            detail);

    private static bool IsSuccessTerminal(string status)
        => status.Trim().Replace("-", string.Empty, StringComparison.Ordinal).ToLowerInvariant() is "succeeded";
}
