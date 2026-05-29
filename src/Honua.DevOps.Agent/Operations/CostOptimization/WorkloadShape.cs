namespace Honua.DevOps.Agent.Operations.CostOptimization;

// Normalized description of a workload used to drive the cost comparison.
//
// The free-text/loose inputs from the tool surface are validated and clamped
// into this shape before any pricing math runs, so the planner only ever sees
// sane, bounded numbers. `MetricsProvenance` records where the numbers came
// from (operator-described vs OTEL-derived) for audit honesty.
internal sealed record WorkloadShape(
    // Requested vCPU per running instance/replica (steady-state sizing).
    double VCpu,
    // Requested memory (GiB) per running instance/replica.
    double MemoryGib,
    // Average sustained requests per second across the billing window.
    double RequestsPerSecond,
    // Average request handling time in milliseconds (drives serverless GB-seconds).
    double AvgRequestMillis,
    // Fraction (0..1) of the billing window the workload actually serves traffic.
    // 1.0 = always-on; low values = spiky/event-driven.
    double DutyCycle,
    // Desired number of always-on replicas for the provisioned families.
    int MinReplicas,
    // Whether the workload must hold long-lived state/connections in-process,
    // which disqualifies a pure-serverless recommendation.
    bool RequiresPersistentState,
    // Whether the workload needs sustained throughput (vs. bursty), which favors
    // provisioned capacity over per-request billing.
    bool LatencySensitiveSustained,
    string MetricsProvenance);
