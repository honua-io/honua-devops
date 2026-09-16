# Protected deployment recovery: implementation and qualification

Issue #191 supports the **2026.1 safe rollout promise**: a scoped approved change
must recover deterministically and subsequent reconciliation must preserve the
restored service. That complete promise is **not qualified** by the retained
DevOps recovery executor. Keep `HONUA_DEVOPS_PROTECTED_RECOVERY_ENABLED=false` in
the default profile. Turning it on alone does not provide protection.

## What the retained executor verifies

The existing grant spine binds actor, tenant, target, distinct prior/candidate
revisions, policy digest, expiry and a defined compensation. Recovery checks the
actor, target, exact expiry boundary and compensation before issuing a request.
It consumes the server's `target.desiredRevision` and `protection` response, and
requires a matching operation, target, prior revision, candidate, policy and
active observation deadline. Generic model rollback remains separately gated.

A retry with a new executor and a new spine reads the surviving server operation.
`RollbackRequested`, recovery in progress and terminal outcomes are observations;
they do not issue another rollback POST. The server remains responsible for the
atomic provider claim and provider/persistence crash recovery. The local ledger
is only an in-process duplicate guard.

Successful HTTP delivery is not completed recovery. `RollbackRequested` and
`Reconciling` remain in progress; failure and manual intervention need attention;
missing, unknown, mismatched or contradictory responses are indeterminate.
A server `RolledBack` becomes **Previous version restored** only after the
DevOps desired-intent ledger records it (next section). A lost acknowledgement is
also indeterminate, resolved by reading the operation.

## Desired-intent ledger: restoration, quarantine and compare-and-set

With protected recovery enabled, DevOps keeps one append-only version chain per
target in `<audit journal>.desired-intent.jsonl`, next to the file-backed audit
journal (`HONUA_DEVOPS_AUDIT_HOOK_TARGET=file:///...`). Recovery refuses without
it: an in-memory record would forget the quarantine on restart.

- **Approved intent.** Before a sync or explicit submit is sent, the executor
  appends an `approved` record with the desired revision, operation, approval
  reference and actor. If the ledger cannot be read or written, nothing is submitted.
- **Recovery basis.** Before any backend call, the latest record for the target
  must be the approval this grant belongs to (same operation, candidate
  revision). Newer intent refuses with `recovery-intent-superseded`; no record
  refuses with `recovery-intent-unrecorded`.
- **Restoration.** Only a verified server `RolledBack` appends a `restored`
  record: prior revision as desired, candidate added to the quarantine set, with
  operation, approval, actor and any server-reported `metadataRelease.commitSha`.
  The write is conditional on the version read before the rollback request.
- **Conflict or failure.** If newer approval landed first, or the write fails,
  the result is `indeterminate` (`desired-intent-conflict` /
  `desired-intent-write-failed`) and the journey is **Needs attention**. The
  newer intent is never overwritten. A restarted recovery reads the server's
  `RolledBack` and records it once, with no second rollback request.
- **Next reconcile.** A sync or submit for a quarantined revision is refused
  before any backend call (`desired-revision-quarantined`). Approved intent
  carries the quarantine forward, so recovery is forward with a corrected revision.

## Server-owned recovery becomes desired intent

The deterministic trigger belongs to the server. Its deploy reconciler opens the
post-activation observation window and issues the rollback itself when the
safety policy fires. DevOps does not run a second lifecycle. It folds that server
outcome into the ledger:

- **While watching.** With protected recovery enabled, an approved sync or submit
  keeps the prior revision the server exposes in `protection.previousRevision`
  while the candidate's window is open. If the operation settles `RolledBack` for
  the same operation, target and candidate, DevOps appends `restored` (prior
  revision, candidate quarantined) at the approval's version. The journey is
  **Previous version restored**.
- **Prior revision never exposed.** A settled `RolledBack` clears `protection`
  on the server. If DevOps never saw the prior revision (for example, recovery fired
  before promotion), it appends `rejected`. That record quarantines the candidate
  with an empty desired revision. The result is `indeterminate`
  (`restored-revision-unverified`, **Needs attention**); no restoration is claimed.
- **Nobody watching.** If the process restarted mid-poll or the server recovered
  later, the next sync or submit reads the latest `approved` record's operation first.
  A settled `RolledBack` is folded in as `rejected` before the quarantine check.
  The rejected candidate is refused, with one read and no mutation, and a corrected
  revision proceeds carrying the quarantine forward.
- **Mismatch or blocked outcome.** A `RolledBack` whose target or candidate
  differs from the approval, or that still carries `blockingReasons`, is
  `server-recovery-unverified`. Nothing is recorded. On the next reconcile it
  stops the sync or submit instead of leaving the candidate unfenced.
