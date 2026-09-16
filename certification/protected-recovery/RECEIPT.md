# Protected recovery live receipt: `nightly-d1fc139`

Issue honua-io/honua-devops#191, carrying honua-io/honua-release#321 box 3.

- **Candidate image:** `ghcr.io/honua-io/honua-server:nightly-d1fc139`
- **Trunk revision:** `d1fc139a64ce33c817bd927bacb2103714221515`
- **Index digest:** `sha256:4bac230b40b0b07e396af54e2fc801420d2957bb31a6b6dafb3d402a8444d350`
- **Target:** `proof-selfhosted` (`SelfHostedRolling`, backend `honua-yarp-rolling`). The server launches the replicas, swaps its embedded proxy at cutover and retains the prior replica through the observation window.
- **Run:** 2026-09-16T21:02:49.565808Z to 2026-09-16T21:25:09.383471Z

Full evidence: `receipt-nightly-d1fc139.json` (server journey) and `devops-live-nightly-d1fc139.jsonl` (DevOps code). Every request, response and protection transition is included.

## Recovery and fault classes

| cell | class | result | settled status | protection phase / reason codes seen |
| --- | --- | --- | --- | --- |
| `fenced-recovery` | Declared recovery: sealed grant, every fence refusal, satisfied fence, replay | **pass** | `RolledBack` | `-/-`, `observing/telemetry-evidence-pending`, `observing/-` |
| `tenant-bound-recovery` | Tenant-bound grant: undeclared binding, declared foreign tenant/actor, satisfied fence | **pass** | `RolledBack` | `-/-`, `observing/-`, `recovering/rollback-requested-out-of-band` |
| `error-rate-regression` | Injected error-rate regression after activation | **pass** | `RolledBack` | `-/-`, `observing/-`, `recovering/rollback-requested` |
| `latency-regression` | Injected p95 latency regression after activation | **pass** | `RolledBack` | `-/-`, `observing/-`, `recovering/rollback-requested` |
| `missing-telemetry` | Missing telemetry (empty evidence) past warmup + grace | **pass** | `RolledBack` | `-/-`, `observing/-`, `recovering/rollback-requested` |
| `stale-telemetry` | Stale telemetry (samples older than the freshness bound) | **pass** | `RolledBack` | `-/-`, `observing/-`, `recovering/rollback-requested` |
| `wrong-body-regression` | Wrong-body regression (golden-query wrong-result marker) | **pass** | `RolledBack` | `-/-` |
| `controller-crash` | Controller crash: server restarted inside the observation window | **pass** | `RolledBack` | `-/-`, `observing/telemetry-evidence-pending`, `observing/-`, `recovering/rollback-requested` |
| `failed-recovery` | Failed recovery: prior replica replaced out of band -> retained unavailable | **pass** | `ManualInterventionRequired` | `-/-`, `observing/-`, `unavailable/rollback-failed` |
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
| `tenant-bound-recovery` | unfenced rollback by a principal that is not the sealed actor | 403 | `recovery_fence_actor_mismatch` |
| `tenant-bound-recovery` | sealed actor declaring a foreign tenant | 403 | `recovery_fence_tenant_mismatch` |
| `tenant-bound-recovery` | another tenant's platform administrator quoting the sealed grant | 403 | `recovery_fence_actor_mismatch` |
| `failed-recovery` | stale observing-phase grant after the window became unavailable | 409 | `recovery_fence_protection_phase_mismatch` |

## Server findings filed from this run

| cell | observed | issue |
| --- | --- | --- |
| `wrong-body-private-probe` | Reconciling: Waiting for the staged candidate revision to pass the backend health gate before cutover (39s remaining before the exposure deadline). -> Reconciling: Waiting for telemetry confirmation because the golden-query correctness gate is misconfigured: Golden-query URL must be a valid HTTPS URL. H -> RolledBack: Automatic rollback requested because the staged candidate revision was not ready for cutover within the 40-second exposure deadline; the rol | honua-server#4988 |
| `platform-admin-cross-tenant` | complete fence quoting tenant-a's sealed grant, declaring tenant-b's own actor and tenant: HTTP 200 (required 403 (the grant is sealed to ops-a / tenant-a)); unfenced rollback by tenant-b's platform administrator: HTTP 200 (required 403 (the grant is sealed to ops-a / tenant-a)) | honua-server#4987 |

