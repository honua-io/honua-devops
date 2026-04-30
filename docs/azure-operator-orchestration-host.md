# Azure Operator Orchestration Host

This document defines the first `honua-devops#29` host slice: an Azure-first
orchestration host for Honua AI operator workflows.

The host is private orchestration. It consumes the open interaction and
execution contracts instead of redefining them.

## Host Boundary

The Azure host owns:

- Microsoft Agent Framework session and tool-call hosting
- Azure trace correlation across agent turns, MCP calls, gRPC jobs, and Honua deployment state
- operator policy, approval, evidence, and audit envelopes
- runtime selection for Azure-oriented deployment targets such as `azure-functions`, `aks`, and `aca`
- evaluation metadata that lets model lanes compare plans against deterministic outcomes

The host does not own:

- MCP tool, resource, prompt, planning, or elicitation semantics
- gRPC service, job, result, artifact, or error contracts
- honua-server package, route, URL, revision, runtime-config, or publication-state behavior
- source-data editing; AI-driven source-data edits remain out of scope

## Consumed Surfaces

| Surface | Owner | Host consumption |
| --- | --- | --- |
| MCP interaction plane | `geospatial-mcp` | Tools, resources, prompts, planning, and elicitation for analyze/publish/build/deploy workflows |
| gRPC execution plane | `geospatial-grpc` | Dry-run, estimate, job progress, result, artifact, render, builder, and deployment services |
| Deterministic runtime | `honua-server` | Validation, package lifecycle, deployment state, route/URL/revision/runtime-config/publication-state |
| Private host envelope | `honua-devops` | Agent Framework host, Azure tracing, policy, approvals, evidence, and eval metadata |

## Workflow Families

The first host planner supports four workflow families:

- `analyze`: intent capture through ProcessService execution and MapPackage result packaging
- `publish`: publishing pipeline planning, execution, and publication/deployment state handoff
- `build`: map/app package composition with JS-first runtime compatibility checks
- `deploy`: deployment lifecycle planning and hosted surface publication checks

All families use the deterministic stage skeleton from the upstream operator
docs:

1. Capture intent
2. Ground candidates
3. Clarify
4. Compile plan
5. Validate plan
6. Dry run or estimate
7. Execute
8. Compose map and/or app where applicable
9. Publish where applicable
10. Return result package

## Microsoft Agent Framework Integration

The console agent already hosts tools through Microsoft Agent Framework. The
`plan_azure_operator_workflow` tool is the first orchestration-host planning
tool. It emits:

- a typed `OrchestrationHostPlan`
- deterministic stage responsibilities
- required checks per stage
- Azure integration points
- boundary rules preventing local MCP/gRPC/server semantic drift
- an `OperationEvidence` envelope marked as dry-run because the current slice
  plans the host path rather than calling concrete MCP or gRPC clients

Future implementation should replace stage placeholders with concrete clients
only after the relevant upstream contract package is available locally.

## Smoke And Eval Path

The host plan intentionally records eval hooks before live execution exists:

- stage status for clarification quality, plan validity, execution success, and result correctness
- package usefulness for MapPackage/AppPackage outputs
- publication/deployment usefulness for hosted surfaces
- trace metadata that connects model proposals to deterministic validation outcomes

This is the handoff point for the later multi-model eval runner in
`honua-devops#31`. Claude, Codex, and local portability lanes should consume the
same deterministic stage envelope rather than model-specific scenarios.

## Current Status

Implemented in this repo:

- typed host planner in `src/Honua.DevOps.Agent/Operations/OrchestrationHost/`
- callable `plan_azure_operator_workflow` tool
- evidence output through `OperationResponse.OrchestrationHost`
- tests covering stage order, approval-required deployment gates, evidence, and invalid workflow family handling

Not yet implemented:

- concrete MCP client invocation
- concrete gRPC generated client invocation
- Azure deployment of a long-running host service
- model-matrix eval execution