- **Conflict.** If newer intent landed first, it stays the desired state with
  its lineage intact. The rejected candidate is added to that intent's quarantine
  by compare-and-set, so a reconcile cannot resurrect it; the result is still
  `desired-intent-conflict` and no restoration is claimed. If the quarantine
  cannot be carried (the intent keeps changing, or the newer intent desires that
  same revision), the reconcile stops. A failed write is
  `desired-intent-write-failed`. All of these are **Needs attention**.

The retained `RecoveryExecutor` restart path also accepts a settled `RolledBack`
without `protection` as an observation, so a crash between the rollback
acknowledgement and the ledger write records restoration once with no second
rollback request. Unsettled observations without `protection` are still refused.

## Recovery the server triggered and could not prove

A failed recovery is **not** a `RolledBack`. When the safety policy fires and the
rollback does not take, honua-server's `DeployWorkflowReconciler` settles the
operation `ManualInterventionRequired` and **retains** the protection window with
`phase: unavailable`, rather than clearing it. That retained record is the only
durable evidence the candidate was rejected, so DevOps reads it:

| `protection.phase` | `protection.reasonCode` | What it means | Ledger |
| --- | --- | --- | --- |
| `observing` | — | candidate under observation | approved intent stands |
| `observing` | `telemetry-evidence-pending` | window elapsed without health evidence; the deploy stays uncommitted | approved intent stands |
| `recovering` | `rollback-requested` | rollback in flight | nothing recorded |
| `recovering` | `rollback-retry-pending` | rollback did not take this cycle; retry budget remains | nothing recorded |
| `unavailable` | `rollback-failed` | recovery ran and terminally failed | candidate **quarantined**, no restoration claimed |
| `unavailable` | `rollback-retry-budget-exhausted` | bounded retries never settled | candidate **quarantined**, no restoration claimed |
| `unavailable` | `complete-protection-failed` | candidate is **healthy**; only the retained prior replica could not be retired | nothing recorded |
| `expired` | `observation-window-elapsed` | clean commit | approved intent stands |

Two properties matter and are asserted directly:

- **No restoration is ever claimed.** The retained record still carries
  `protection.previousRevision`, and that revision was *not* restored — restoring
  it is exactly what failed. The fold forces the restored revision to null, so the
  result is a `rejected` record with an empty desired revision, blocking reason
  `protected-recovery-unproven`, and the journey **Needs attention**.
- **The candidate is still fenced.** Without this, the next sync or submit of the
  same revision passes the quarantine check and converges straight back onto the
  revision the safety policy just rejected. It is quarantined whether the outcome
  was seen live or folded in on a later reconcile after a restart, and a corrected
  forward revision proceeds carrying that quarantine.

`complete-protection-failed` shares the `unavailable` phase and is deliberately
excluded: the candidate passed its window there, so quarantining it would fence a
revision the server never rejected. Re-deploying it is not refused.

If the quarantine itself cannot be written the result is
`desired-intent-write-failed`, not a silent pass: the candidate is knowingly
unfenced and that is reported rather than left to a refusal that will not happen.

## Rollout journey

Deploy responses lead with a plain `Rollout:` line (Checking update, Updating,
Confirming service health, Update complete, Previous version restored, Needs
attention).

The server's structured `protection.phase` drives it whenever a protection window
is present; the free-text `currentPhase` match is only the fallback for an
operation that reports no window at all:

- `observing` / `protected` → **Confirming service health**
- `recovering` → **Updating**. A recovery in flight is an update in flight, and it
  resolves to Previous version restored or Needs attention. Reporting *Confirming
  service health* would claim the candidate's health is still being verified when
  its safety policy has already rejected it.
- `unavailable` with `rollback-failed` / `rollback-retry-budget-exhausted` →
  **Needs attention**, and this outranks every other signal including a terminal
  status, because *Previous version restored* is the one claim this vocabulary
  must never make on the server's behalf.
- `expired` → defers to the operation outcome (**Update complete** on success).

Each protection state also contributes one plain-language sentence to the
findings, covering the honua-release#321 box 3 fault classes — error-rate /
latency / wrong-body regression and missing or stale health evidence
(`telemetry-evidence-pending`), controller crash and failed rollback
(`rollback-failed`, `rollback-retry-budget-exhausted`), and concurrent service
changes (the desired-intent conflict path above). None of those sentences names
Git, PR, metric-query or telemetry mechanics; a regression test asserts it.

Writes take an exclusive file handle across read, compare and append, so
concurrent DevOps processes on the same host cannot both commit at one version.
The ledger fences DevOps-originated intent. It is not a Git commit, and it does
not fence other writers to the server's target intent.

The ordinary journey is change preview, scoped approval, Checking update /
Updating / Confirming service health, then Update complete / Previous version
restored / Needs attention. Git and telemetry details are optional diagnostics.

## Remaining acceptance and blockers (2026-09-16, candidate 8862065)

