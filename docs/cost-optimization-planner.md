# Multi-Cloud Cost Optimization Planner

`plan_cost_optimization` is a **read-only planning tool** that compares the six
supported runtime targets for a described workload shape and recommends the
lowest-cost *viable* target. It never calls a backend, never mutates state, and
is not edition-gated (it sits with the other read-only architecture tools such
as `recommend_deployment_topology` and `analyze_customer_requirements`).

It emits a typed `CostOptimizationPlan` carried on `OperationResponse`
(`[JsonIgnore]`'d from the LLM wire shape, like `GitOpsPlan` and
`ReleaseOrchestration`) so the full structured comparison lands in the audit
journal while the model sees only the compact findings/actions.

## What it produces

- `EstimatedMonthlyUsd` and `RelativeToCheapest` per target.
- Per-target right-sizing suggestions (memory, replicas, scaling).
- A single `RecommendedTarget` plus rationale.
- The full `Assumptions` provenance list (always populated).

## Inputs (workload shape)

| Input | Meaning |
| --- | --- |
| `vCpu`, `memoryGib` | Per-replica steady-state sizing. |
| `requestsPerSecond` | Average sustained RPS over the billing window. |
| `avgRequestMillis` | Average request handling time (drives serverless GB-seconds). |
| `dutyCycle` | Fraction (0–1) of the window actually serving traffic. |
| `minReplicas` | Always-on replica floor for provisioned families. |
| `requiresPersistentState` | Disqualifies pure-serverless recommendation. |
| `latencySensitiveSustained` | Sustained, latency-sensitive traffic favors provisioned. |
| `metricsSource` | Provenance string, e.g. `OTEL p50 over 7d` vs `operator estimate`. Prefer OTEL-derived metrics when the telemetry integration exposes them. |

All numeric inputs are clamped to sane bounds before any pricing math runs.

## Cost model

- **Serverless** (`azure-functions`, `lambda`): per-request + per-GB-second of
  busy time; idle time assumed ~0. Disqualified when the workload requires
  persistent in-process state, or is sustained + latency-sensitive + always-on.
- **Managed container** (`ecs`, `aca`): provisioned vCPU-hour + memory-GB-hour,
  modeled at a ~0.75 steady-state utilization ceiling.
- **Kubernetes** (`aks`, `eks`): node vCPU-hour + memory-GB-hour, plus a fixed
  monthly control-plane + baseline-node overhead, modeled at a ~0.6 ceiling.

The recommendation is always the cheapest **viable** target. Disqualified
targets are still costed and shown, but never recommended.

## Pricing assumptions (provenance)

The figures in `RuntimePricingTable` are **static, approximate, US list-price
derived** values for a single primary region, on-demand. They are **not a live
quote** and must never be used for billing or contractual commitments. They
exist only to compare *relative* cost between targets.

Not modeled: reserved capacity / savings plans / spot / committed-use discounts,
egress, storage, data transfer. 1 month = 730 hours. Memory defaults to ~2x the
requested vCPU when not supplied.

These numbers are refreshed **by hand**, not fetched. To update them, edit
`RuntimePricingFactors` in
`src/Honua.DevOps.Agent/Operations/CostOptimization/RuntimePricingTable.cs`
against current cloud rate cards, and update the `Assumptions` list if the model
shape changes. For procurement decisions, override with a real cloud quote.

## Implementation

- `Operations/CostOptimization/RuntimePricingTable.cs` — static factors + assumptions.
- `Operations/CostOptimization/WorkloadShape.cs` — normalized input shape.
- `Operations/CostOptimization/CostOptimizationPlan.cs` — typed result records.
- `Operations/CostOptimization/CostOptimizationPlanner.cs` — pure comparison logic.
- Wired in `Operations/CapabilityToolset.cs` as `plan_cost_optimization`.
- Carried on `OperationResponse.CostOptimization` (`[JsonIgnore]`).
- Tests: `tests/.../CostOptimizationPlannerTests.cs` and
  `tests/.../HonuaOperationsToolkitCostPlannerTests.cs`.
