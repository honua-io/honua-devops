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
| honua-server packaging | honua-server | Docker images, Helm charts, image registries consumed by runtime adapters | Active | — |
| honua-terraform modules | honua-terraform | Infra plan/apply for 6 targets (azure-functions, lambda, eks, aks, ecs, aca) | Active | — |
| OTEL telemetry | OTEL standard | Log and metrics queries via BackendGateway | Active | — |
| geospatial-grpc services | geospatial-grpc | ProcessService/PipelineService RPCs, typed job/progress/result/artifact/error models, dry-run semantics | Not yet consumed | geospatial-grpc-6 |
| geospatial-mcp tool surface | geospatial-mcp | MCP tool taxonomy (Analyze, Publish, Build, Automate/Deploy), resource model, interaction-plane boundaries | Not yet consumed | geospatial-mcp-2 |

## Private Orchestration Inventory

The following are owned by `honua-devops` as private operator tooling,
cross-referenced to the existing document that defines each:

- Desired-state object model: `docs/desired-state-schemas.md`
- Runtime adapter framework (6 targets): `docs/runtime-adapter-framework.md`
- Release orchestration state machine (8 stages): `docs/release-orchestration-state-machine.md`
- Operator control contract (tiers, evidence): `docs/operator-control-contract.md`
- Operator policy (approval, support, break-glass): `docs/operator-policy-and-delegated-ops.md`
- GitOps engine planning: `docs/honua-gitops-engine.md`
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

## Future Consumption: geospatial-mcp

Once `geospatial-mcp-2` lands:

- Invoke MCP tools through the published taxonomy, not a parallel tool registry
- Use MCP resource model for data access
- Respect interaction-plane boundary: MCP for discovery/interaction, gRPC for execution
- Delegate geospatial operations to MCP tool surfaces rather than embedding logic directly

This section documents intended patterns; details may shift when the
upstream contract closes.
The Azure host planner records these intended MCP responsibilities as
contract-consumption stages; it does not create a parallel tool taxonomy.

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

`geospatial-mcp-2` and `geospatial-grpc-6` are active contracts not yet
delivered. Until they land, this document records intended consumption
patterns without stubs or alternative paths.

When the dependencies land, update the matrix rows from "Not yet consumed"
to "Active" and revise the future-consumption sections with concrete
integration details.