Delivered so far: [#192](https://github.com/honua-io/honua-devops/pull/192)
(grant + executor), [#193](https://github.com/honua-io/honua-devops/pull/193)
(recovery scope validation), [#194](https://github.com/honua-io/honua-devops/pull/194)
(durable compare-and-set intent ledger),
[#195](https://github.com/honua-io/honua-devops/pull/195) (server-owned recovery
folded into intent, rollout journey), and this change (recovery the server could
not prove).

### Verified against the candidate

Candidate `honua-server` `886206527cc97bad1bbaa5fa6358910ebc45e9c0`
(honua-release#354; image `ghcr.io/honua-io/honua-server:nightly-8862065`, AOT
index `sha256:0b16046533e5330ecdd48255c06b5397e869191299e1e5e8cc7b4b2ded60b388`).

honua-server#4842 closed via server PR #4943, which added `PlatformDeployAuthority`
to the deploy control surface: with multi-tenancy enabled a tenant-bound principal
must hold a platform-admin role. That closes the coarse authority hole. It does
**not** fence the rollback surface, which was verified directly on the pinned
image:

- `RollbackDeployOperationRequest` still carries exactly one property, `reason`.
- The rollback executor submits only `targetOperationId`, `reason`,
  `approvedDataAffecting`, `approvedRequiresApproval`.
- `DeployProtectionState` / `DeployProtectionResponse` carry no actor, tenant,
  grant identity or permitted-compensation declaration.
- Live: a rollback POST carrying a wrong `targetId`, a foreign `actor` and
  `tenantId`, an unknown `grantId`, an already-past `expiresAt`, a mismatched
  `policyDigest` and a broadened `compensation` returned **HTTP 200** and settled
  the operation, with every one of those fields silently dropped by the binder —
  on an operation that had no protection window at all.

Filed as [honua-server#4958](https://github.com/honua-io/honua-server/issues/4958)
with the exact request shapes and negative cases.

### Still open

- **Server-enforced recovery scope (honua-server#4958).** Until the rollback
  surface accepts and enforces a target/expected-revision/actor/tenant/expiry
  fence, `AuthorizeRecoveryGrant` and `RecoveryExecutor` still have no production
  caller: minting a grant nothing on the server enforces would be theater. The
  server's reconciler remains the deterministic trigger and DevOps consumes its
  outcome.
- **Client-side compare-and-set is not atomic against the server.** The
  desired-intent ledger fences the next *DevOps* reconcile. A server-initiated
  reconcile, or any other writer of the server's target intent, is fenced only
  once honua-server#4958 lands.
- **Git metadata-branch writer** is honua-devops#57 (2026.2). Nothing here writes
  a restoration commit, and nothing claims one without a server-reported
  `metadataRelease.commitSha`.
- **Installed fault/recovery certification** is honua-release#321. honua-server#4619
  closed on this pin via server PR #4904 (installed recovery proof, harness 5/5),
  so the blocker that stood on 2026-09-13 is cleared; the joined installed
  receipt for the advertised protected-change contract is still owned by
  honua-release#321 and is not claimed here. Everything in this repo is HTTP
  boundary evidence against the candidate's response shapes, not installed
  provider qualification.

## Regression evidence

`RecoveryExecutorTests` exercises the actual server response shape with declared
prior/candidate identities and an explicit expected outcome matrix. It rejects
wrong operation/target/policy/revisions, expired/unavailable/missing protection,
and mismatched success responses. The lost-acknowledgement fixture retains server
state across newly constructed spines and asserts one operation and one provider
request. Ledger fixtures cover restoration lineage, newer approval before and
during recovery, write failure followed by restart (one rollback, one restored
record), and refusal of the quarantined candidate on the next sync and submit.
`DesiredIntentLedgerTests` covers compare-and-set, restart durability, 16
concurrent writers (exactly one commit), torn lines and unwritable storage.
These are HTTP boundary fixtures, not installed provider qualification.

`DeploymentRecoveryGrantTests` retains the actor, target, broadened compensation,
plan-mode and expiry negatives, and adds exact-expiry, missing-actor,
same-revision and undefined-compensation checks. No test asserts generated shell
commands as execution evidence.

`UnprovenRecoveryQuarantineTests` covers the recovery-could-not-be-proven
contract against the candidate's response shape: both rejection reason codes
quarantine the candidate without claiming restoration even though the retained
window still exposes `previousRevision`; the next reconcile of that revision is
refused with zero HTTP calls and a corrected revision carries the quarantine
forward; the same outcome is folded in on restart with one read and no mutation;
`complete-protection-failed` records nothing and does not fence a healthy
revision; a recovery still executing reports **Updating** and records nothing; a
scope mismatch records nothing; and a quarantine that cannot be written is
reported as `desired-intent-write-failed` rather than left implied. The journey
projection is asserted at unit level, including that an unproven recovery
outranks a terminal `RolledBack` and that the structured `protection.phase`
drives the journey with free text that contains none of the legacy tokens.

Installed fault/recovery (honua-release#321), atomic server target intent and
server-side next-reconcile preservation (honua-server#4958) remain unproven.
