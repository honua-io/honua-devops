# honua-iac fixtures

These files are copied from **honua-io/honua-iac** at commit
`d60a85282f45983905fda88c805ed404482c7ea4` (trunk, PR #161). They exist so
honua-devops can test its consumption of the governed execution substrate
without requiring a honua-iac checkout — or AWS credentials — in CI.

Nothing here is authored by honua-devops. If a test fails after a honua-iac
contract change, refresh these files rather than editing them to fit.

## `contracts/`

Verbatim copies of `infrastructure/terraform/contracts/*.schema.json`.

At runtime honua-devops reads these schemas from the **configured honua-iac
checkout**, never from a vendored copy — see `TerraformExactSubstrate`. The
copies here are test inputs only, standing in for that checkout.

## `artifacts/`

| File | Origin |
|---|---|
| `exact-plan-metadata.json` | Emitted by a real `scripts/terraform-exact-plan.sh` run |
| `exec-receipt.json` | Emitted by a real `scripts/terraform-exact-apply.sh` run |
| `terraform-output.json` | honua-iac `contracts/fixtures/valid-aws-ecs-small.json` |

The metadata/receipt pair was produced by one offline run of the real wrappers
(`HONUA_IAC_OFFLINE=1`, fake `terraform`, STS/state fixtures — the same harness
shape as honua-iac's own `test-terraform-exact-plan.sh`). They are therefore
genuinely joined: the receipt's `plan_metadata_digest` and `saved_plan_sha256`
are the ones that run's metadata document actually carried, and the receipt's
`output_contract.digest` matches `operator_contract_digest` in
`terraform-output.json`.

Two fields were rewritten after capture, because they recorded the ephemeral
`mktemp` directory of the capture run and would otherwise make the fixture
non-deterministic:

- `cleanup.claim_dir`
- `cleanup.saved_plan`

Because the capture ran offline, the documents are stamped
`identity.evidence_mode = "offline-test"` and `posture.release_qualified =
false`. That is correct and load-bearing: these fixtures prove the consumption
path, and they can never be mistaken for evidence about a real AWS account.
