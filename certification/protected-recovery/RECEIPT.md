# Protected recovery live receipt: `nightly-80e23be`

Issue honua-io/honua-devops#191, carrying honua-io/honua-release#321 box 3.

- **Candidate image:** `ghcr.io/honua-io/honua-server:nightly-80e23be`
- **Trunk revision:** `80e23bedfe8ff7b43362c8d8ea22bfae1756df7d`
- **Index digest:** `sha256:9869f044b1c5d0de15aef6c87cc3d60383037ee9a4346d08bb9c83f2c56cc176`
- **Target:** `proof-selfhosted` (`SelfHostedRolling`, backend `honua-yarp-rolling`). The server launches the replicas, swaps its embedded proxy at cutover and retains the prior replica through the observation window.
- **Run:** 2026-09-19T09:03:00.697631Z to 2026-09-19T09:23:21.011521Z

Full evidence: `receipt-nightly-80e23be.json` (server journey) and `devops-live-nightly-80e23be.jsonl` (DevOps code). Every request, response and protection transition is included.

## Recovery and fault classes

| cell | class | result | settled status | protection phase / reason codes seen |
| --- | --- | --- | --- | --- |
| `fenced-recovery` | Declared recovery: sealed grant, every fence refusal, satisfied fence, replay | **pass** | `RolledBack` | `-/-`, `observing/-` |
| `platform-admin-cross-tenant` | Foreign platform administrators refused; grant identity redacted; sealed principal admitted | **pass** | `RolledBack` | `-/-`, `observing/-`, `observing/telemetry-evidence-pending` |
| `tenant-bound-recovery` | Tenant-bound grant: undeclared binding, declared foreign tenant/actor, satisfied fence | **pass** | `RolledBack` | `-/-`, `observing/telemetry-evidence-pending`, `observing/-` |
| `error-rate-regression` | Injected error-rate regression after activation | **pass** | `RolledBack` | `-/-`, `observing/telemetry-evidence-pending`, `recovering/rollback-requested` |
| `latency-regression` | Injected p95 latency regression after activation | **pass** | `RolledBack` | `-/-`, `observing/-`, `recovering/rollback-requested` |
| `missing-telemetry` | Missing telemetry (empty evidence) past warmup + grace | **pass** | `RolledBack` | `-/-`, `observing/-`, `recovering/rollback-requested` |
| `stale-telemetry` | Stale telemetry (samples older than the freshness bound) | **pass** | `RolledBack` | `-/-`, `observing/-`, `recovering/rollback-requested` |
| `wrong-body-regression` | Wrong-body regression (golden-query wrong-result marker) | **pass** | `RolledBack` | `-/-` |
| `controller-crash` | Controller crash: server restarted inside the observation window | **pass** | `RolledBack` | `-/-`, `observing/-`, `recovering/rollback-requested` |
| `failed-recovery` | Failed recovery: prior replica replaced out of band -> retained unavailable | **pass** | `ManualInterventionRequired` | `-/-`, `observing/telemetry-evidence-pending`, `unavailable/rollback-failed` |
| `newer-intent` | Concurrent service change submitted during the window | **pass** | `RolledBack` | `-/-`, `observing/-` |

## Fence refusals observed (no operation transition asserted for each)

| cell | request | HTTP | code |
| --- | --- | --- | --- |
| `fenced-recovery` | foreign target | 409 | `recovery_fence_target_mismatch` |
| `fenced-recovery` | wrong candidate revision | 409 | `recovery_fence_candidate_revision_mismatch` |
| `fenced-recovery` | wrong previous revision | 409 | `recovery_fence_previous_revision_mismatch` |
| `fenced-recovery` | unknown grant | 409 | `recovery_fence_grant_mismatch` |
| `fenced-recovery` | mismatched policy digest | 409 | `recovery_fence_policy_digest_mismatch` |
| `fenced-recovery` | wrong protection phase | 409 | `recovery_fence_protection_phase_mismatch` |
| `fenced-recovery` | unrecognized protection phase | 400 | `recovery_fence_protection_phase_unrecognized` |
| `fenced-recovery` | foreign actor | 403 | `recovery_fence_actor_mismatch` |
| `fenced-recovery` | foreign tenant | 403 | `recovery_fence_tenant_mismatch` |
| `fenced-recovery` | broadened compensation | 403 | `recovery_fence_compensation_not_permitted` |
| `fenced-recovery` | expired grant | 412 | `recovery_fence_expired` |
| `fenced-recovery` | unknown property | 400 | `recovery_fence_unknown_property` |
| `fenced-recovery` | grant expired in flight | 412 | `recovery_fence_expired` |
| `platform-admin-cross-tenant` | complete fence quoting tenant-a's grant, declaring tenant-b's own identity | 403 | `recovery_fence_actor_mismatch` |
| `platform-admin-cross-tenant` | unfenced rollback by tenant-b's platform administrator | 403 | `recovery_fence_actor_mismatch` |
| `platform-admin-cross-tenant` | same actor name in another tenant | 403 | `recovery_fence_tenant_mismatch` |
| `tenant-bound-recovery` | unfenced rollback by a principal that is not the sealed actor | 403 | `recovery_fence_actor_mismatch` |
| `tenant-bound-recovery` | sealed actor declaring a foreign tenant | 403 | `recovery_fence_tenant_mismatch` |
| `tenant-bound-recovery` | another tenant's platform administrator quoting the sealed grant | 403 | `recovery_fence_actor_mismatch` |
| `failed-recovery` | stale observing-phase grant after the window became unavailable | 409 | `recovery_fence_protection_phase_mismatch` |

