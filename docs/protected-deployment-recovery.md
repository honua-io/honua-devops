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
`RolledBack` reports only the server's recovery outcome. It does not prove a Git
commit, quarantine, restored desired state, or that the next reconcile is safe.
A lost acknowledgement is also indeterminate, resolved by reading the operation.

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
- A canonical Git desired-state writer must commit restoration plus candidate
  rejection/quarantine with operation, approval and commit lineage, conditional
  on the approved branch head. Conflicts and write failures must prevent a
  converged outcome. Neither this executor nor its strings implement that writer.
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
request. This is an HTTP boundary fixture, not installed provider qualification.

`DeploymentRecoveryGrantTests` retains the actor, target, broadened compensation,
plan-mode and expiry negatives, and adds exact-expiry, missing-actor,
same-revision and undefined-compensation checks. No test asserts generated shell
commands as execution evidence. Installed fault/recovery, atomic target intent,
Git conflict/write failure and next-reconcile preservation remain unproven.
