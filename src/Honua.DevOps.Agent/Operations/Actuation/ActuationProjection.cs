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
                execution.ActuatorReceiptId ?? execution.OperationId!,
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
            BlockingReasons: execution.BlockingReasons,
            IdempotencyKey: execution.IdempotencyKey);
    }
}
