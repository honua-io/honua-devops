# WS8 provisioning acceptance evidence

Issue: [honua-devops#147](https://github.com/honua-io/honua-devops/issues/147).

The release promise is governed AWS ECS installation: the deployment, approval,
remote state, execution identity, and verified Admin handoff must describe the
same approved operation. A schema-valid execution receipt that echoes the plan
digest while substituting those facts breaks that promise.

DevOps now checks the execution receipt against the saved plan before recording
successful provisioning state. The comparison covers both approval/plan digests,
the saved-plan hash, action, actor, target, candidate, backend identity, execution
role and issuer, workload identity contract, prior state, and teardown root.
The operator-contract digest read after apply must match the execution receipt.
Mismatch responses retain the attempted mutation's backend step and cannot
authorize an install handoff, including after a toolkit restart.

## Repeatable local verification

```bash
dotnet test tests/Honua.DevOps.Agent.Tests/Honua.DevOps.Agent.Tests.csproj \
  --configuration Release \
  --filter 'FullyQualifiedName~Provisioning|FullyQualifiedName~TerraformExactSubstrate|FullyQualifiedName~ApprovalReceiptSigning'
dotnet test tests/Honua.DevOps.Agent.Tests/Honua.DevOps.Agent.Tests.csproj \
  --configuration Release
./scripts/smoke-provisioning-contracts.sh
```

`ProvisioningReceiptBindingTests` changes individual fields in captured
honua-iac offline-wrapper documents while preserving schema validity and, except
for the dedicated digest case, the approved metadata digest. The expected
rejection is specified independently of the response. It asserts the mismatched
field, one attempted mutation, no successful lineage, no handoff directory after
restart, no second plan, and no replayed mutation.

The approval cases compute the expected saved-plan SHA-256 from known fixture
bytes and sign the documented field sequence with `HMACSHA256` independently of
the production canonicalization/signature provider. An expired, future,
rejected, unknown-issuer, invalid-signature, or changed-plan request must start
zero additional processes. A valid approval must consume the saved plan once.
Destroy cases exercise the BreakGlass path, exact saved-plan reuse, post-action
review instruction, action substitution, and refusal on restart/replay.

The process seam uses captured offline substrate documents. These tests do not
run Terraform against AWS, exercise live KMS permissions, or qualify a published
proxy/candidate. They make no live-execution claim.

## Acceptance disposition

| Issue requirement | Evidence and remaining work |
|---|---|
| Plan/apply identity and secured AWS state | Existing substrate consumption plus receipt-binding regressions. Empty-account → healthy Honua still needs a live disposable AWS cell. |
| Trusted, exact, unexpired, single-use approval | Approval and receipt-binding tests cover missing/mismatched/expired/rejected/untrusted/signature failures and replay. Production KMS principal separation still needs live evidence. |
| BreakGlass destroy and post-action review | Tier refusal test plus approved-destroy/replay tests. The completed live post-action review must accompany the disposable-cell run. |
| Handoff emission is written/unverified | `TerraformProvisioningTests` exercises contract-derived handoff generation without reporting readiness. |
| Exact pinned proxy proves health/auth/MCP/Admin | Real verifier implementation exists; real verifier regression work is tracked by #182. Exact published candidate/proxy execution is not supplied by fixture tests. |
| Partial verification cannot produce ready binding | Failed-probe tests plus receipt-substitution tests reject success and handoff authority. Successful fixture binding tests assert state serials, role, endpoint, backend and teardown values. |
| Secretless artifacts and process arguments | Allowlisted-input and handoff tests cover the local contract. Live evidence must also be inspected for credential disclosure. |
| Retry/restart/concurrent claim: at most one mutation | Existing concurrent-claim test plus apply/destroy restart and replay regressions. |

The pre-cut disposable AWS apply → verified handoff → smoke → destroy proof
remains required. This lane has no configured short-lived execution role,
remote-state cell, trusted KMS approval issuer, or pinned server/proxy inputs.
This product lane is not authorized to write AWS resources. No cloud mutation was attempted.

Only the exact-candidate proxy verification and join into honua-release#129's
candidate receipt are released to candidate qualification: the immutable
candidate and its manifest-pinned published proxy must exist first. The live
pre-cut AWS proof is not released on that basis. Issue #147 remains open until
its outstanding execution evidence is supplied; local green tests do not close it.

## 2026.1-rc.2 ECS-small disposition (2026-10-31)

The product executor is implemented. `ProvisioningStackCatalog` maps the public
`aws-ecs` stack to `infrastructure/terraform/examples/aws`; that root instantiates
`modules/aws-ecs`. `ProvisionInfrastructureCoreAsync` invokes
`scripts/terraform-exact-plan.sh --root <that root> --plan-out <saved plan>
--metadata-out <metadata> --var-file <validated inputs>`. It never builds a
second Terraform plan at apply. `ApplySavedPlanAsync` invokes
`scripts/terraform-exact-apply.sh --plan <same saved plan> --metadata <same
metadata> --approved-digest <reviewed metadata digest>`, with
`HONUA_IAC_REQUIRE_APPROVAL=1`. The honua-iac wrappers own actual Terraform
init/plan/apply and the independent backend/state/credential refusal matrix.
The public tool, not the release harness, owns this execution path.

ECS-small can express Redis off and a customer hostname using managed
Route53/ACM or an existing certificate. Its topology tuple and exact action list
come from the administrator's topology-aware `deploy_identity_contract` in
honua-iac #349. The operator requires that role's assumed session, binds the
contract hash into the saved-plan input digest, and retains the exact contract
under a content-addressed `deploy-identity-contract` evidence reference. Other
small-preset guards and the separate trusted signed approval stay in force.

Lineage and cleanup are implemented, including restart and failure paths:

- `TerraformProvisioningLineageTests` checks keyed plans, crash reservations,
  exact retained evidence, audit serialization, restart/replay, and verified
  handoff reuse. The retained contract remains available after plan cleanup.
- `ProvisioningCleanupRecoveryTests` executes owned local wrapper/process
  fixtures for partial failure, cancellation, live-child custody, unknown state,
  and fresh separately approved destroy recovery. Cleanup inspection never turns
  an indeterminate mutation into a successful deletion or reuses a spent plan.
- `TerraformProvisioningTests.EcsReleaseScope` joins Redis-off/custom-domain
  planning to exact saved-plan apply, the approved digest, retained contract and
  approval bytes, restart without a second mutation, and BreakGlass destroy.
- Existing receipt-binding, approval issuer, contract-schema and handoff tests
  cover substitution, expiry, replay, partial verification and secretless output.

These are offline product and process tests. They do not prove a live AWS
installation, role sufficiency, KMS principal separation, endpoint health,
cloud cleanup, or recovery qualification. #147 remains open for the authorized
release session's disposable-cell apply → exact published proxy handoff → smoke
→ separately approved destroy, joined to the release receipt. Retain the actual
state lineage/serial, target, candidate, assumed role, approval, execution and
cleanup evidence. No AWS writes were performed by this product lane.

EKS and Lambda remain unqualified for the 2026.1 first-install entry.
`install_honua` returns typed `install-target-unqualified` before any process;
EKS lacks live install qualification, and Lambda lacks qualified first-install
DB initialization. Existing host-authorized preview planners/observers are not
qualification or a promise of unattended first install. This lane adds no EKS or
Lambda execution support and does not infer it from ECS evidence.

The release session must publish a new honua-devops MCP release after the rc.2
fix PRs merge, then re-pin the setup binary/archive checksum or container digest
and a coordinated immutable honua-iac revision containing #349. v2026.1.1 contains
`install_honua` but predates these ECS topology and deploy-identity changes.
