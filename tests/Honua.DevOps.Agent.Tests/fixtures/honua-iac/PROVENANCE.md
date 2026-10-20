# honua-iac fixtures

These files are copied from **honua-io/honua-iac** at commit
`d60a85282f45983905fda88c805ed404482c7ea4` (trunk, PR #161). They exist so
honua-devops can test its consumption of the governed execution substrate
without requiring a honua-iac checkout — or AWS credentials — in CI.

The exact-plan, backend identity and execution-receipt schemas retain that
provenance. The operator-contract v2 schema and all operator output documents
were refreshed from the coordinated honua-iac GP credential-free producer repair
on 2026-10-16. These are producer-owned contract fixtures, not live deployment
evidence. Refresh them from that producer rather than editing their shapes to fit
this consumer.

## `contracts/`

Verbatim copies of `infrastructure/terraform/contracts/*.schema.json`.

At runtime honua-devops reads these schemas from the **configured honua-iac
checkout**, never from a vendored copy — see `TerraformExactSubstrate`. The
copies here are test inputs only, standing in for that checkout.

## `documents/`

| File | Origin |
|---|---|
| `exact-plan-metadata.json` | Emitted by a real `scripts/terraform-exact-plan.sh` run |
| `exec-receipt.json` | Emitted by a real `scripts/terraform-exact-apply.sh` run |
| `terraform-output.json` | honua-iac `contracts/fixtures/valid-aws-ecs-small.json` |

The metadata/receipt pair was produced by one offline run of the real wrappers
(`HONUA_IAC_OFFLINE=1`, fake `terraform`, STS/state fixtures — the same harness
shape as honua-iac's own `test-terraform-exact-plan.sh`). They are therefore
genuinely joined: the receipt's `plan_metadata_digest` and `saved_plan_sha256`
are the ones that run's metadata document actually carried. For the v2 consumer
repair, only the receipt's `output_contract.digest` was rebound to the copied
v2 producer fixture's `operator_contract_digest`. This normalized offline receipt
is not claimed to be a fresh wrapper execution against a v2 deployment.

Two fields were rewritten after capture, because they recorded the ephemeral
`mktemp` directory of the capture run and would otherwise make the fixture
non-deterministic:

- `cleanup.claim_dir`
- `cleanup.saved_plan`

Because the capture ran offline, the documents are stamped
`identity.evidence_mode = "offline-test"` and `posture.release_qualified =
false`. That is correct and load-bearing: these fixtures prove the consumption
path, and they can never be mistaken for evidence about a real AWS account.

## v2 operator-contract fixtures

`operator-contract.v2.schema.json` is copied verbatim from the coordinated
honua-iac producer. `terraform-output.json` is its generated
`valid-aws-ecs-small.json`. The `valid-aws-cert-credential-free.json` and
`invalid-*` operator documents are verbatim copies of that producer's fixtures.
The negative fixtures prove old/mixed contract versions, task IAM roles, direct
artifact storage and missing credential declarations are refused. The exact producer revision is `b5c07d39a19e01883983c1af7a13ae4eb450f13a`
(honua-iac PR #253). The copied v2 schema SHA-256 is
`0fa4492b33a5df397fed3ff72aa8e2efa54f53b30981df942aaf44a98cd6bc50`.

## Lambda ordinary-root producer shape (2026-10-18)

`lambda-qualified-producer-output.json` extracts exactly six outputs explicitly
marked `sensitive:false` from actual `qualified_lambda_contract` Terraform
mock-apply `test_state` at honua-iac `a61ce04`. The full Lambda example suite passed
41/41, including all eight operator scenarios; the focused module suite passed
14/14. All four actual applied contract bundles independently passed the
producer's schema, digest and identity validator. Original test JSONL SHA-256:
`1bd07d5eb5ee7a2cdbd87b846a310e88b8bebcc5fac0a9320a70a723f9504cc3`.
Extracted bundle SHA-256:
`6174ccecedad8a4e8c541cedef49d66a9dd1b9f9b60794b89d5d478f6edea1d8`.
This is mock-provider contract evidence, not real AWS apply, migration, public
DNS or installed Honua qualification. No secret-bearing outputs were extracted.

`lambda-exact-plan-metadata.json` and `lambda-exec-receipt.json` reuse the
existing offline wrapper fixture (including explicit fake saved-plan bytes),
rebinding only the Lambda root/target, canonical plan-metadata digest and output
contract digest. The metadata SHA follows the producer's
`json.dumps(document, sort_keys=True, separators=(",", ":"))` over the document
without `plan_metadata_digest`. These rebound files are test scaffolding, not new
Terraform execution evidence. No ECS contract was relabelled as a Lambda output.
The combined test fakes provider/public probes and uses a current registered
full-ARN authority response; old producer short-name registry configuration is not
claimed to satisfy that newer server contract.
