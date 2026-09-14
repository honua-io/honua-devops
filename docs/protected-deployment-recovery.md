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

Writes take an exclusive file handle across read, compare and append, so
concurrent DevOps processes on the same host cannot both commit at one version.
The ledger fences DevOps-originated intent. It is not a Git commit, and it does
not fence other writers to the server's target intent.

The intended ordinary journey remains change preview, scoped approval, Checking
update / Updating / Confirming service health, then Update complete / Previous
version restored / Needs attention. Git and telemetry details are optional
diagnostics. The retained journey mapper is not yet connected to a protected
deployment approval/trigger flow.

## Remaining acceptance and blockers (2026-09-13)

The earlier [PR #192](https://github.com/honua-io/honua-devops/pull/192) delivered
the grant and executor foundations. Its unqualified restoration/quarantine
findings and shared-ledger restart test did not prove durable convergence.

At server trunk `0fa0a5d0e441eea2f986cf58f8a80630454a41c9`,
`RollbackDeployOperationRequest` contains only `reason` and
`DeployOperationResponse` exposes protection state, but no conditional recovery
grant or Git restoration receipt. Therefore:

- The deployment approval path still needs durable grant/approval lineage and a
  server-owned deterministic trigger consuming that exact scope. The retained
  `AuthorizeRecoveryGrant` and `RecoveryExecutor` have no production callers.
- The server contract must enforce actor/tenant/target, approval/policy,
  permitted compensation, deadline and the current target intent atomically.
  Checking a historical operation's candidate on the client is **not** a
  compare-and-set against newer approved target intent and has a read/write race.
- The DevOps desired-intent ledger records restoration and quarantine with
  compare-and-set and fences the next DevOps reconcile. A canonical Git
  metadata-branch writer (honua-devops#57, 2026.2) and the server's own target
  intent still do not consume that record. Server-initiated reconciles are fenced
  only when the server enforces the recovery scope.
- The ordinary interaction must be wired to that actual approval and observation
  path before it advertises protected change.

The observation implementation in server#4618 is closed. Server#4619 has landed
staged activation, but its installed evidence still fails against the accepted
candidate. Accepted release trunk `f6c54b4396bdadb76676be7b839de71fb9a3de84`
continues to pin server `7ba422672e0c751843b17beb36e954a019cc19fb` / OCI digest
`sha256:dd50cd81c057e37e73a6144572abdfc90d48de314d7625c54c4ef3b6eb65b0fd`.
The retained installed receipt shows preparation exposing the candidate while
`priorRevision` is null. See
[the server qualification disposition](https://github.com/honua-io/honua-server/issues/4619#issuecomment-5653014787).
The proposed newer image is not an accepted candidate. This DevOps change does
not change that pin or claim installed execution.

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
commands as execution evidence. Installed fault/recovery, atomic server target
intent and server-side next-reconcile preservation remain unproven.
