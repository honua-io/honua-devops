using System.ComponentModel;
using System.Text.Json;

using Honua.DevOps.Agent.Operations.GitOps;

namespace Honua.DevOps.Agent.Operations;

// Fix-forward (roll-forward convergence) operator primitive. This is the release's
// operate safety story: a single-environment deploy verified against health, where an
// UNHEALTHY outcome is recovered by proposing another FORWARD change — never a rollback.
// Plan-only and default-safe: it reads health/status evidence and emits an ordered
// forward-convergence plan; it never mutates, submits, promotes, or rolls back.
internal sealed partial class HonuaOperationsToolkit
{
    [Description(
        "Health-gated FIX-FORWARD (roll-forward convergence) planner: the release's operate loop. " +
        "Verifies the health of a single-environment deploy and, when unhealthy, returns an ordered " +
        "plan to recover by rolling FORWARD (propose a corrected revision -> re-deploy through the " +
        "governed create path -> re-verify health), NEVER by rolling back. Reads live readiness + " +
        "deploy-preflight, and (when priorOperationId is supplied) the prior operation's terminal " +
        "status, failing phase/error, smoke evidence, and whether the server itself rolled it back. " +
        "Read-only and plan-only: it issues no mutation, submit, promotion, or rollback. Returns overall " +
        "readiness of healthy-converged / forward-fix-required / backend-unavailable. This is the " +
        "operator flow to use for recovery in this release, where the rollback surface is disabled.")]
    public async Task<OperationResponse> PlanForwardFixAsync(
        string service,
        string environment,
        string forwardRevision,
        string priorOperationId,
        string symptoms,
        CancellationToken cancellationToken = default)
    {
        string normalizedService = ValidateServiceName(service);
        string[] parsedEnvironments = ParseEnvironments(environment);
        if (parsedEnvironments.Length != 1)
        {
            throw new InvalidOperationException(
                "Fix-forward operates on a single environment; supply exactly one environment.");
        }

        string normalizedEnvironment = parsedEnvironments[0];
        string normalizedRevision = ValidateRevision(Normalize(forwardRevision, "HEAD"), "forwardRevision");
        string normalizedSymptoms = SanitizeFreeText(symptoms, "not provided");
        string normalizedOperationId = Normalize(priorOperationId, string.Empty);
        bool hasPriorOperation = normalizedOperationId.Length > 0;
        if (hasPriorOperation &&
            (normalizedOperationId.Length > 200 ||
             normalizedOperationId.Any(character => char.IsWhiteSpace(character) || char.IsControl(character))))
        {
            throw new InvalidOperationException(
                "priorOperationId must be 1-200 characters with no whitespace or control characters.");
        }

        // Health verification building blocks (all read-only): live readiness + deploy preflight,
        // plus the prior operation's terminal/smoke evidence when a priorOperationId is supplied.
        Task<BackendCallResult> readinessTask = gateway.ProbeHonuaAsync(cancellationToken);
        Task<BackendCallResult> preflightTask = gateway.RequestDeployPreflightAsync(includeDiagnostics: true, cancellationToken);
        await Task.WhenAll(readinessTask, preflightTask);
        BackendCallResult readiness = readinessTask.Result;
        BackendCallResult preflight = preflightTask.Result;

        bool backendReachable = readiness.IsSuccess || preflight.IsSuccess;
        bool liveHealthy = readiness.IsSuccess && preflight.IsSuccess;

        string? priorStatus = null;
        string? priorPhase = null;
        string? priorError = null;
        bool priorRolledBack = false;
        bool priorSmokeEvidence = false;
        bool priorTerminalSuccess = false;
        bool priorReadable = false;

        List<string> findings =
        [
            $"Fix-forward target: service `{normalizedService}` in single environment `{normalizedEnvironment}`.",
            $"Proposed forward revision: `{normalizedRevision}`.",
            $"Reported symptoms: {normalizedSymptoms}.",
            $"Live readiness probe: {(readiness.IsSuccess ? "healthy" : "unhealthy")} ({readiness.Detail}).",
            $"Deploy preflight: {(preflight.IsSuccess ? "healthy" : "unhealthy")} ({preflight.Detail})."
        ];

        if (hasPriorOperation)
        {
            using BackendJsonResult prior = await gateway.GetDeployOperationJsonAsync(normalizedOperationId, cancellationToken);
            if (prior.CallResult.IsSuccess && prior.Payload is not null)
            {
                priorReadable = true;
                JsonElement root = prior.Payload.RootElement;
                priorStatus = DeployOperationReader.ReadStatus(root);
                priorPhase = DeployOperationReader.ReadCurrentPhase(root);
                priorError = DeployOperationReader.ReadErrorMessage(root);
                priorRolledBack = DeployOperationReader.IsRolledBack(priorStatus);
                priorSmokeEvidence = DeployOperationReader.HasSmokeEvidence(root);
                priorTerminalSuccess = DeployOperationReader.IsSuccess(priorStatus);
                findings.Add(
                    $"Prior operation `{normalizedOperationId}`: status={priorStatus ?? "unknown"}; phase={priorPhase ?? "unknown"}; " +
                    $"rolledBack={priorRolledBack}; smokeEvidence={priorSmokeEvidence}; error={priorError ?? "none"}.");
            }
            else
            {
                findings.Add(
                    $"Prior operation `{normalizedOperationId}` could not be read ({prior.CallResult.Detail}); no operation invented.");
            }
        }

        // Health verdict. Unhealthy when the live signals are red, OR the prior operation ended in a
        // non-success terminal state / was rolled back by the server. A prior server-side rollback is
        // itself a signal that a FORWARD fix is now required.
        bool priorUnhealthy = priorReadable && (priorRolledBack || (!priorTerminalSuccess && priorStatus is not null));
        bool healthy = backendReachable && liveHealthy && !priorUnhealthy;

        List<string> forwardSteps =
        [
            $"1. Diagnose the failing signal: run honua_diagnose for `{normalizedService}` in `{normalizedEnvironment}` " +
                "using the symptoms and the prior operation's failing phase/error (diagnose only; no reverting).",
            $"2. Propose the corrected FORWARD change: prepare revision `{normalizedRevision}` that fixes the root cause " +
                "(code/config/metadata), advancing state forward rather than reverting.",
            $"3. Re-deploy to the SAME single environment `{normalizedEnvironment}` through the governed create path " +
                "(deploy_service_gitops with a sync action, submitImmediately=false, approval-gated) — a single-environment forward deploy.",
            "4. Re-verify health by re-running plan_forward_fix; if still unhealthy, iterate with another FORWARD fix. " +
                "Converge by rolling forward; do not revert."
        ];

        List<string> healthySteps =
        [
            $"Health verified for `{normalizedService}` in `{normalizedEnvironment}`; no forward fix required.",
            "Continue monitoring readiness/preflight and smoke evidence; re-run plan_forward_fix if a regression appears."
        ];

        string status;
        string summary;
        List<string> actions;
        if (!backendReachable)
        {
            status = "backend-unavailable";
            summary = $"Backend not reachable to verify health for `{normalizedService}` in `{normalizedEnvironment}`; emitting the fix-forward plan skeleton.";
            actions = forwardSteps;
        }
        else if (healthy)
        {
            status = "healthy-converged";
            summary = $"`{normalizedService}` in `{normalizedEnvironment}` is healthy; the deploy has converged with no rollback needed.";
            actions = healthySteps;
        }
        else
        {
            status = "forward-fix-required";
            summary = $"`{normalizedService}` in `{normalizedEnvironment}` is unhealthy; converge by rolling FORWARD (rollback is not used in this release).";
            actions = forwardSteps;
        }

        List<string> validationChecks =
        [
            "Health is verified from live readiness + deploy preflight (and prior-operation smoke/terminal evidence when supplied).",
            "The recovery path is single-environment and forward-only; it never issues a rollback or a cross-environment promotion.",
            "Plan-only: this tool mutates nothing and submits nothing; the forward re-deploy runs through the governed, approval-gated create path."
        ];

        List<string> risks =
        [
            "Rollback is disabled for this release; unresolved failures must be recovered by rolling forward, so keep a known-good forward revision ready.",
            hasPriorOperation && priorRolledBack
                ? "The prior operation was rolled back server-side; the forward fix must address the root cause the smoke gate caught."
                : "If the forward fix does not clear the failing signal, iterate forward rather than reverting."
        ];

        return new OperationResponse(
            Status: status,
            Summary: summary,
            Findings: findings,
            Actions: actions,
            ValidationChecks: validationChecks,
            Risks: risks);
    }
}
