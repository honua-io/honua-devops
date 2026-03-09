# Release Orchestration State Machine

This document captures the target-independent release state machine for `honua-devops#17`.

## Goal

The operator should treat promotion and rollback as an explicit staged workflow rather than as a single deploy command.

The state machine is intentionally target-independent:

1. `preflight`
2. `backup`
3. `migration`
4. `rollout`
5. `smoke`
6. `slo-watch`
7. `promote`
8. `rollback`

## Stage Semantics

### `preflight`

- validate desired-vs-actual scope
- confirm runtime adapter fit
- classify rollback scope before execution

### `backup`

- capture the recovery checkpoint for app, config, and schema
- record the known-good revision before traffic moves

### `migration`

- run migrations explicitly
- prefer out-of-band migration steps when the target family requires it
- never rely on hidden startup migration behavior for managed rollout safety

### `rollout`

- apply adapter-specific release steps
- stop on failed gates instead of blindly continuing

### `smoke`

- run the shared smoke contract after rollout
- do not promote if smoke fails

### `slo-watch`

- hold the release long enough to observe SLO gate behavior
- prefer the watch path over a single one-shot threshold check for canary promotion
- require SLO evidence before promotion

### `promote`

- advance only after lower-environment evidence is captured
- treat promotion as gated, not automatic

### `rollback`

- trigger on failed smoke, failed SLO gate, or explicit operator stop
- classify rollback separately for:
  - infrastructure
  - app release
  - service config
  - schema

## Migration Modes

Two baseline migration modes exist:

- `out-of-band-migration`
- `compatibility-reviewed-migration`

Serverless targets default toward out-of-band migration because rollout and traffic shifting should not depend on startup migration side effects.

## Promotion Modes

Two baseline promotion modes exist:

- `single-environment-rollout`
- `gated-promotion`

Any multi-environment flow or production-targeted flow should use gated promotion.

## Evidence Requirements

Every orchestration plan should collect evidence for:

- manifest diff
- target validation
- rollback classification
- backup checkpoint
- migration plan
- release evidence
- smoke contract
- SLO gate evidence
- approval record when promotion is gated
- rollback evidence

## Typed Promotion And Rollback Policy

The current planner now emits explicit typed policy in addition to the stage list:

- promotion gate name
- promotion blockers
- promotion sequence per environment
- rollback trigger set
- rollback fallback mode
- rollback semantics per change class

Rollback semantics are now captured separately for:

- infrastructure
- app release
- service config
- schema

## Current Implementation

The current repo implementation emits this state machine directly in upgrade and GitOps planning responses.

That means:

- stages are visible to the operator before execution
- required evidence is folded into the operation evidence bundle
- the `slo-watch` stage now points at `scripts/slo-release-watch.sh` with explicit rollback command wiring
- rollback classes are explicit before promotion begins
- promotion sequence is explicit before multi-environment rollout begins
- rollback recovery paths are typed per change class rather than implied by stage names alone
