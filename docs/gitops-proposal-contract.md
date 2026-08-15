# GitOps Proposal Contract (honua-server `OperationProposal` Alignment)

Tracked by `honua-devops#78` (tracks alongside `honua-server#1690`/`#1692`/`#1694` and
`honua-console#193`). This document defines how the `honua-devops` console-bridge GitOps
proposal contract aligns with the `honua-server` `OperationProposal` shape and approval API,
so `honua-console` can aggregate **server-owned** (admin/deploy/metadata/seed) and
**devops-owned** (gitops/infra) proposals on **one approval surface** without per-source
forks.

## Ownership Split

Per the locked cross-repo split:

- **honua-server** owns admin/deploy/metadata/seed proposals and the durable proposal
  store + approval API (`honua-server#1692`/`#1694`).
- **honua-devops** owns gitops/infra proposals via `create_gitops_proposal`,
  `get_gitops_proposal`, `get_devops_operation_status`, and
  `record_gitops_proposal_decision`.
- **honua-console** aggregates both into one approval/timeline surface
  (`honua-console#193`).

For aggregation to work, the devops proposal contract must align field-for-field (or via the
documented adapter below) with the server `OperationProposal` shape, the
`WorkflowOperationStatus`-derived lifecycle, and the decision audit.

## Source Of Truth

The server contract this bridge aligns to lives at
`honua-server/src/Honua.Core/Features/ControlPlane/Domain/OperationModels.cs`:

- `WorkflowOperationStatus` (enum) — the proposal/operation lifecycle states.
- `OperationAuditInfo` (`RequestedBy`, `Reason`, `IdempotencyKey`, `CorrelationId`, …) —
  the decision/audit fields.
- `DeployPlan` (`RequiresApproval`, `BlockingReasons`, `Warnings`) — the plan posture.
- Deploy-control approval API: `POST /deploy/operations/{id}/submit` and
  `POST /deploy/operations/{id}/rollback`, both carrying a `reason`
  (`SubmitDeployOperationRequest` / `RollbackDeployOperationRequest`).

> Adapter note: at the time of writing, `honua-server#1692`'s dedicated `OperationProposal`
> projection type is still open and not yet present in the server repo. This bridge therefore
> aligns to the **contract as described in the issue** plus the **real, landed** server types
> above (`WorkflowOperationStatus`, `OperationAuditInfo`, `DeployPlan`, the submit/rollback
> reason fields). When the server publishes a typed `OperationProposal`, the mapping table
> below is the single place to reconcile names; the bridge field set was chosen to be a
> superset-compatible projection so no per-source fork is needed.

## Field-For-Field Mapping (devops ↔ server)

The devops projection is `GitOpsProposalBridge`
(`src/Honua.DevOps.Agent/Operations/ConsoleBridge/ConsoleBridgeContracts.cs`). The leading
fields are the bridge-local projection the console already consumes; the trailing fields are
the canonical `OperationProposal` fields added for `#78`.

| Issue contract field | devops `GitOpsProposalBridge` field | server `OperationProposal` source | Notes |
| --- | --- | --- | --- |
| proposal id | `ProposalId` | proposal/operation id | Stable; equals the deploy-control `operationId` when one exists, else the scope-derived idempotency key. |
| operation id | `OperationId` | `WorkflowOperationRecord.OperationId` | Null until a durable server operation is recorded (blocked projections). |
| kind | `Kind` | `WorkflowOperationKind` | Always `gitops-deploy` for this bridge. |
| requester | `Requester` | `OperationAuditInfo.RequestedBy` | Mirrors `Owner` for this bridge. |
| agent | `Agent` | agent identity | Constant `honua-devops`. |
| status (lifecycle) | `ProposalStatus` | `WorkflowOperationStatus` | Canonical lifecycle value, 1:1 with the server enum (see lifecycle table). |
| status (projection) | `Status` | — | Bridge-local (`proposed` / `target-unconfigured` / `contract-unavailable`); retained for back-compat, **not** the lifecycle. |
| plan: diff | `Plan.DiffSummary` | — | Human-readable `{action} {service} -> {envs} @ {revision}`. |
| plan: dry-run | `Plan.DryRun` | `submitImmediately=false` | Always `true`: proposals record but never execute. |
| plan: requires approval | `Plan.RequiresApproval` / `ApprovalRequired` | `DeployPlan.RequiresApproval` / `DeployTargetDefinition.RequiresApproval` | Prod-targeting or non-direct-allowed policy ⇒ approval required. |
| plan: risk | `Plan.Risk` | — | Derived: `high` (prod + destructive), `elevated` (prod or destructive), else `standard`. |
| plan: blocking reasons | `Plan.BlockingReasons` | `DeployPlan.BlockingReasons` / `WorkflowOperationRecord.BlockingReasons` | Non-empty only on a blocked projection. |
| plan: warnings | `Plan.Warnings` | `DeployPlan.Warnings` | E.g. prod-approval advisory. |
| idempotency key | `IdempotencyKey` | `OperationAuditInfo.IdempotencyKey` | Scope-derived, never prose. |
| target/service/envs/revision | `Service`, `TargetEnvironments`, `DesiredRevision`, `CurrentRevision` | `DeployOperationSpec.*` | Read back from the deploy-control `target` object. |
| requested/effective action | `RequestedAction`, `EffectiveAction` | — | `EffectiveAction` is always `propose`. |
| approve/reject decision | `Decision` (`ProposalDecision`) | decision audit (`OperationAuditInfo` + submit/rollback `reason`) | Null until a decision is recorded; see decision-audit section. |
| evidence / links / actions | `Evidence`, `WorkflowLinks`, `SuggestedActions` | evidence + governed action links | Raw refs back to the deploy-control record; never scraped. |
| timestamps | `CreatedAt`, `UpdatedAt` | `CreatedAt` / `UpdatedAt` | ISO-8601 UTC. |

