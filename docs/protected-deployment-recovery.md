# Protected deployment recovery: implementation and qualification

Issue #191 supports the **2026.1 safe rollout promise**: a scoped approved change
must recover deterministically and subsequent reconciliation must preserve the
restored service. Qualification belongs to an installed target and server revision, not merely to
the retained DevOps executor. The unconfigured default keeps
`HONUA_DEVOPS_PROTECTED_RECOVERY_ENABLED=false`; a qualified target enables it
with the durable ledger and server policy below. Turning it on alone does not
provide protection.

## Qualified target configuration

Use a server containing honua-server#5004: the sealed actor and tenant bind every
caller, including platform administrators. Qualify the installed backend with
`certification/protected-recovery/` before enabling this profile. The proof target
is `SelfHostedRolling` / `honua-yarp-rolling`; it does not qualify another backend.

Configure DevOps for that target with:

```dotenv
HONUA_DEVOPS_PROTECTED_RECOVERY_ENABLED=true
HONUA_DEVOPS_DEPLOY_TARGET_ID=<qualified-target-id>
HONUA_DEVOPS_AUDIT_HOOK_TARGET=file:///var/lib/honua-devops/audit.jsonl
HONUA_DEVOPS_EXPERIMENTAL_ROLLBACK=false
HONUA_DEVOPS_EXPERIMENTAL_CROSS_ENV_PROMOTION=false
```

Keep the normal deployment approval policy. Protected recovery neither widens
that policy nor grants the model a recovery tool. The server must explicitly
admit `control-plane.deploy.rollback` in its operation policy so the request
reaches the sealed recovery fence. The fixture's isolated server config uses:

```dotenv
Operations__Policy__Rules__0__OperationId=control-plane.deploy.rollback
Operations__Policy__Rules__0__Decision=Allow
Operations__Policy__Rules__0__Reason=Admit declared recovery to the sealed-principal fence.
```

Merge that rule into the installed server's policy at an unused rule index;
do not overwrite another rule. Admission still requires the server's ordinary
authentication, deployment authority and matching recovery grant. Deterministic
recovery remains server-owned; no new model response or CLI invocation is needed.
The persisted audit and desired-intent files must survive DevOps restarts.

## What the retained executor verifies

The existing grant spine binds actor, tenant, target, distinct prior/candidate
revisions, policy digest, expiry and a defined compensation. Recovery checks the
actor, target, exact expiry boundary and compensation before issuing a request.
It consumes the server's `target.desiredRevision` and `protection` response, and
requires a matching operation, target, prior revision, candidate, policy and
active observation deadline. Generic model rollback remains separately gated.

