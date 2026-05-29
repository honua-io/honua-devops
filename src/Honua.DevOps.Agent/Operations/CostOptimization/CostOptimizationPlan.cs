namespace Honua.DevOps.Agent.Operations.CostOptimization;

internal sealed record RightSizingSuggestion(
    string Target,
    string Dimension,
    string Suggestion,
    string Rationale);

internal sealed record TargetCostEstimate(
    string Target,
    string Family,
    string BillingModel,
    // Approximate monthly USD for the workload on this target.
    decimal EstimatedMonthlyUsd,
    // Cost relative to the cheapest viable target (cheapest = 1.00).
    double RelativeToCheapest,
    // Modeled effective utilization for provisioned families (1.0 for serverless).
    double EffectiveUtilization,
    // False when the workload shape disqualifies this target (e.g. serverless for
    // a stateful, always-on service). Disqualified targets are still costed and
    // shown, but never recommended.
    bool Viable,
    IReadOnlyList<string> Notes,
    IReadOnlyList<RightSizingSuggestion> RightSizing);

internal sealed record CostOptimizationPlan(
    string WorkloadSummary,
    string MetricsProvenance,
    // Targets ordered cheapest-first among viable options, then disqualified.
    IReadOnlyList<TargetCostEstimate> Estimates,
    string RecommendedTarget,
    string RecommendationRationale,
    // Documented, honest provenance of the pricing model. Always populated.
    IReadOnlyList<string> Assumptions);
