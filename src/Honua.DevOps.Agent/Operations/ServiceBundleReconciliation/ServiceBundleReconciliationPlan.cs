namespace Honua.DevOps.Agent.Operations.ServiceBundleReconciliation;

internal sealed record ServiceBundleDriftScope(
    string Scope,
    string ExportSource,
    string ComparisonMode,
    IReadOnlyList<string> LifecycleActions,
    IReadOnlyList<string> EvidenceRequirements);

internal sealed record ServiceBundleReconciliationPlan(
    string Strategy,
    string IdempotencyModel,
    string DriftModel,
    string ExportMode,
    string LongRunningOperationMode,
    string CurrentStateSummary,
    IReadOnlyList<ServiceBundleDriftScope> DriftScopes,
    IReadOnlyList<ServiceBundleReconciliationOperation> Operations);
