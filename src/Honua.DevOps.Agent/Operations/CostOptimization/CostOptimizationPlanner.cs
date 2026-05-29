using Honua.DevOps.Agent.Operations.RuntimeAdapters;

namespace Honua.DevOps.Agent.Operations.CostOptimization;

// Pure, read-only cost comparison across the supported runtime targets.
//
// No backend calls, no state mutation, no edition-gated capability. Given a
// normalized WorkloadShape and the static RuntimePricingTable, it produces a
// typed CostOptimizationPlan with a relative-cost estimate, right-sizing
// suggestions, and a single recommended target. All dollar figures are
// directional and carry the pricing assumptions provenance.
internal static class CostOptimizationPlanner
{
    private const decimal SecondsPerMonth = RuntimePricingTable.HoursPerMonth * 3600m;

    internal static CostOptimizationPlan Build(
        WorkloadShape workload,
        IReadOnlyList<string> targets)
    {
        if (targets.Count == 0)
        {
            throw new InvalidOperationException("At least one runtime target is required for a cost comparison.");
        }

        // Resolve every target through the real adapter registry so we never cost
        // a target the operator cannot actually deploy to, then enrich with the
        // pricing factors.
        IReadOnlyList<RuntimeAdapterCapability> capabilities = RuntimeAdapterCatalog.ResolveMany(targets);

        List<TargetCostEstimate> estimates = capabilities
            .Select(capability => Estimate(workload, capability))
            .ToList();

        decimal cheapestViable = estimates
            .Where(estimate => estimate.Viable)
            .Select(estimate => estimate.EstimatedMonthlyUsd)
            .DefaultIfEmpty(estimates.Min(estimate => estimate.EstimatedMonthlyUsd))
            .Min();

        // Avoid divide-by-zero for free-tier-shaped serverless workloads.
        decimal relativeBase = cheapestViable <= 0m ? 0.01m : cheapestViable;

        estimates = estimates
            .Select(estimate => estimate with
            {
                RelativeToCheapest = Math.Round((double)(estimate.EstimatedMonthlyUsd / relativeBase), 2),
            })
            // Viable first, then cheapest first within each group.
            .OrderByDescending(estimate => estimate.Viable)
            .ThenBy(estimate => estimate.EstimatedMonthlyUsd)
            .ToList();

        TargetCostEstimate recommended = estimates
            .FirstOrDefault(estimate => estimate.Viable)
            ?? estimates[0];

        string rationale = BuildRecommendationRationale(workload, recommended, estimates);

        return new CostOptimizationPlan(
            WorkloadSummary: BuildWorkloadSummary(workload),
            MetricsProvenance: workload.MetricsProvenance,
            Estimates: estimates,
            RecommendedTarget: recommended.Target,
            RecommendationRationale: rationale,
            Assumptions: RuntimePricingTable.Assumptions);
    }

    private static TargetCostEstimate Estimate(WorkloadShape workload, RuntimeAdapterCapability capability)
    {
        RuntimePricingFactors factors = RuntimePricingTable.Resolve(capability.Target);
        List<string> notes = [];
        List<RightSizingSuggestion> rightSizing = [];

        decimal monthlyUsd;
        double effectiveUtilization;

        if (factors.Family == "serverless")
        {
            (monthlyUsd, effectiveUtilization) = EstimateServerless(workload, factors, notes, rightSizing);
        }
        else
        {
            (monthlyUsd, effectiveUtilization) = EstimateProvisioned(workload, factors, notes, rightSizing);
        }

        bool viable = IsViable(workload, factors, notes);

        return new TargetCostEstimate(
            Target: capability.Target,
            Family: capability.Family,
            BillingModel: factors.BillingModel,
            EstimatedMonthlyUsd: Math.Round(monthlyUsd, 2),
            RelativeToCheapest: 1.0,
            EffectiveUtilization: Math.Round(effectiveUtilization, 2),
            Viable: viable,
            Notes: notes,
            RightSizing: rightSizing);
    }

    private static (decimal MonthlyUsd, double EffectiveUtilization) EstimateServerless(
        WorkloadShape workload,
        RuntimePricingFactors factors,
        List<string> notes,
        List<RightSizingSuggestion> rightSizing)
    {
        // Serverless bills per request plus per GB-second of busy time only; idle
        // time costs ~0, so the duty cycle is captured implicitly through the
        // request volume rather than provisioned hours.
        decimal monthlyRequests = (decimal)workload.RequestsPerSecond * SecondsPerMonth;
        decimal requestCost = monthlyRequests / 1_000_000m * factors.PerMillionRequests;

        decimal busySecondsPerMonth = monthlyRequests * (decimal)(workload.AvgRequestMillis / 1000.0);
        decimal gbSeconds = busySecondsPerMonth * (decimal)workload.MemoryGib;
        decimal computeCost = gbSeconds * factors.PerGbSecond;

        decimal monthly = requestCost + computeCost + factors.MonthlyPlatformOverhead;

        notes.Add($"Per-request + GB-second billing: ~{monthlyRequests / 1_000_000m:F1}M requests/mo, ~{gbSeconds:F0} GB-seconds/mo.");
        if (workload.DutyCycle < 0.4)
        {
            notes.Add($"Low duty cycle ({workload.DutyCycle:P0}) strongly favors per-request billing; no idle charge.");
        }

        if (workload.MemoryGib > 3.0)
        {
            rightSizing.Add(new RightSizingSuggestion(
                Target: factors.Target,
                Dimension: "memory",
                Suggestion: $"Reduce per-invocation memory below {workload.MemoryGib:F1} GiB if cold-start CPU allows.",
                Rationale: "Serverless compute cost is linear in allocated memory (GB-seconds); right-sizing memory directly cuts spend."));
        }

        return (monthly, 1.0);
    }