## DevOps code against the live server

| scenario | result | step | status | journey | blocking reasons |
| --- | --- | --- | --- | --- | --- |
| Live_DeclaredRecovery_IsFencedRecordedAndHoldsOnNextReconcile | | approved-sync | `in-progress` | Confirming service health | - |
|  | | wrong-actor | `approval-required` | Needs attention | `recovery-scope-mismatch` |
|  | | wrong-target | `approval-required` | Needs attention | `recovery-scope-mismatch` |
|  | | broadened-compensation | `approval-required` | Needs attention | `recovery-scope-mismatch` |
|  | | expired-grant | `approval-required` | Needs attention | `recovery-scope-mismatch` |
|  | | declared-recovery | `in-progress` | Updating | - |
|  | | restart-observes-recovery | `rolled-back` | Previous version restored | - |
|  | | replay | `rolled-back` | Previous version restored | - |
|  | | next-reconcile-of-quarantined-candidate | `approval-required` | Needs attention | `desired-revision-quarantined` |
| | | ledger | `restored` v2 | desired `sha256:641a1c8f73c5` | quarantined 1, operation `deploy-honua-devops-live-recovery-e2a059d33c9442778ffeb-e2f1a44d0132` |
| Live_GenericRollbackTool_StaysGated | | generic-rollback-tool | `experimental-disabled` | Checking update | `rollback-experimental-disabled` |
| Live_FailedServerRecovery_QuarantinesWithoutClaimingRestoration | | watched-sync | `indeterminate` | Needs attention | `protected-recovery-unproven` |
|  | | next-reconcile-of-quarantined-candidate | `approval-required` | Needs attention | `desired-revision-quarantined` |
| | | ledger | `rejected` v2 | desired `(none)` | quarantined 1, operation `deploy-honua-devops-live-recovery-fa8c4253f0dd4f6f8f3f5-30f5b5b0f764` |
| Live_NewerApprovedIntent_RefusesOlderRecoveryWithoutRequest | | approved-sync | `in-progress` | Confirming service health | - |
|  | | concurrent-approved-change | `failed` | Needs attention | - |
|  | | older-recovery | `approval-required` | Needs attention | `recovery-intent-superseded` |
| | | ledger | `approved` v2 | desired `sha256:fdb990da32d7` | quarantined 0, operation `deploy-honua-devops-live-recovery-5083304a45a24649bdf2c-936bc2a6a3cd` |
| Live_ServerRecoveryOnInjectedRegression_IsFoldedIntoDesiredIntent | | watched-sync | `rolled-back` | Previous version restored | - |
|  | | next-reconcile-of-quarantined-candidate | `approval-required` | Needs attention | `desired-revision-quarantined` |
| | | ledger | `restored` v2 | desired `sha256:641a1c8f73c5` | quarantined 1, operation `deploy-honua-devops-live-recovery-ed8610b907bd4413b2151-1523243b6bc3` |

All five scenarios passed (`ProtectedRecoveryLiveJourneyTests`, run with `HONUA_DEVOPS_LIVE_PROTECTED_RECOVERY=true`).

## The declared recovery DevOps sent

`POST /api/v1/admin/deploy/operations/deploy-honua-devops-live-recovery-e2a059d33c9442778ffeb-e2f1a44d0132/rollback` -> HTTP 200

```json
{
  "reason": "declared recovery: live journey",
  "targetId": "proof-selfhosted",
  "expectedCandidateRevision": "sha256:c6cfa867fb07cb99123c2f587c8190638208e1fa0a620461ed498a37739fc6f9",
  "expectedPreviousRevision": "sha256:641a1c8f73c59f5bd337ba56d1b4036b27f9b90cadf4f6710318a7039ce079ae",
  "expectedProtectionPhase": "observing",
  "grantId": "grant-e9c65580d20a89010579c431efea6e39",
  "policyDigest": "40F265FF6E39CB44F5EC660B5874553F53D5B27337D0DDAE638CFEA37494A032",
  "actor": "admin",
  "notAfter": "2026-09-16T21:37:05.6484911+00:00",
  "compensation": "restore-previous-revision"
}
```
