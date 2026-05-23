# Guided-Fix Support Workflow

This document describes the guided-fix operating model for Honua DevOps support operations.

## Overview

The guided-fix workflow moves a support ticket through three postures, each with explicit evidence and approval requirements:

1. **Read-only triage** (default) — diagnose without writes
2. **Guided fix** — produce commands and artifacts for the customer to execute
3. **Operator-scoped escalation** — approved, time-bound, audited operator execution

The default path is always read-only. Escalation requires explicit ticket-scoped approval.

## Ticket Input Contract

Every support interaction starts with a `SupportTicket` containing:

| Field | Required | Description |
|-------|----------|-------------|
| `ticketId` | Yes | Ticket or incident reference ID |
| `severity` | Yes | `critical`, `high`, `medium`, or `low` (aliases: `p1`–`p4`, `sev1`–`sev4`) |
| `environment` | Yes | Target environment (e.g., `dev`, `staging`, `prod`) |
| `symptoms` | Yes | Customer-reported symptoms and reproduction steps |
| `requestedAction` | Yes | What the customer is asking for (e.g., `diagnose`, `fix`, `rollback`) |
| `allowedAccessMode` | Yes | `read-only`, `guided-fix`, or `operator-scoped` |
| `ttlMinutes` | Yes | Maximum session duration (1–1440 minutes) |
| `rollbackExpected` | Yes | Whether rollback preparation is required |
| `attachedEvidence` | No | Logs, screenshots, traces, or other artifacts |

## Escalation Webhook Receiver

`honua-devops --listen` runs the inbound receiver for `honua-support`
escalation events. The listener binds to localhost only:
`http://localhost:${HONUA_DEVOPS_WEBHOOK_PORT:-8090}${HONUA_DEVOPS_WEBHOOK_PATH:-/escalations}`.

Environment:

| Variable | Required | Default | Description |
|----------|----------|---------|-------------|
| `HONUA_DEVOPS_WEBHOOK_SECRET` | Yes | none | Shared HMAC-SHA256 secret matching `honua-support` |
| `HONUA_DEVOPS_WEBHOOK_PORT` | No | `8090` | Local TCP port, 1–65535 |
| `HONUA_DEVOPS_WEBHOOK_PATH` | No | `/escalations` | Normalized URL path; must start with `/` |
| `HONUA_DEVOPS_WEBHOOK_AUTO_TRIAGE` | No | `true` | Runs read-only triage after an accepted event |

Accepted requests are signed `POST` bodies:

| Contract item | Value |
|---------------|-------|
| Method | `POST` |
| Path | `HONUA_DEVOPS_WEBHOOK_PATH` |
| Required signature header | `X-Honua-Signature: sha256=<lowercase-hex HMAC-SHA256(secret, raw body)>` |
| Event body field | `eventType: "ticket.escalation_requested"` |
| Informational event header | `X-Honua-Event: ticket.escalation_requested` |

Payload body fields are camelCase and mirror honua-support's
`SupportNotificationPayload`:

| Field | Description |
|-------|-------------|
| `eventId` | Sender-generated event identifier, surfaced for audit correlation |
| `eventType` | Must be `ticket.escalation_requested` |
| `ticketId` | Ticket or incident reference |
| `customerId` | Customer reference from `honua-support` |
| `severity` | Support severity string |
| `environment` | Target environment |
| `service` | Affected service |
| `phase` | Ticket lifecycle phase from `honua-support` |
| `customerStatus` | Customer-facing status string from `honua-support` |
| `sla` | SLA snapshot object (`supportTier`, `firstResponseTarget`, `updateCadence`, etc.); summarised for display |
| `escalation` | Optional object — `tier` (int), `accessMode` (string), `escalatedAt` (ISO-8601) |
| `ticketUrl` | Optional deep link to the ticket in `honua-support` |

`honua-support`'s notification body does not carry symptoms or a diagnosis
blob — those live on the ticket itself. Operators follow `ticketUrl` for that
detail; the embedded read-only triage runs against ticket metadata only.