    private static (decimal MonthlyUsd, double EffectiveUtilization) EstimateProvisioned(
        WorkloadShape workload,
        RuntimePricingFactors factors,
        List<string> notes,
        List<RightSizingSuggestion> rightSizing)
    {
        // Provisioned families pay for capacity whether or not it is busy. We size
        // for the requested replicas and clamp the effective utilization to the
        // family ceiling so idle waste is visible in the comparison.
        double effectiveUtilization = Math.Min(
            Math.Max(workload.DutyCycle, 0.05),
            factors.SteadyStateUtilizationCeiling);

        decimal replicas = workload.MinReplicas;
        decimal vCpuHours = replicas * (decimal)workload.VCpu * RuntimePricingTable.HoursPerMonth;
        decimal memoryGbHours = replicas * (decimal)workload.MemoryGib * RuntimePricingTable.HoursPerMonth;

        decimal compute = vCpuHours * factors.VCpuHour + memoryGbHours * factors.MemoryGbHour;
        decimal monthly = compute + factors.MonthlyPlatformOverhead;

        notes.Add($"Provisioned {workload.MinReplicas} replica(s) at {workload.VCpu:F2} vCPU / {workload.MemoryGib:F1} GiB each, {RuntimePricingTable.HoursPerMonth:F0}h/mo.");
        if (factors.MonthlyPlatformOverhead > 0m)
        {
            notes.Add($"Includes ~${factors.MonthlyPlatformOverhead:F0}/mo platform overhead (control plane + baseline nodes).");
        }

        if (effectiveUtilization < 0.5)
        {
            notes.Add($"Modeled effective utilization {effectiveUtilization:P0}: provisioned capacity is largely idle for this duty cycle.");
            rightSizing.Add(new RightSizingSuggestion(
                Target: factors.Target,
                Dimension: "scaling",
                Suggestion: "Enable scale-to-low/zero or right-size replica count; consider a serverless or managed-container target for this duty cycle.",
                Rationale: "Always-on capacity bills the same idle or busy; low duty cycle wastes provisioned spend."));
        }

        if (workload.MinReplicas > 2 && workload.RequestsPerSecond < 50)
        {
            rightSizing.Add(new RightSizingSuggestion(
                Target: factors.Target,
                Dimension: "replicas",
                Suggestion: $"Reduce min replicas from {workload.MinReplicas} toward 2 and rely on autoscaling.",
                Rationale: "Request volume does not justify the replica floor; the extra replicas add fixed cost without headroom benefit."));
        }

        return (monthly, effectiveUtilization);
    }

    private static bool IsViable(WorkloadShape workload, RuntimePricingFactors factors, List<string> notes)
    {
        if (factors.Family == "serverless")
        {
            if (workload.RequiresPersistentState)
            {
                notes.Add("Disqualified: workload requires long-lived in-process state, which serverless cannot hold across invocations.");
                return false;
            }

            if (workload.LatencySensitiveSustained && workload.DutyCycle >= 0.7)
            {
                notes.Add("Disqualified for recommendation: sustained latency-sensitive, always-on traffic is exposed to cold starts and per-request economics flip unfavorable.");
                return false;
            }
        }

        return true;
    }

    private static string BuildRecommendationRationale(
        WorkloadShape workload,
        TargetCostEstimate recommended,
        IReadOnlyList<TargetCostEstimate> estimates)
    {
        TargetCostEstimate? runnerUp = estimates
            .Where(estimate => estimate.Viable && !estimate.Target.Equals(recommended.Target, StringComparison.OrdinalIgnoreCase))
            .OrderBy(estimate => estimate.EstimatedMonthlyUsd)
            .FirstOrDefault();

        string shapeNote = workload.DutyCycle < 0.4
            ? "spiky/low-duty traffic"
            : workload.RequiresPersistentState
                ? "stateful, always-on traffic"
                : "sustained traffic";

        string comparison = runnerUp is null
            ? "no viable runner-up to compare against"
            : $"~{recommended.RelativeToCheapest:F2}x vs {runnerUp.Target} at ~{runnerUp.RelativeToCheapest:F2}x (relative to cheapest viable)";

        return $"Recommend `{recommended.Target}` ({recommended.Family}) for {shapeNote}: lowest viable modeled cost at ~${recommended.EstimatedMonthlyUsd:F0}/mo ({comparison}). Directional only — confirm with a real cloud quote.";
    }

    private static string BuildWorkloadSummary(WorkloadShape workload)
    {
        return $"{workload.VCpu:F2} vCPU / {workload.MemoryGib:F1} GiB x{workload.MinReplicas}, " +
            $"{workload.RequestsPerSecond:F1} req/s, {workload.AvgRequestMillis:F0}ms avg, " +
            $"duty {workload.DutyCycle:P0}, stateful={workload.RequiresPersistentState}, sustained={workload.LatencySensitiveSustained}";
    }
}