## Server findings filed from this run

| cell | observed | issue |
| --- | --- | --- |

## DevOps code against the live server

| scenario | result | step | status | journey | blocking reasons |
| --- | --- | --- | --- | --- | --- |
| Live_FailedServerRecovery_QuarantinesWithoutClaimingRestoration | | watched-sync | `indeterminate` | Needs attention | `protected-recovery-unproven` |
|  | | next-reconcile-of-quarantined-candidate | `approval-required` | Needs attention | `desired-revision-quarantined` |
| | | ledger | `rejected` v2 | desired `(none)` | quarantined 1, operation `deploy-honua-devops-live-recovery-6a1310f1302140bc9fc69-4ff3aa21aab1` |
| Live_NewerApprovedIntent_RefusesOlderRecoveryWithoutRequest | | approved-sync | `in-progress` | Confirming service health | - |
|  | | concurrent-approved-change | `failed` | Needs attention | - |
|  | | older-recovery | `approval-required` | Needs attention | `recovery-intent-superseded` |
| | | ledger | `approved` v2 | desired `sha256:860061857f9e` | quarantined 0, operation `deploy-honua-devops-live-recovery-f92aba42ad2f43e4855e2-0cce22b38fe1` |
| Live_ServerRecoveryOnInjectedRegression_IsFoldedIntoDesiredIntent | | watched-sync | `rolled-back` | Previous version restored | - |
|  | | next-reconcile-of-quarantined-candidate | `approval-required` | Needs attention | `desired-revision-quarantined` |
| | | ledger | `restored` v2 | desired `sha256:2c7b1d6d21eb` | quarantined 1, operation `deploy-honua-devops-live-recovery-c3ae4de4465440d8be0f8-25cdd8533763` |
| Live_DeclaredRecovery_IsFencedRecordedAndHoldsOnNextReconcile | | approved-sync | `in-progress` | Confirming service health | - |
|  | | wrong-actor | `approval-required` | Needs attention | `recovery-scope-mismatch` |
|  | | wrong-target | `approval-required` | Needs attention | `recovery-scope-mismatch` |
|  | | broadened-compensation | `approval-required` | Needs attention | `recovery-scope-mismatch` |
|  | | expired-grant | `approval-required` | Needs attention | `recovery-scope-mismatch` |
|  | | declared-recovery | `in-progress` | Updating | - |
|  | | restart-observes-recovery | `rolled-back` | Previous version restored | - |
|  | | replay | `rolled-back` | Previous version restored | - |
|  | | next-reconcile-of-quarantined-candidate | `approval-required` | Needs attention | `desired-revision-quarantined` |
| | | ledger | `restored` v2 | desired `sha256:2c7b1d6d21eb` | quarantined 1, operation `deploy-honua-devops-live-recovery-8b7ddcec7ac14457b1508-366ee2cf3324` |
| Live_GenericRollbackTool_StaysGated | | generic-rollback-tool | `experimental-disabled` | Checking update | `rollback-experimental-disabled` |

All five scenarios passed (`ProtectedRecoveryLiveJourneyTests`, matching transcripts and `devops-live-nightly-80e23be.trx`).

## The declared recovery DevOps sent

`POST /api/v1/admin/deploy/operations/deploy-honua-devops-live-recovery-8b7ddcec7ac14457b1508-366ee2cf3324/rollback` -> HTTP 200

```json
{
  "reason": "declared recovery: live journey",
  "targetId": "proof-selfhosted",
  "expectedCandidateRevision": "sha256:bd23f02e2ba64247720709672b482485cd6e916be49594060889d3fc17d6ee1b",
  "expectedPreviousRevision": "sha256:2c7b1d6d21ebcfa2012402b1f13e07548e3da4f926a20700de93c0f53b083e2c",
  "expectedProtectionPhase": "observing",
  "grantId": "grant-da87092bf4dbdc397312eb5fd40258a3",
  "policyDigest": "40F265FF6E39CB44F5EC660B5874553F53D5B27337D0DDAE638CFEA37494A032",
  "actor": "admin",
  "notAfter": "2026-09-19T09:37:19.3166518+00:00",
  "compensation": "restore-previous-revision"
}
```
