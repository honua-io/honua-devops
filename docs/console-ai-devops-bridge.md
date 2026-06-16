# Console-Facing AI DevOps Operation Bridge

Tracked by `honua-devops#59` (proposal-contract alignment in `honua-devops#78`). This
document defines the Console-facing AI DevOps bridge that `honua-devops` owns: stable,
evidence-linked projections for GitOps proposals, unified operation status, and advisory AI
briefs. The field-for-field alignment with the honua-server `OperationProposal` contract that
lets `honua-console` aggregate server + devops proposals on one approval surface is specified
in `docs/gitops-proposal-contract.md`.

## Purpose

Console must surface AI DevOps and GitOps workflows without scraping CI logs, raw
Git state, or agent chat output. AI output stays advisory by default, while governed
operations use durable operation IDs that can be followed across proposal, PR/CI,
promotion, SLO watch, and rollback views.

This work is bounded to the `honua-devops` projection layer. It defines the contracts
and the mapping over existing local operation models and landed honua-server
deploy-control APIs. The durable PR/CI evidence APIs, server-side persistence, and the
Console UI are owned by the bounded child tickets listed at the end of this document.

## Scope

In scope (this repo):

- Bridge DTOs and mappers under `src/Honua.DevOps.Agent/Operations/ConsoleBridge/`.
- Agent/tool projections: `create_gitops_proposal`, `get_gitops_proposal`,
  `get_devops_operation_status`, `build_ai_devops_brief`, `explain_release_package`.
- Reuse of `BackendGateway` deploy-control calls, `OperationResponse`,
  `OperationBackendStep`, operator policy, and redaction.

Out of scope (child tickets):

- Console UI implementation.
- Server-side event/job/release/log evidence APIs and durable PR/CI links.
- General-purpose chat agent implementation.
- Any standing in-memory/mock bridge client in the merged build.

## Identity Rules

- The honua-server deploy-control `operationId` is the Console-facing stable workflow
  ID. It is reused unchanged across proposal, PR, CI, promotion, smoke, SLO watch,
  rollback-readiness, and rollback-execution views.
- Idempotency keys derive from operational scope, never prose:
  `honua-devops:proposal:{targetId}:{service}:{envs}:{revision}:{action}`. Environments
  are lower-cased and sorted so key generation is order-insensitive. When the readable
  key would exceed 200 characters, the variable scope collapses to a stable short hash
  (`honua-devops:proposal:{targetId}:{sha256-16}`) to bound length.
- The JSONL audit `OperationId` remains a per-tool-call trace id and is **not** the
  Console workflow id.
- When no durable server operation can be created — no deploy target, or the server
  contract is unavailable — the bridge returns a blocked projection with status
  `target-unconfigured` or `contract-unavailable` instead of inventing an id.

## Tools and Contracts

### `create_gitops_proposal`

Validates service, environments, revision, and action with the same helpers as
`deploy_service_gitops` (`DeploymentInputs`). When `HONUA_DEVOPS_DEPLOY_TARGET_ID` is
configured, it calls deploy preflight and plan (evidence), then creates a durable
server operation with `submitImmediately=false`. It never calls manifest apply, submit,
rollback, or write-capable runbooks. Returns a `GitOpsProposalBridge`:

