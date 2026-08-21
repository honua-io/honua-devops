# Contract Boundaries

This document defines the contract boundaries for `honua-devops` as tracked by `honua-devops#30`.

## Purpose

`honua-devops` is private operator tooling that consumes open standards and
Honua implementation surfaces. It is not a standards body. The
desired-state object model (`honua.io/v1alpha1`) is internal orchestration
and does not replace or shadow upstream contracts.

This boundary exists so that feature work in `honua-devops` does not
silently re-invent semantics that belong to `geospatial-mcp` (interaction
plane) or `geospatial-grpc` (execution plane). Downstream issues should
reference this document instead of re-explaining the repo split.

The Azure-first host path is explicitly a consumer of these open standards,
not a parallel definition of them. When `honua-devops` needs geospatial
interaction or execution semantics, it consumes the upstream contract rather
than building a local equivalent.

## Contract Consumption Matrix

| Surface | Contract Owner | Consumption Pattern | Status | Lockstep |
| --- | --- | --- | --- | --- |
| honua-server REST API | honua-server | BackendGateway HTTP to 13 readiness/admin/metrics/manifest endpoints | Active | — |
| honua-support ticket API and escalation webhook | honua-support | SupportGateway polls/posts ticket diagnosis records; `--listen` accepts signed `ticket.escalation_requested` webhooks | Active | — |
| honua-support bug-report event | honua-support | `--bugreport-listen` accepts the signed, operator-approved `ticket.bug_report.v1` event (HMAC-SHA256 + freshness/replay window + durable bounded `eventId` idempotency), resolves the destination repo ONLY from a server-owned component→repo allowlist, dedupes, and files a sanitized (references-only) GitHub issue | Active | honua-support#44; honua-devops#141 |
| honua-server packaging | honua-server | Docker images, Helm charts, image registries consumed by runtime adapters | Active | — |
| honua-iac modules | honua-iac | Infra plan/apply for 6 targets (azure-functions, lambda, eks, aks, ecs, aca) | Active | — |
| OTEL telemetry | OTEL standard | Log and metrics queries via BackendGateway | Active | — |
| geospatial-grpc services | geospatial-grpc | ProcessService/PipelineService RPCs, typed job/progress/result/artifact/error models, dry-run semantics | Not yet consumed | geospatial-grpc-6 |
| honua-server MCP ops surface | geospatial-mcp + honua-server | Session-aware bounded reads of ops health, findings, alerts, Operate timeline, platform release, and deploy operations; finding-id proposal handoff remains server-owned | Active (ops subset) | geospatial-mcp#57; honua-server#2555/#2566 |

## Private Orchestration Inventory

The following are owned by `honua-devops` as private operator tooling,
cross-referenced to the existing document that defines each:

- Desired-state object model: `docs/desired-state-schemas.md`
- Runtime adapter framework (6 targets): `docs/runtime-adapter-framework.md`
- Release orchestration state machine (8 stages): `docs/release-orchestration-state-machine.md`
- Operator control contract (tiers, evidence): `docs/operator-control-contract.md`
- Operator policy (approval, support, break-glass): `docs/operator-policy-and-delegated-ops.md`
- GitOps engine planning: `docs/honua-gitops-engine.md`
- Console-facing AI DevOps operation bridge (GitOps proposal / operation-status / advisory-brief projections over honua-server deploy-control): `docs/console-ai-devops-bridge.md`
- GitOps proposal contract aligned with the honua-server `OperationProposal` shape for single-surface console aggregation: `docs/gitops-proposal-contract.md`
- ServiceBundle reconciliation: `docs/service-bundle-reconciliation.md`
- Azure operator orchestration host: `docs/azure-operator-orchestration-host.md`
- AI agent prompts and tool definitions
- Customer control-repo starter pack: `desired-state/`

## Future Consumption: geospatial-grpc

Once `geospatial-grpc-6` lands:

- Import typed models (job, progress, result, artifact, error) from upstream protos
- Call ProcessService/PipelineService as a gRPC client
- Use gRPC dry-run and estimation semantics, not local reimplementations
- Route long-running geospatial work through gRPC services rather than REST manifest

Consuming generated gRPC client stubs from upstream protos is consumption,
not redefinition. This section documents intended patterns; details may
shift when the upstream contract closes.
The Azure host planner records these intended gRPC responsibilities as
contract-consumption stages; it does not introduce local client stubs or
replacement service contracts.

## Remaining Consumption: geospatial-mcp

The bounded operator-observability subset is consumed now. Remaining broader
interaction-plane work still tracked by `geospatial-mcp-2` should:

- Invoke MCP tools through the published taxonomy, not a parallel tool registry
- Use MCP resource model for data access
- Respect interaction-plane boundary: MCP for discovery/interaction, gRPC for execution
- Delegate geospatial operations to MCP tool surfaces rather than embedding logic directly

The active ops client consumes the published server tool names and structured
results without redefining them. The Azure host planner records remaining MCP
responsibilities as contract-consumption stages; it does not create a parallel
tool taxonomy.

## Non-Goals

`honua-devops` must not:

1. Redefine MCP tool, resource, or prompt semantics (owned by geospatial-mcp)
2. Redefine gRPC service contracts or proto definitions (owned by geospatial-grpc)
3. Redefine server packaging, image tagging, or distribution (owned by honua-server)
4. Redefine error envelopes or authorization models (owned by honua-server)
5. Introduce direct AI data editing (prohibited by ADR-0028)
6. Publish operator-internal schemas (`honua.io/v1alpha1`) as open standards
7. Build local equivalents of upstream MCP/gRPC semantics before those contracts land

## Lockstep Dependencies

The ops subset of geospatial MCP is active; `geospatial-mcp-2` still tracks the
remaining broader interaction surface. `geospatial-grpc-6` is not yet consumed.
Until those remaining contracts land, this document records intended
consumption patterns without stubs or alternative paths.

When the remaining dependencies land, update the relevant matrix status and
revise the remaining-consumption sections with concrete integration details.