The HTTP response body is always a small JSON status envelope:
`{"status":<http-status>,"reason":"<reason>"}`. Current reasons are
`accepted`, `invalid-signature`, `malformed-json`, `empty-payload`,
`unexpected-event:<event>`, `method-not-allowed`, and `not-found`.

When `HONUA_DEVOPS_WEBHOOK_AUTO_TRIAGE=true`, the accepted payload is rendered
to the operator console and then passed through `triage_support_ticket` with
`allowedAccessMode=read-only`; the webhook itself does not grant write access.

## Workflow Phases

### Phase 1: Read-Only Triage (Default)

The agent operates in plan mode with no write capability:

- Analyzes symptoms, logs, metrics, deployment state, and attached evidence
- Produces a diagnosis summary with confidence level (`high`, `medium`, `low`)
- Lists missing evidence that would improve diagnosis accuracy
- Emits a recommended next action: `observe-only`, `guided-customer-action`, or `approval-required-escalation`

This phase is always safe — no environment changes, no operator access.

### Phase 2: Guided Fix

When the diagnosis is complete and remediation is needed, the agent produces a guided-fix package for the customer:

- Shell commands the customer can run directly
- Terraform / GitOps PR or patch proposals for customer review
- Step-by-step rollback or configuration change checklist
- Post-change validation steps

The agent does **not** write to the environment. The customer executes the commands.

### Phase 3: Operator-Scoped Escalation

If guided fix is insufficient and operator intervention is required:

**Requires:**
- Explicit approver identity
- Ticket or incident reference
- Access scope matching support-session policy
- TTL (capped at the lesser of requested and policy-configured TTL)
- Operator justification
- Rollback intent declaration
- Audit hook target

**Constraints:**
- Support session access must be `operator-scoped` in policy
- Allowed access mode on the ticket must be `operator-scoped` or `execute`
- Execution tier must be `execute-lower-env` or higher
- Break-glass requires post-action review when policy enforces it

## Mode Resolution

The guided-fix mode is resolved from the intersection of:

| Input | Effect |
|-------|--------|
| `executionMode = plan` | Forces read-only triage |
| `executionTier = observe` or `plan` | Forces read-only triage |
| `supportSession.access = disabled` | Forces read-only triage |
| `allowedAccessMode = read-only` | Forces read-only triage |
| `supportSession.access = operator-scoped` + `allowedAccessMode = operator-scoped` | Enables operator-scoped escalation |
| All other combinations | Guided fix (customer-operated remediation) |

## Evidence Bundle

Every triage interaction captures:

| Evidence Field | Description |
|----------------|-------------|
| `requestedAction` | What the customer asked for |
| `effectiveAction` | What mode was actually used (read-only-triage, guided-fix, operator-scoped) |
| `policyGate` | The policy gate that governed access |
| `approvalMode` | The approval mode in effect |
| `supportSessionAccess` | The support session access level |
| `supportSessionTtlMinutes` | The TTL in effect |
| `supportSessionCustomerVisible` | Whether the session was customer-visible |
| `requiredChecks` | Evidence requirements (ticket-context, diagnosis-evidence, access-mode-record, etc.) |

## Adoption Guidance

Recommended default posture for customer deployments:

- Customer owns their `honua-devops` control repo
- Plan mode and `pr-first` approval by default
- Guided-fix output is shared with the customer first
- No standing support-session write access
- Operator-scoped execution only for approved, ticket-scoped exceptions
- Customers can run the agent themselves for read-only triage, or receive generated fix artifacts from support

## Integration with Operator Policy

The guided-fix workflow integrates with the existing operator-policy model:

- `ApprovalMode` governs whether writes require PR-first, direct-allowed, or break-glass-only flow
- `SupportSessionPolicy` controls access level, TTL, and customer visibility
- `BreakGlassPostActionReviewRequired` triggers post-action review for emergency access
- All actions are recorded to the configured `AuditHookTarget`
