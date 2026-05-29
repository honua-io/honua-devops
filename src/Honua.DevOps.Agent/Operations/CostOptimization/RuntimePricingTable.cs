namespace Honua.DevOps.Agent.Operations.CostOptimization;

// Static, approximate pricing reference for the six supported runtime targets.
//
// IMPORTANT: these are NOT live prices. They are coarse, list-price-derived
// approximations used only to compare *relative* cost between targets for a
// described workload shape. Every field that feeds a recommendation also feeds
// the `Assumptions` provenance list emitted on the plan so the operator can see
// exactly what the comparison is built on and override it with a real quote.
//
// Provenance for these figures is documented in
// docs/cost-optimization-planner.md ("Pricing assumptions"). They are intended
// to be refreshed by hand, not fetched, and should be treated as directional.
internal sealed record RuntimePricingFactors(
    string Target,
    string Family,
    // Billing model the family bills on, for explainability.
    string BillingModel,
    // Approx. USD per vCPU-hour for the always-on portion of the workload
    // (managed-container / kubernetes families). Serverless targets set this to
    // 0 because they bill per request + per GB-second instead.
    decimal VCpuHour,
    // Approx. USD per GB-hour of memory for the always-on portion.
    decimal MemoryGbHour,
    // Approx. USD per 1M requests for request-billed (serverless) targets.
    decimal PerMillionRequests,
    // Approx. USD per GB-second of allocated compute for serverless targets.
    decimal PerGbSecond,
    // Fixed monthly platform overhead (control plane, cluster fee, NAT, etc.)
    // amortized into the comparison. Captures e.g. the EKS/AKS control-plane fee
    // and baseline node overhead the workload cannot avoid.
    decimal MonthlyPlatformOverhead,
    // Fraction (0..1) of provisioned capacity that is actually billable revenue
    // work for a steady, always-on workload. Lower means more idle waste; this
    // is where serverless wins on spiky/low-duty workloads and loses on
    // sustained ones.
    double SteadyStateUtilizationCeiling);

internal static class RuntimePricingTable
{
    // Reference month length used to convert hourly rates to monthly estimates.
    internal const decimal HoursPerMonth = 730m;

    // Assumptions provenance attached to every plan. Keep this honest: it is the
    // contract that tells the operator the numbers are directional, not billable.
    internal static readonly IReadOnlyList<string> Assumptions =
    [
        "Pricing is STATIC and APPROXIMATE (US list-price derived, single primary region, on-demand). It is NOT a live quote and must not be used for billing or contractual commitments.",
        "Estimates compare RELATIVE cost between targets for the described workload shape; absolute dollar figures are directional only.",
        "Serverless targets (azure-functions, lambda) are modeled per-request plus per-GB-second; idle time is assumed to cost ~0.",
        "Container/Kubernetes targets are modeled as provisioned vCPU-hours plus memory-GB-hours running continuously, plus a fixed monthly platform overhead.",
        "Kubernetes targets (aks, eks) include a control-plane and baseline-node overhead; they amortize best at higher sustained utilization.",
        "Reserved capacity, savings plans, spot, committed-use discounts, egress, storage, and data-transfer costs are NOT modeled.",
        "1 month = 730 hours. Memory is assumed proportional to the requested vCPU when not explicitly supplied.",
        "Refresh these figures from docs/cost-optimization-planner.md before relying on them; override with a real cloud quote for procurement.",
    ];

    private static readonly IReadOnlyDictionary<string, RuntimePricingFactors> Factors =
        new Dictionary<string, RuntimePricingFactors>(StringComparer.OrdinalIgnoreCase)
        {
            ["azure-functions"] = new(
                Target: "azure-functions",
                Family: "serverless",
                BillingModel: "per-request + per-GB-second",
                VCpuHour: 0m,
                MemoryGbHour: 0m,
                PerMillionRequests: 0.20m,
                PerGbSecond: 0.000016m,
                MonthlyPlatformOverhead: 0m,
                SteadyStateUtilizationCeiling: 1.0),
            ["lambda"] = new(
                Target: "lambda",
                Family: "serverless",
                BillingModel: "per-request + per-GB-second",
                VCpuHour: 0m,
                MemoryGbHour: 0m,
                PerMillionRequests: 0.20m,
                PerGbSecond: 0.0000166667m,
                MonthlyPlatformOverhead: 0m,
                SteadyStateUtilizationCeiling: 1.0),
            ["aca"] = new(
                Target: "aca",
                Family: "managed-container",
                BillingModel: "provisioned vCPU-hour + memory-GB-hour",
                VCpuHour: 0.0432m,
                MemoryGbHour: 0.0043m,
                PerMillionRequests: 0m,
                PerGbSecond: 0m,
                MonthlyPlatformOverhead: 0m,
                SteadyStateUtilizationCeiling: 0.75),
            ["ecs"] = new(
                Target: "ecs",
                Family: "managed-container",
                BillingModel: "provisioned vCPU-hour + memory-GB-hour (Fargate)",
                VCpuHour: 0.04048m,
                MemoryGbHour: 0.004445m,
                PerMillionRequests: 0m,
                PerGbSecond: 0m,
                MonthlyPlatformOverhead: 0m,
                SteadyStateUtilizationCeiling: 0.75),
            ["aks"] = new(
                Target: "aks",
                Family: "kubernetes",
                BillingModel: "node vCPU-hour + memory-GB-hour + control plane",
                VCpuHour: 0.031m,
                MemoryGbHour: 0.004m,
                PerMillionRequests: 0m,
                PerGbSecond: 0m,
                MonthlyPlatformOverhead: 73m,
                SteadyStateUtilizationCeiling: 0.6),
            ["eks"] = new(
                Target: "eks",
                Family: "kubernetes",
                BillingModel: "node vCPU-hour + memory-GB-hour + control plane",
                VCpuHour: 0.034m,
                MemoryGbHour: 0.004m,
                PerMillionRequests: 0m,
                PerGbSecond: 0m,
                MonthlyPlatformOverhead: 73m,
                SteadyStateUtilizationCeiling: 0.6),
        };

    internal static IReadOnlyCollection<string> SupportedTargets => Factors.Keys.ToArray();

    internal static RuntimePricingFactors Resolve(string target)
    {
        if (Factors.TryGetValue(target.Trim(), out RuntimePricingFactors? factors))
        {
            return factors;
        }

        throw new InvalidOperationException(
            $"No pricing factors for runtime target `{target}`. Supported targets: {string.Join(", ", SupportedTargets)}.");
    }
}
