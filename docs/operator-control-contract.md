# Operator Control Contract

This document defines the first public control contract for `honua-devops` as the product-grade Honua operator surface tracked by `honua-devops#12`.

## Purpose

`honua-devops` exists to turn Honua deployment and operations into an explicit, auditable control system rather than an internal-only automation script collection.

The contract has four goals:

- separate read-only guidance from gated write execution
- make trust boundaries explicit across AI planning, runtime adapters, and control-plane backends
- define the minimum evidence captured for every plan or execution
- stabilize the command and object model before the full `honua-gitops` engine lands

## Personas

Supported operator personas:

- Customer platform engineer: owns deployment topology, infra boundaries, and environment promotion.
- GIS operator: owns service publishing, runtime health, and operational readiness.
- Implementation partner: executes onboarding, migration, and controlled rollout work for customers.
- Scoped Honua support: assists with diagnostics and recovery under explicit customer-approved boundaries.

## Trust Boundaries

The operator surface is intentionally split into distinct control zones:

- AI planner: reasons about intent, explains actions, and produces plans or proposals. It does not become the source of truth.
- Desired state: Git commits and typed operator objects are the source of desired intent.
- Runtime adapters: translate desired state into Terraform, Helm, or control-plane operations for each target family.
- Honua control plane: reconciles GIS and service state through Honua APIs.
- Telemetry truth: OTEL and Honua metrics provide observed health, drift, and release evidence.
- Approval layer: humans or policy gates decide whether proposed state can advance to execution.

## Execution Model

Two knobs govern behavior:

- `HONUA_DEVOPS_EXECUTION_MODE`: `plan` or `execute`
- `HONUA_DEVOPS_EXECUTION_TIER`: `observe`, `plan`, `propose`, `execute-lower-env`, `promote-prod`, or `break-glass`

Execution mode answers "may this process write at all?"

Execution tier answers "what class of write is allowed, and under what guardrails?"

### Execution Tiers

| Tier | Write posture | Intended use |
| --- | --- | --- |
| `observe` | no writes | incident review, evidence gathering, health checks |
| `plan` | no writes | dry-run diff, rollout planning, operator rehearsal |
| `propose` | no writes | prepare desired-state payloads and PR-ready changes |
| `execute-lower-env` | gated writes to non-prod only | dev and staging rollout execution |
| `promote-prod` | gated prod promotion only | promote a previously validated revision into prod |
| `break-glass` | emergency direct execution | incident recovery with explicit audit burden |

Default posture:

- `plan` mode defaults to tier `plan`
- `execute` mode defaults to tier `execute-lower-env`

That keeps the first write-capable default out of production until the operator explicitly opts into prod promotion or break-glass behavior.

## Command Semantics

The current CLI and tool surface is intentionally narrow:

- diagnostic tools (`analyze_logs`, `analyze_metrics`, `troubleshoot_incident`, `tune_performance`) are read-mostly
- rollout-oriented tools (`plan_server_upgrade`, `deploy_service_gitops`) must always surface an effective dry-run/write decision
- write intent must record both requested action and effective action

Current deployment actions:

- `plan`
- `dry-run`
- `sync`
- `apply`
- `prune`
- `promote`

Contract rules:

- `observe`, `plan`, and `propose` always resolve to dry-run behavior even if the caller requests `apply`
- `execute-lower-env` rejects any `prod` target
- `promote-prod` requires action `promote` when `prod` is targeted
- `break-glass` allows direct execution but must produce elevated-risk evidence

## Evidence Model

Every operator plan or execution should emit enough evidence to replay intent and evaluate safety.

Minimum evidence fields:

- requested action
- effective action
- execution mode
- execution tier
- dry-run flag
- desired revision
- target environments
- GitOps tool
- Terraform source reference
- policy gate outcome
- backend endpoint summary
- validation requirements
- observed warnings or residual risk

Evidence requirements by tier:

- read-only tiers must capture desired scope, diff summary, and gating requirements
- write tiers must additionally capture execution target, approval context, and rollback path
- `break-glass` must also capture operator justification and incident context

## Default Workflow

Normal path:

1. Gather telemetry and runtime facts.
2. Produce a plan or proposal in a read-only tier.
3. Validate lower environments with `execute-lower-env`.
4. Promote to production with `promote-prod`.
5. Reserve `break-glass` for exceptional recovery work.

## Current Bootstrap Mapping

The repo is still in the bootstrap phase:

- the runtime already supports plan-only and execute flows
- the deploy path now resolves effective write behavior from execution tier, not from prompt text alone
- the desired-state object model is being introduced before full multi-object reconciliation is available in `honua-server`

That means the contract is ahead of the full backend implementation by design. The operator can stabilize semantics and evidence now while the deeper `honua-gitops` engine and runtime adapters continue to land.
