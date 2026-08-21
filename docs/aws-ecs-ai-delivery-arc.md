# AWS ECS AI delivery-arc producer

The reusable action at
`.github/actions/aws-ecs-ai-delivery-arc/action.yml` is the Honua DevOps
producer for the 2026.1 cloud release gate. It runs inside the existing
`honua-release` ECS provision/readiness/teardown lifetime. It does not run
Terraform and must not be moved into a second cloud workflow.

The caller checks out the exact SDK SHA from `platform-manifest.yaml`, prepares
the secretless `install_handoff` output, and writes a pre-teardown provision
binding that validates against
`contracts/aws-ecs-provision-binding.schema.json`. The binding joins the exact
manifest digest and release id to the installed server image digest,
honua-server/devops/iac SHAs, HTTPS endpoint, admin secret reference, Terraform
plan/apply evidence, readiness, and the MCP handoff. It contains no credential
material.

## Invocation inside the ECS lifetime

Use the action by the exact manifest-pinned `honua-devops` SHA. The first pass
runs the manifest-pinned SDK driver through the publication proposals and stops
at the Console gate:

```yaml
- id: arc-prepare
  uses: honua-io/honua-devops/.github/actions/aws-ecs-ai-delivery-arc@<manifest-devops-sha>
  with:
    phase: prepare
    producer-sha: <manifest-devops-sha>
    manifest-path: platform-manifest.yaml
    sdk-root: _honua-sdk-js
    handoff-path: out/handoff/honua-mcp-proxy.handoff.json
    provision-binding-path: out/aws-ecs-provision-binding.json
    fixture-base-url: https://<ephemeral-fixture-origin>
    db-host: <terraform-db-endpoint>
    db-connection-secret-ref: <terraform-install-contract-db-secret-arn>
    checkpoint-path: out/zero-to-map-checkpoint.json
    sdk-receipt-path: out/zero-to-map-paused.json
```

The action resolves only the handoff's scoped Secrets Manager admin ARN and
places the result in the SDK child environment as `HONUA_ADMIN_KEY` and
`HONUA_API_KEY`. The provisioned database connection secret is resolved in the
same child job, checked against the expected host/port/database/user, and its
password is passed to the SDK with `--var-env`; no secret is placed on argv. An
already-populated database password environment variable remains supported for
non-IaC callers. The SDK checkpoint contains captured identifiers and a resolved
Console receipt request, never the database password or authentication
environment.

The manifest-pinned Studio runner also drives a real model, with natural
language, against this same endpoint and the captured runtime identifiers. It
must cover Admin setup/configuration/publication, Esri GP, native analysis, and
map/app/dashboard composition and publication. The Console candidate producer
then consumes the checkpoint's `consoleReceiptRequest`, inspects and approves
the exact map, app, and dashboard proposals, verifies audit/recovery, and writes
the passed Console receipt. Seal the model transcript hashes and exact
deterministic/Console identity joins as
`honua.aws-ecs.real-model-ai-arc/v1`; do not serialize the transcript or model
credential. The model operates on and verifies the IDs created by the
deterministic pass; idempotent create/configure calls must reconcile to those
same IDs, not create a second lookalike resource set. Its secret-free call
evidence records every required tool/resource call, response hash, successful
result status, and extracted deterministic IDs. Resume uses the same inputs
plus both receipts:

```yaml
- id: arc-resume
  uses: honua-io/honua-devops/.github/actions/aws-ecs-ai-delivery-arc@<manifest-devops-sha>
  with:
    phase: resume
    producer-sha: <manifest-devops-sha>
    manifest-path: platform-manifest.yaml
    sdk-root: _honua-sdk-js
    handoff-path: out/handoff/honua-mcp-proxy.handoff.json
    provision-binding-path: out/aws-ecs-provision-binding.json
    real-model-receipt-path: out/aws-ecs-real-model-ai-arc.json
    real-model-evidence-path: out/aws-ecs-real-model-ai-arc.evidence.json
    console-receipt-path: out/console-approval.json
    fixture-base-url: https://<ephemeral-fixture-origin>
    db-host: <terraform-db-endpoint>
    db-connection-secret-ref: <terraform-install-contract-db-secret-arn>
    checkpoint-path: out/zero-to-map-checkpoint.json
    sdk-receipt-path: out/zero-to-map-live.json
    pre-teardown-evidence-path: out/aws-ecs-ai-delivery-arc.pre-teardown.json
```

Resume supplies `--checkpoint-digest` and the SDK atomically claims the
checkpoint, so replay or concurrent resume fails before adapter work. Every
action must be `passed` with live evidence. Contract, skipped, queued,
approval-required, pre-approval URL, missing map/app/dashboard publication
evidence, model transcript without tool use, or a model receipt not joined to
the deterministic IDs is a hard refusal. The real-model receipt validates
against `contracts/aws-ecs-real-model-ai-arc.schema.json` and is a separate
gate from the deterministic SDK receipt. Its local evidence bytes validate
against `contracts/aws-ecs-real-model-ai-arc-evidence.schema.json` and must hash
exactly to the receipt's `evidence.sha256`.

## Finalization after teardown

The caller always executes its existing teardown in `finally`, then writes a
candidate-bound teardown record matching
`contracts/aws-ecs-teardown-evidence.schema.json`. A failed or unverified
cleanup must produce no passed release receipt. A successful cleanup is sealed
with:

```yaml
- id: arc-finalize
  uses: honua-io/honua-devops/.github/actions/aws-ecs-ai-delivery-arc@<manifest-devops-sha>
  with:
    phase: finalize
    producer-sha: <manifest-devops-sha>
    manifest-path: platform-manifest.yaml
    pre-teardown-evidence-path: out/aws-ecs-ai-delivery-arc.pre-teardown.json
    teardown-evidence-path: out/aws-ecs-teardown.json
    evidence-url: ${{ github.server_url }}/${{ github.repository }}/actions/runs/${{ github.run_id }}
    final-evidence-path: out/aws-ecs-ai-delivery-arc.evidence.json
    provision-receipt-path: out/aws-ecs-provision.json
    arc-receipt-path: out/aws-ecs-ai-delivery-arc.json
```

The final two receipts use `honua.release.evidence-receipt/v1`, carry the exact
component SHAs and manifest SHA-256 identity, and point to the uploaded final
evidence bytes by SHA-256. Upload the evidence, SDK receipt, Console receipt,
real-model receipt, and non-secret bindings as one restricted release artifact.
Keep the full model transcript in a separate restricted artifact if retained.
Do not upload the runner environment, Terraform state, Secrets Manager
responses, or model credentials.

## Inputs that remain external

A live run still needs an OIDC role with narrowly scoped Terraform and
Secrets Manager access, a configured public HTTPS ECS/domain path, an HTTPS
fixture origin reachable from ECS, the database connection secret ARN from the
IaC install contract, a live-model provider credential and model id, a
manifest-pinned Studio full-arc model runner, and the focused Console approval
producer. The platform manifest must be re-pinned to the exact server, SDK,
Studio, Console, DevOps, and IaC commits before the producer will run.
