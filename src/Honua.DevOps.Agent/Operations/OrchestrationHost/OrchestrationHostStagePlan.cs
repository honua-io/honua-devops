namespace Honua.DevOps.Agent.Operations.OrchestrationHost;

internal sealed record OrchestrationHostStagePlan(
    OrchestrationStageKind Stage,
    string Status,
    string ModelRole,
    string DeterministicRole,
    string ContractSurface,
    string AzureHostResponsibility,
    IReadOnlyList<string> RequiredChecks);
