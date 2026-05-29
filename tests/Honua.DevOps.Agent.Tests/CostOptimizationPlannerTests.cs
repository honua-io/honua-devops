using Honua.DevOps.Agent.Operations.CostOptimization;

namespace Honua.DevOps.Agent.Tests;

public sealed class CostOptimizationPlannerTests
{
    private static readonly string[] AllTargets =
        ["azure-functions", "lambda", "eks", "aks", "ecs", "aca"];

    // Fixture 1: spiky, stateless, event-driven API. Low duty cycle, modest
    // request volume, no persistent state. Serverless should win.
    private static WorkloadShape SpikyStatelessApi() => new(
        VCpu: 0.5,
        MemoryGib: 0.5,
        RequestsPerSecond: 5,
        AvgRequestMillis: 80,
        DutyCycle: 0.15,
        MinReplicas: 2,
        RequiresPersistentState: false,
        LatencySensitiveSustained: false,
        MetricsProvenance: "test-fixture: spiky stateless api");

    // Fixture 2: sustained, stateful, high-throughput tile/geospatial service.
    // Always-on, holds connection pools and in-process caches. A provisioned
    // container/k8s target should win; serverless must be disqualified.
    private static WorkloadShape SustainedStatefulTileService() => new(
        VCpu: 4,
        MemoryGib: 16,
        RequestsPerSecond: 800,
        AvgRequestMillis: 40,
        DutyCycle: 0.95,
        MinReplicas: 4,
        RequiresPersistentState: true,
        LatencySensitiveSustained: true,
        MetricsProvenance: "test-fixture: sustained stateful tile service");

    [Fact]
    public void Build_SpikyStatelessWorkload_RecommendsServerless()
    {
        CostOptimizationPlan plan = CostOptimizationPlanner.Build(SpikyStatelessApi(), AllTargets);

        TargetCostEstimate recommended = plan.Estimates
            .Single(estimate => estimate.Target == plan.RecommendedTarget);

        Assert.Equal("serverless", recommended.Family);
        Assert.True(recommended.Viable);
        Assert.Contains(plan.RecommendedTarget, new[] { "azure-functions", "lambda" });

        // Cheapest viable should be the recommendation, and it should be the front
        // of the ordered list.
        Assert.Equal(plan.Estimates.First(e => e.Viable).Target, plan.RecommendedTarget);

        // Provisioned k8s should be visibly more expensive for this low-duty shape.
        TargetCostEstimate eks = plan.Estimates.Single(e => e.Target == "eks");
        Assert.True(eks.EstimatedMonthlyUsd > recommended.EstimatedMonthlyUsd);
        Assert.True(eks.RelativeToCheapest > 1.0);
    }

    [Fact]
    public void Build_SustainedStatefulWorkload_DisqualifiesServerlessAndRecommendsProvisioned()
    {
        CostOptimizationPlan plan = CostOptimizationPlanner.Build(SustainedStatefulTileService(), AllTargets);

        // Both serverless targets must be marked non-viable for a stateful, always-on workload.
        Assert.False(plan.Estimates.Single(e => e.Target == "azure-functions").Viable);
        Assert.False(plan.Estimates.Single(e => e.Target == "lambda").Viable);

        TargetCostEstimate recommended = plan.Estimates.Single(e => e.Target == plan.RecommendedTarget);
        Assert.True(recommended.Viable);
        Assert.Contains(recommended.Family, new[] { "kubernetes", "managed-container" });
        Assert.DoesNotContain(plan.RecommendedTarget, new[] { "azure-functions", "lambda" });
    }

    [Fact]
    public void Build_AlwaysReturnsPricingAssumptionsProvenance()
    {
        CostOptimizationPlan plan = CostOptimizationPlanner.Build(SpikyStatelessApi(), AllTargets);

        Assert.NotEmpty(plan.Assumptions);
        Assert.Same(RuntimePricingTable.Assumptions, plan.Assumptions);
        Assert.Contains(plan.Assumptions, a => a.Contains("STATIC", StringComparison.Ordinal));
        Assert.Contains(plan.Assumptions, a => a.Contains("NOT a live quote", StringComparison.Ordinal));
        Assert.Equal("test-fixture: spiky stateless api", plan.MetricsProvenance);
    }

    [Fact]
    public void Build_RelativeCostIsAnchoredToCheapestViable()
    {
        CostOptimizationPlan plan = CostOptimizationPlanner.Build(SustainedStatefulTileService(), AllTargets);

        TargetCostEstimate cheapestViable = plan.Estimates
            .Where(e => e.Viable)
            .OrderBy(e => e.EstimatedMonthlyUsd)
            .First();

        Assert.Equal(1.0, cheapestViable.RelativeToCheapest, precision: 2);
        Assert.All(
            plan.Estimates.Where(e => e.Viable),
            e => Assert.True(e.RelativeToCheapest >= 1.0));
    }

    [Fact]
    public void Build_EmitsRightSizingForIdleProvisionedCapacity()
    {
        CostOptimizationPlan plan = CostOptimizationPlanner.Build(SpikyStatelessApi(), AllTargets);

        // The low-duty workload on always-on k8s should surface a scaling right-size hint.
        IReadOnlyList<RightSizingSuggestion> eksHints = plan.Estimates
            .Single(e => e.Target == "eks")
            .RightSizing;

        Assert.Contains(eksHints, hint => hint.Dimension == "scaling");
    }

    [Fact]
    public void Build_RejectsEmptyTargetList()
    {
        Assert.Throws<InvalidOperationException>(
            () => CostOptimizationPlanner.Build(SpikyStatelessApi(), []));
    }

    [Fact]
    public void Build_RejectsUnsupportedTarget()
    {
        Assert.Throws<InvalidOperationException>(
            () => CostOptimizationPlanner.Build(SpikyStatelessApi(), ["nomad"]));
    }
}
