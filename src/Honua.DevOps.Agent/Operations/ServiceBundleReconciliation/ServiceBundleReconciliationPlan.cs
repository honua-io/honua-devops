namespace Honua.DevOps.Agent.Operations.ServiceBundleReconciliation;

internal sealed record ServiceBundleDriftScope(
    string Scope,
    string ExportSource,
    string ComparisonMode,
    IReadOnlyList<string> LifecycleActions,
    IReadOnlyList<string> EvidenceRequirements,
    // Real drift verdict for the scope, computed from the runtime adapters' drift capability
    // and the control-plane actual-state export vs the desired revision. One of:
    //   drift-detected | no-drift | unreconcilable | drift-indeterminate
    // Defaults to "drift-indeterminate" so scopes that are not yet wired to a real detector
    // keep an honest, non-asserting verdict instead of claiming no drift.
    string DriftVerdict = "drift-indeterminate",
    string DriftDetail = "Drift verdict not evaluated for this scope.");

internal sealed record ServiceBundleReconciliationPlan(
    string Strategy,
    string IdempotencyModel,
    string DriftModel,
    string ExportMode,
    string LongRunningOperationMode,
    string CurrentStateSummary,
    IReadOnlyList<ServiceBundleDriftScope> DriftScopes,
    IReadOnlyList<ServiceBundleReconciliationOperation> Operations);
