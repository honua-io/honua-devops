using Honua.DevOps.Agent.Operations.GitOps;

namespace Honua.DevOps.Agent.Operations.Actuation;

// Projects an executor outcome into the authoritative ActuationResult that every
// write-capable response derives its status and `Mutated` flag from (issue #151).
//
// This is the ONLY place the executor vocabulary is translated. Tools do not re-infer
// success from a status string of their own, so the response status, the audit `Mutated`
// flag, and the backend steps can never disagree about what happened.
internal static class ActuationProjection
{
    internal static ActuationResult ToActuationResult(
        this GitOpsExecutionResult execution,
        string actuatorId,
        string action,
        string target)
    {
        ArgumentNullException.ThrowIfNull(execution);

        string outcome = execution.Status switch
        {
            GitOpsExecutionStatus.ExperimentalDisabled => ActuationOutcome.ExperimentalDisabled,
            GitOpsExecutionStatus.PlanOnly => ActuationOutcome.PlanOnly,
            GitOpsExecutionStatus.AwaitingApproval => ActuationOutcome.AwaitingApproval,
            GitOpsExecutionStatus.ApprovalRequired => ActuationOutcome.ApprovalRequired,
            GitOpsExecutionStatus.InProgress => ActuationOutcome.InProgress,
            GitOpsExecutionStatus.Succeeded => ActuationOutcome.Executed,
            GitOpsExecutionStatus.RolledBack => ActuationOutcome.RolledBack,
            GitOpsExecutionStatus.Failed => ActuationOutcome.Failed,
            GitOpsExecutionStatus.Indeterminate => ActuationOutcome.Indeterminate,
            GitOpsExecutionStatus.ContractUnavailable => ActuationOutcome.ContractUnavailable,
            _ => ActuationOutcome.BackendError
        };

        // The receipt is the durable server operation identity. When the control plane did
        // not return one it stays null and blocks every claim that depends on it; DevOps
        // never substitutes an identity of its own.
        ActuationReceipt? receipt = string.IsNullOrWhiteSpace(execution.OperationId)
            ? null
            : new ActuationReceipt(
                actuatorId,
                execution.OperationId!,
                "honua-server.deploy-control",
                execution.ServerStatus);

        return new ActuationResult(
            ActuatorId: actuatorId,
            Action: action,
            Target: target,
            Outcome: outcome,
            Mutated: execution.Mutated,
            Receipt: receipt,
            OperationId: execution.OperationId,
            BackendSteps: execution.BackendSteps,
            Findings: execution.Findings,
            BlockingReasons: execution.BlockingReasons);
    }

    // Projection for a registered READ-ONLY actuator: a real backend read that mutated
    // nothing. It can never resolve to executed/applied.
    internal static ActuationResult FromReadOnlyCall(
        ActuatorDescriptor descriptor,
        string target,
        BackendCallResult call,
        string stepName)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(call);

        return new ActuationResult(
            ActuatorId: descriptor.ActuatorId,
            Action: descriptor.Action,
            Target: target,
            Outcome: call.IsSuccess ? ActuationOutcome.Observed : ActuationOutcome.BackendError,
            Mutated: false,
            Receipt: null,
            OperationId: null,
            BackendSteps: [OperationBackendStep.From(stepName, call, mutatesState: false)],
            Findings: [$"{descriptor.Description} Endpoint: {call.Endpoint}. Result: {call.Detail}."],
            BlockingReasons: call.IsSuccess ? [] : ["backend-call-failed"]);
    }

    // Projection for a registered actuator that exists but was not permitted to run. No
    // backend call was made, so nothing can be claimed beyond the refusal itself.
    internal static ActuationResult NotPermitted(
        ActuatorDescriptor descriptor,
        string target,
        string outcome,
        string reason)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        return new ActuationResult(
            ActuatorId: descriptor.ActuatorId,
            Action: descriptor.Action,
            Target: target,
            Outcome: outcome,
            Mutated: false,
            Receipt: null,
            OperationId: null,
            BackendSteps: [],
            Findings: [reason],
            BlockingReasons: [outcome]);
    }
}