- `proposalId`, `operationId`, `idempotencyKey`, `status`
- `service`, `targetEnvironments`, `desiredRevision`, `currentRevision`
- `requestedAction`, `effectiveAction` (`propose`), `owner`, `approvalRequired`
- `workflowLinks`, `evidence`, `suggestedActions`, `createdAt`, `updatedAt`
- `kind`, `requester`, `agent`, `proposalStatus`, `plan`, `decision` — the canonical
  honua-server `OperationProposal` alignment fields (issue #78). `proposalStatus` is the
  canonical lifecycle value (1:1 with the server `WorkflowOperationStatus`); `plan` carries
  diff/dry-run/risk/blocking-reasons; `decision` is the recorded approve/reject audit (null
  until a decision is recorded). See `docs/gitops-proposal-contract.md` for the full
  field-for-field and lifecycle mapping tables.

`status` (the bridge-local projection) is `proposed`, `target-unconfigured`, or
`contract-unavailable`; `proposalStatus` (the canonical lifecycle) is a
`WorkflowOperationStatus` value such as `Planned`, `AwaitingApproval`, `Submitted`,
`Succeeded`, `Failed`, or `Rejected`.

### `get_gitops_proposal`

Projects an existing proposal by stable `operationId` over the deploy-control operation
record, with the same `GitOpsProposalBridge` shape, raw evidence references, and
governed suggestions. Service, action, owner, and revisions are read from the
deploy-control `target` object, where honua-server echoes the create-request parameters.
Never scrapes Git or CI. When the operation is not found or the contract is unavailable,
the projection stays blocked (`contract-unavailable`, `operationId` null) and omits the
server-operation link and governed submit/rollback suggestions, so it never advertises
mutating actions against an operation that does not exist.

### `record_gitops_proposal_decision`

Records an auditable approve/reject decision against a proposal by stable `operationId`,
consistent with the honua-server decision audit (issue #78). Captures the deciding `actor`
and a free-form `reason` (redaction-scrubbed) and returns the `GitOpsProposalBridge` with a
populated `decision` (`ProposalDecision`: `decision`, `actor`, `reason`, `decidedAt`,
`resultingStatus`, `governedAction`). An `approve` moves the canonical lifecycle toward
`Submitted` and authorizes (but never invokes) the governed `submit`; a `reject` is terminal
(`Rejected`) and surfaces no mutating action. The bridge records the decision only — it never
submits, executes, or rolls back. When the operation cannot be read the projection stays
blocked (`contract-unavailable`, `proposalStatus` `unknown`) while still recording the
decision for audit. See `docs/gitops-proposal-contract.md` for the decision-audit and
lifecycle mapping.

### `get_devops_operation_status`

Maps the deploy-control workflow status into a `DevOpsOperationStatus` whose sections
all share one `operationId`:

| Server status | Bridge status | Phase |
| --- | --- | --- |
| `Planned` | `planned` | `plan` |
| `AwaitingApproval` | `awaiting-approval` | `approval` |
| `Submitted` | `submitted` | `execution` |
| `Reconciling` | `reconciling` | `execution` |
| `Succeeded` | `succeeded` | `complete` |
| `Failed` | `failed` | `complete` |
| `RollbackRequested` | `rollback-requested` | `rollback` |
| `RolledBack` | `rolled-back` | `rollback` |
| `ManualInterventionRequired` | `manual-intervention-required` | `blocked` |
| unreadable | `unknown` | `unknown` |

`proposal`, `promotion`, `rollbackReadiness`, and `rollbackExecution` are derived from
the server status. `pr`, `ci`, `smoke`, and `sloWatch` are marked `evidence-missing`
rather than scraped from GitHub or CI; durable PR/CI links are owned by the server
child ticket.

### `build_ai_devops_brief`

Advisory projection. Produces an `AiDevOpsBrief` with `affectedResources`, raw
`evidence` references, `suggestedActions`, `confidence`, `owner`, `status` (`advisory`),
and `workflowLinks`. Auto-apply is disabled; the brief makes no backend calls and never
executes. Mutating suggestions carry `requiresApproval=true` and `mutatesState=true`
with a `targetOperationId` and are surfaced but never run.

### `explain_release_package`

Read-only release-package explanation surface (tracked by `honua-devops#58`). Console
hands the bridge the release-package evidence document — the server-computed
compatibility report, script coverage, PR preview, promotion gates, and rollback plan —
and gets back a `ReleaseExplanation`:

- `explanationId`, `operationId`, `correlationId`, `mode`, `releaseId`, `service`,
  `targetEnvironments`, `desiredRevision`
- `readiness` (`ready` / `warning` / `blocked` / `unknown` / `rollback-required`)
- `summary` (single human-readable paragraph Console can render verbatim)
- `sections` (one `ReleaseExplanationSection` per `compatibility`, `script-coverage`,
  `pr-preview`, `promotion-gates`, `rollback-plan`; each with its own status, findings,
  and evidence)
- `promotionGates`, `requiredApprovals`, `residualRisks`, `rollbackClassification`
  (`automatic` / `manual` / `irreversible` / `not-required` / `unknown`)
- `evidence`, `suggestedActions`, `workflowLinks`, `blockingReasons`, `createdAt`

It does **not** compute compatibility — it interprets the supplied report (server
compatibility computation is owned by `honua-server#57`). It never scrapes Git or CI: a
section without supplied evidence is marked `evidence-missing`. The explanation is
read-only in `explanation` mode (the default) and makes no backend call. In `proposal`
mode it surfaces a single governed, approval-required PR-creation handoff suggestion for
a non-blocked release; it still never creates the PR, submits, applies, or rolls back. A
`rollback-required` release surfaces a governed rollback suggestion the same way. All
free text passing through (findings, residual risks, gate labels/details, release/service
identifiers, PR/rollback references) is redaction-scrubbed before it reaches the
projection. A document that cannot be parsed returns an `unknown` projection with an
`evidence-missing` reference rather than throwing. `correlationId` is preserved on the
explanation so Console actions, the server release package, PRs, CI checks, and GitOps
operations can be correlated.

The structured `ReleaseExplanation` is carried on `OperationResponse.ConsoleBridge`
(in-process, `JsonIgnore`d like the other bridge projections); the LLM-facing/audit wire
shape keeps only the compact status/summary.

## Advisory and Approval Guarantees

- Proposals are always recorded with `submitImmediately=false`. Execution requires a
  separate governed submit through the existing approval/execution-tier/audit gates.
- Suggested actions carry `requiresApproval`, `mutatesState`, `targetOperationId`, and a
  `workflowLink`. The bridge never invokes the governed submit/rollback path itself.
- Bridge tools return `OperationResponse`, so each call emits one audit record with
  backend steps; the `mutated` flag reflects the actual server write (operation creation
  is a durable write even though nothing executes).

## Evidence and Links

- `EvidenceRef` carries `type`, `source`, `rawRef`, resolved `url`, `summary`,
  `capturedAt`, and a `sensitivity` marker. Payload previews are scrubbed by the
  transport before they reach a projection.
- Server deep links and raw evidence refs derive from
  `HONUA_DEVOPS_HONUA_API_BASE_URL`. Console deep links derive from the optional
  `HONUA_DEVOPS_CONSOLE_BASE_URL`; when unset, the `self` link is returned with
  `available=false` rather than fabricated.

## Support-Ticket Trust State (L2/L3)

Tracked by `honua-devops#70` (part of `honua-server#1495`). The bridge also projects a
support ticket's live trust state so Console can render escalation rationale and the
remote-session posture without scraping agent prose. The `get_support_ticket_console_view`
tool returns a `SupportTicketConsoleView` (kind `support-ticket-view`) over the same
diagnosis pipeline as `triage_support_ticket`. It is a read-only projection: it never
opens a session, posts a diagnosis, or escalates.

- **`DelegatedSessionState`** — access mode (`disabled`/`read-only`/`operator-scoped`,
  verbatim from `SupportSessionAccess.ToConfigValue()`), the guided-fix posture, the
  effective TTL (min-clamped against policy), `establishedAt`/`expiresAt` for a countdown,
  the customer-visible flag, and an `active` flag (true only when access is enabled and the
  ticket resolved to an operator-scoped session). TTL/expiry/customer-visibility derive
  from `OperatorPolicy.SupportSession`.
- **`DiagnosisScorecardBridge`** — projects the `DiagnosisScorecard` posted to
  honua-support: `overallResult` (pass/fail), composite score, confidence, the
  per-criterion booleans (diagnosis/remediation/policy/rollback/recovery/health) for a
  checklist, failure modes, and evidence references.
- **`EscalationRationale`** — "why escalated": an `escalated` flag, a stable `trigger`
  code (`matched-fault-write-remediation`, `severity-escalation`, `access-requested`, or
  `not-escalated`), the concrete `signal`, the human-readable justification, access scope,
  TTL, rollback intent, and required approval context. The same `trigger`/`signal` now
  travel on the `escalation` object that `SupportGateway.PostDiagnosisAsync` posts back to
  honua-support alongside the diagnosis.
- **Audit references** — `EvidenceRef`s of type `audit-journal` keyed by the support-triage
  operation scope, with a `file://` URL when `HONUA_DEVOPS_AUDIT_HOOK_TARGET` is
  file-backed, so Console deep-links to the append-only JSONL rather than embedding raw
  audit lines.

### Alignment with honua-support#20 (telemetry/context contract)

honua-support#20 formalizes a **shared, versioned telemetry/context schema** for the
console→support→devops auto-attached payload (user/tenant, env kind, version/commit,
route, recent errors, instance URL + scoped key). Two mismatches to resolve when that
schema lands (tracked there, not fixed here):

1. **Auto-bundle payload is informal.** `SupportGateway.TriggerAutoBundleAsync` posts only
   `{ instanceUrl, apiKey }`. It does not yet carry the #20 context fields (tenant, env
   kind, version/commit, route, recent errors). It should adopt the versioned schema once
   published.
2. **Escalation webhook is a separate, narrower shape.** The signed inbound
   `EscalationWebhookPayload` (`eventType`, `ticketId`, `severity`, `environment`,
   `service`, `phase`, `sla`, nested `escalation.{tier,accessMode,escalatedAt}`,
   `ticketUrl`) mirrors honua-support's `SupportNotificationPayload`, not the #20
   telemetry/context schema. The HMAC-over-raw-bytes signing and `X-Honua-Event` /
   `X-Honua-Signature` headers are sound and should be retained; the body should reference
   the shared schema version so all three repos stay in lockstep.

## Real-Server Integration Policy

Per the Console Patterns Charter section 11, the bridge binds to real honua-server
deploy-control contracts. There is no standing in-memory/mock bridge client in the
merged build. Unit tests use local HTTP handlers only for isolation.

Live coverage lives in `ConsoleBridgeLiveIntegrationTests`, gated behind
`HONUA_DEVOPS_LIVE_INTEGRATION=true` and a configured deploy target. It stays blocked
until the honua-server `#59`/`#58` durable proposal/operation contracts land, then runs
against a live server (Testcontainers or a live endpoint) without code changes.

## Bounded Child Tickets

- **honua-server `#59`/`#58`** (coordination `honua-server#1165`): durable
  proposal/operation persistence and PR/CI/evidence/release/log link APIs.
- **honua-console `#22`/`#24`**: consume the bridge projections and render proposal,
  operation status, evidence, and approval actions in the one Console runtime
  (ADR-0001).
- **honua-sdk-dotnet** (optional): typed client models for the landed bridge API. Until
  available, a narrow internal `HttpClient` shim over the real server contract suffices.