The rollback request quotes a **recovery fence** the server enforces at admission
(honua-server#4958, delivered by #4963). The target, prior/candidate revisions,
policy digest and expiry (`notAfter`) come from the grant; the grant id, actor,
tenant and permitted compensation are quoted from the grant the server sealed
when the candidate was first exposed (`protection.grantId`, `actor`, `tenantId`,
`permittedCompensation`). Nothing is invented. The executor refuses without any
request (`recovery-grant-unsealed`) when:

- the window publishes no sealed grant;
- the window permits a compensation other than `restore-previous-revision`;
- the grant was sealed for a tenant other than the approved one.

A server refusal (`recovery_fence_*`, 400/403/409/412) is a definite answer, not a
lost acknowledgement. It is reported as `recovery-fence-refused` plus the code, and
the ledger is unchanged.

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
- **Prior revision never exposed.** A settled `RolledBack` normally clears
  `protection` on the server. On `nightly-2cc2213`/`d1fc139` the self-hosted backend keeps a
  stale window instead (`recovering`, or `observing`; honua-server#4989). The
  terminal status is authoritative, and a retained window's `previousRevision`
  counts as the exposed prior revision. If DevOps never saw the prior revision (for example, recovery fired
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

## Initial live proof (2026-09-16, `nightly-d1fc139`)

Delivered so far:

- [#192](https://github.com/honua-io/honua-devops/pull/192): grant and executor.
- [#193](https://github.com/honua-io/honua-devops/pull/193): recovery scope validation.
- [#194](https://github.com/honua-io/honua-devops/pull/194): durable compare-and-set intent ledger.
- [#195](https://github.com/honua-io/honua-devops/pull/195): server-owned recovery folded into intent, and the rollout journey.
- [#196](https://github.com/honua-io/honua-devops/pull/196): recovery the server could not prove.
- [#197](https://github.com/honua-io/honua-devops/pull/197): the server-enforced recovery fence and initial live journey below.

### Live journey

Operator ruling A: proofs run on the newest imaged trunk nightly.

- **Image:** `ghcr.io/honua-io/honua-server:nightly-d1fc139`.
- **Trunk:** `d1fc139a64ce33c817bd927bacb2103714221515`.
- **Index:** `sha256:4bac230b40b0b07e396af54e2fc801420d2957bb31a6b6dafb3d402a8444d350`.
- **Contains:** #4963.
- **Developed on:** `nightly-2cc2213` (index `sha256:61e06ef3...`). No deploy-surface change separates the two.

`certification/protected-recovery/` boots that image against PostGIS and Redis. It has one `SelfHostedRolling` target on `honua-yarp-rolling`: a real protected activation where the server launches the workload replicas, swaps its embedded proxy at cutover, and retains the prior replica through the observation window. Faults are injected through a Prometheus query stub.

The prior and candidate workloads are distinct immutable image ids. Traffic is read through the server's own proxy route. Receipts, with every request, response and phase transition:

- `receipt-nightly-d1fc139.json` (server journey, `journey.py`);
- `devops-live-nightly-d1fc139.jsonl` (DevOps code, `ProtectedRecoveryLiveJourneyTests`);
- `RECEIPT.md` (summary).

What that proves:

- **Grant bound at approval and exposure.** The window seals `grantId`, `actor`, `tenantId` (tenant-bound principals), the revision pair, the policy digest and `permittedCompensation: restore-previous-revision`. DevOps records approved intent before submit, and mints a grant bound to actor, tenant, target, the revision pair, the server's policy digest, an expiry and the one compensation.
- **Only the declared recovery is admitted.** Locally, a wrong actor, wrong target, broadened compensation or expired grant is refused with no request.
  - The server refuses, with no transition:
    - a foreign target;
    - a wrong candidate or previous revision;
    - an unknown grant;
    - a mismatched digest;
    - a wrong or unrecognized phase;
    - a declared foreign actor or tenant;
    - a broadened compensation;
    - an expired `notAfter`, including a grant that expired in flight;
    - an unknown property.
  - An undeclared rollback by a principal that is not the sealed actor is refused.
  - The DevOps fence is admitted and the prior revision serves again. A replay issues no second compensation.
  - The generic rollback tool stays `experimental-disabled` with zero requests.
- **Server-owned truth.** Every fault class is recovered by the server's reconciler with no DevOps rollback request:
  - error-rate regression;
  - p95 latency regression;
  - missing telemetry;
  - stale telemetry;
  - wrong-body regression (golden-query marker; fails before activation);
  - controller crash (server restarted mid-window; grant, deadline and digest preserved).
- **Restored intent, quarantine and the next reconcile.** A watched server recovery, and a declared recovery observed after an executor restart, each record `restored` (prior desired, candidate quarantined, with operation, approval and actor lineage). The next reconcile of the candidate is refused with no mutation, and the prior revision keeps serving.
- **Failed recovery.** The retained prior replica was replaced out of band (a concurrent service change). The server settles `ManualInterventionRequired` and retains `unavailable` / `rollback-failed`, and the rejected candidate still serves. DevOps records `rejected` (no restoration claimed, candidate quarantined, **Needs attention**), and a stale `observing`-phase fence is refused (`409`).
- **Compare-and-set.** A concurrent approved change for the same target is recorded first, so the older grant's recovery is refused (`recovery-intent-superseded`) before any request, and the server window is untouched. A concurrent server-side deploy fails to start its replica and leaves the first window's grant and declared recovery intact.

### Findings from the initial run

- **Cross-tenant compensation ([honua-server#4987](https://github.com/honua-io/honua-server/issues/4987)).** The initial image admitted another tenant's platform administrator against tenant-a's grant, both unfenced and with a complete fence declaring the foreign caller's identity. Server [#5004](https://github.com/honua-io/honua-server/pull/5004) removed the exemption and binds both the caller and declared identity to the sealed grant. The updated `platform-admin-cross-tenant` cell requires exact refusal, unchanged operation and replicas, redacted status reads, and successful recovery by the sealed principal. The original failing receipt is retained as before-fix evidence.
- **Wrong-body gating on self-hosted targets ([honua-server#4988](https://github.com/honua-io/honua-server/issues/4988)).** The plan admits a golden-query or health URL on the replica, and the runtime SSRF guard then always refuses it, failing a correct candidate at the exposure deadline. The wrong-body class above was proven against a public HTTPS body for that reason.
- **Stale window on settled `RolledBack` ([honua-server#4989](https://github.com/honua-io/honua-server/issues/4989)).** DevOps keys on the terminal status and is unaffected.
- **Git metadata-branch writer** is honua-devops#57 (2026.2). Nothing here writes a restoration commit, and nothing claims one without a server-reported `metadataRelease.commitSha`.
- **The trigger stays server-owned.** `RecoveryExecutor` has no model-facing or CLI entry point. The deterministic recovery is the server reconciler's, and the executor is the fenced, retained path for a declared recovery.

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

`RecoveryExecutorTests` also covers the fence:

- the exact body: approved scope plus the sealed grant id, actor and tenant, with nothing else;
- refusal without a request when the window is unsealed, broadened, or sealed for another tenant;
- a server `recovery_fence_*` refusal is definite and records nothing.

`ServerOwnedRecoveryTests` covers the retained-window `RolledBack` shapes observed live.

The live evidence is `ProtectedRecoveryLiveJourneyTests`. It is opt-in, via
`HONUA_DEVOPS_LIVE_PROTECTED_RECOVERY=true` against `certification/protected-recovery/boot.sh`,
and never runs in CI.
