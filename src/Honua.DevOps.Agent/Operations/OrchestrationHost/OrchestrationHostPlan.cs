namespace Honua.DevOps.Agent.Operations.OrchestrationHost;

internal sealed record OrchestrationHostPlan(
    OperatorWorkflowFamily WorkflowFamily,
    string HostTarget,
    string Environment,
    string OperatorGoal,
    string? PackageReference,
    string? DeploymentTarget,
    bool PublishExternally,
    IReadOnlyList<string> ContractSurfaces,
    IReadOnlyList<string> AzureIntegrationPoints,
    IReadOnlyList<OrchestrationHostStagePlan> Stages,
    IReadOnlyList<string> EvaluationHooks,
    IReadOnlyList<string> BoundaryRules)
{
    internal IReadOnlyList<string> RequiredChecks => Stages
        .SelectMany(stage => stage.RequiredChecks)
        .Distinct(StringComparer.Ordinal)
        .ToArray();

    internal string GateStatus => PublishExternally
        ? "approval-required"
        : "plan-ready";
}