## Status Lifecycle Mapping (`ProposalLifecycle` ↔ `WorkflowOperationStatus`)

The issue spine is `Planned → AwaitingApproval → Submitted → Succeeded/Failed/Rejected`. The
canonical `ProposalLifecycle` values map **1:1** onto the server `WorkflowOperationStatus`
enum (mapper: `ConsoleOperationBridge.MapProposalLifecycle`, case- and hyphen-insensitive):

| server `WorkflowOperationStatus` | canonical `ProposalStatus` | On the issue spine |
| --- | --- | --- |
| `Planned` | `Planned` | spine entry |
| `AwaitingApproval` | `AwaitingApproval` | spine |
| `Submitted` | `Submitted` | spine |
| `Reconciling` | `Reconciling` | (post-submit execution detail) |
| `Succeeded` | `Succeeded` | spine terminal |
| `Failed` | `Failed` | spine terminal |
| `RollbackRequested` | `RollbackRequested` | (rollback detail) |
| `RolledBack` | `RolledBack` | (rollback terminal) |
| `ManualInterventionRequired` | `ManualInterventionRequired` | (blocked) |
| — (rejected approval) | `Rejected` | spine terminal — **decision-audit state**, see below |
| unreadable | `Planned` (or `AwaitingApproval` when approval required) | safe default; never invents a server state |

Notes:

- `Rejected` has **no distinct server enum member**. It is produced **only** by a recorded
  `reject` decision via `record_gitops_proposal_decision`; reading a server status never
  yields `Rejected`. A rejected proposal sits logically atop `AwaitingApproval` (the approval
  was declined and nothing executed). When the server publishes a typed rejected/declined
  approval state, map it onto `Rejected` here.
- The unreadable fallback never fabricates a server lifecycle state: a freshly recorded
  proposal with no server-advanced status reports `AwaitingApproval` when approval is
  required, otherwise `Planned`, so it still enters the lifecycle.

## Approve/Reject Decision Audit

`record_gitops_proposal_decision(operationId, decision, actor, reason)` records an auditable
decision and returns the proposal projection with a populated `Decision`
(`ProposalDecision`):

- `Decision` — normalized `approve` or `reject` (unknown verbs are rejected).
- `Actor` — the deciding operator/principal (server `OperationAuditInfo.RequestedBy`).
- `Reason` — free-form decision reason (server submit/rollback request `reason` /
  `OperationAuditInfo.Reason`), redaction-scrubbed.
- `DecidedAt` — ISO-8601 UTC decision timestamp.
- `ResultingStatus` — the canonical lifecycle the decision moves toward: `Submitted` for
  approve, `Rejected` for reject (`unknown` if the operation cannot be read).
- `GovernedAction` — the governed deploy-control verb the decision **authorizes**: `submit`
  for approve, `none` for reject.

Default-safe posture (unchanged):

- The bridge **records the decision only**. It never calls `submit`, `rollback`, or manifest
  apply. An `approve` surfaces the governed `submit` suggestion (with
  `requiresApproval=true`, `mutatesState=true`); a `reject` surfaces no mutating action.
- `create_gitops_proposal` still records with `submitImmediately=false` and returns a blocked
  `target-unconfigured` projection unless `HONUA_DEVOPS_DEPLOY_TARGET_ID` is set.
- Decisions are emitted on the standard `OperationResponse` audit path, so each call writes
  one audit record (the read step is non-mutating).

## Console Aggregation Guarantee

Because the devops `GitOpsProposalBridge` carries the canonical proposal id / kind /
requester / agent / lifecycle / plan / decision fields under names that map cleanly to the
server contract, `honua-console` can render and resolve proposals from both sources with one
model. The only source-specific value is `Kind` (`gitops-deploy` for this bridge), which the
console already uses to route the governed submit/rollback action back to the owning system.
