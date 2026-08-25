# honua-gitops Engine

This document captures the first in-repo `honua-gitops` engine slice for `honua-devops#14`.

## Goal

`honua-devops` should stop treating GitOps as a loose list of suggested commands and instead emit a typed internal engine plan that the AI runtime can reason about directly.

The first slice is plan-first:

- model the core command/state transitions
- capture desired vs actual revision state per environment
- distinguish infra, release, and service-state drift
- expose gate status and required evidence before any write-capable path runs

## Supported Operations

The current engine plan emits the baseline operation set:

- `plan`
- `diff`
- `sync`
- `status`
- `drift`
- `pause`
- `resume`
- `approve`
- `promote`
- `rollback`

## Current Plan Model

Deploy responses now include a typed GitOps plan with:

- engine name
- requested action and effective action
- actual-state source
- diff summary
- drift summary
- overall gate status
- required evidence
- per-environment state
- typed state transitions

Per environment, the plan carries:

- desired revision
- actual revision
- diff status
- gate status
- drift buckets for `infra`, `release`, and `service-state`
- typed commands for the supported operations

## State Machine Contract

Each transition now exposes the explicit state boundary the operator is crossing:

- `fromState`
- `toState`
- whether the transition mutates customer state
- whether approval evidence is required
- the required checks that must be attached before execution

The baseline path is:

`desired-revision -> planned -> diff-reviewed -> applied -> status-read -> drift-checked`

Promotion and recovery paths extend that contract with:

- `approval-requested -> approved -> promoted`
- `reconciling -> paused -> reconciling`
- `applied -> rolled-back`

Plan-only responses keep the same transition shape, but mark mutating operations as non-mutating and render `sync` as `diff-reviewed -> sync-preview`. That lets agents reason over the same contract before and after write access is enabled.

## Actual-State Read Path

The current implementation uses Honua manifest export as the first actual-state source.

That means:

- actual revision is read from exported manifest state when present
- missing or incomplete export data is surfaced explicitly as pending actual-state evidence
- service-state drift points back to the typed `ServiceBundle` reconciliation model rather than collapsing into a generic manifest diff

## Current Limitations

This is intentionally the first engine slice, not the finished execution subsystem.

Current gaps, each with its tracking status as of 2026-08-24. "UNTRACKED" means
no open issue covers it — do not read it as planned work.

- **No standalone CLI entrypoint for `honua-gitops`.** UNTRACKED. The originating
  epic honua-devops#14 closed without one; the engine is reachable only as agent
  function tools or over MCP (`--mcp`). honua-devops#148 packages a runnable
  `--mcp` artifact but does not add a `honua-gitops` CLI.
- **No persistent reconciliation loop.** UNTRACKED as a honua-devops feature. The
  current architecture puts reconcile state on the server side (see
  honua-devops#57, which spans PR, CI, reconcile, and rollback under one
  server-owned operation id), so a devops-resident loop may never be built. If
  that decision is intentional, this line should be retired rather than tracked.
- **No pause/resume/approve backend implementation.** UNTRACKED. Approval today
  is the `pr-first` / execution-tier gate plus the Console proposal bridge
  (`create_gitops_proposal` / `record_gitops_proposal_decision`); there is no
  pause/resume state machine and no open issue for one.
- **Diff and drift are evidence/planning-first, not full actuation.** Partially
  tracked. The write seam landed in honua-devops#151/#153; the actuator
  vocabulary that would make drift remediation real is honua-devops#156
  (`ActuatorRegistry.Remediations` currently holds exactly two entries:
  `gitops-rollback`, which is gated off unless rollback is explicitly enabled,
  and the read-only `drift-observe`). Turning a GitOps *diff* into an
  applied change through a PR is honua-devops#57. No open issue covers
  autonomous drift correction, and none is intended in the default posture.

## What Landed

The current repo implementation now:

- exposes a dedicated `plan_gitops_engine` tool for snapshot-only engine planning
- emits typed GitOps plan state from `deploy_service_gitops`
- folds GitOps required evidence into the shared operation evidence bundle
- exposes explicit from/to GitOps state transitions with mutation and approval flags
- ties promotion/rollback semantics to release orchestration policy
- ties service-state drift to `ServiceBundle` export/reconciliation semantics

That is enough to make `#14` real in code and to support the next execution-focused slice without redesigning the response contract again.

## Related: Console-Facing Projection

The Console-facing projection over GitOps proposals — stable operation IDs, raw evidence references, unified operation status, and advisory briefs surfaced to Honua Console without scraping Git or CI — is a separate bounded layer that reuses `deploy_service_gitops` validation and the deploy-control operation model. It is documented in `docs/console-ai-devops-bridge.md` (`honua-devops#59`).
