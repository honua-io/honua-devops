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

The caller next runs the manifest-pinned component executables, still inside
the same ECS lifetime, in this exact order:

1. From the Studio checkout, run
   `npm run release:real-model-ai-arc -- prepare --execute --yes`. It consumes
   `HONUA_PLATFORM_MANIFEST`, `HONUA_AI_ARC_SDK_PLAN`,
   `HONUA_AI_ARC_CHECKPOINT`, `HONUA_AI_ARC_ENDPOINT`, and
   `HONUA_AI_ARC_PROVISION_BINDING`; uses its own scoped model/Admin credential;
   writes the sealed paused handoff to `HONUA_AI_ARC_REAL_MODEL_EVIDENCE`; and
   must exit 2 without writing `HONUA_AI_ARC_REAL_MODEL_RECEIPT`.
2. From the Console `e2e/playwright` directory, run
   `npm run receipt:console`. Its credential environment must contain only
   `HONUA_AI_ARC_CONSOLE_TOKEN`, never `HONUA_ADMIN_KEY` or `HONUA_API_KEY`. It
   consumes the checkpoint and paused Studio evidence and writes two distinct
   outputs: the three-family aggregate at `HONUA_AI_ARC_CONSOLE_RECEIPT` and the
   app-gate SDK projection at `HONUA_AI_ARC_SDK_CONSOLE_RECEIPT`.
3. Run Studio
   `npm run release:real-model-ai-arc -- resume --execute --yes` with the
   aggregate Console receipt. It replaces the paused handoff with final
   transcript-level evidence and writes the passed real-model receipt.
4. Invoke this action's `resume` phase. DevOps validates the aggregate against
   the checkpoint and real-model joins, but passes only the SDK projection to
   the deterministic SDK resume.

The model must cover Admin setup/configuration/publication, Esri GP, native
analysis, and map/app/dashboard composition and publication. It operates on and
verifies the IDs created by the deterministic pass; idempotent
create/configure calls must reconcile to those same IDs, not create a second
lookalike resource set. Its secret-free call evidence records every required
tool/resource call, response hash, successful result status, and extracted
deterministic IDs. Resume uses the same inputs plus the aggregate, projection,
and final model artifacts:

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
    console-receipt-path: out/console-aggregate.json
    sdk-console-receipt-path: out/console-sdk-projection.json
    fixture-base-url: https://<ephemeral-fixture-origin>
    db-host: <terraform-db-endpoint>
    db-connection-secret-ref: <terraform-install-contract-db-secret-arn>
    checkpoint-path: out/zero-to-map-checkpoint.json
    sdk-receipt-path: out/zero-to-map-live.json
    pre-teardown-evidence-path: out/aws-ecs-ai-delivery-arc.pre-teardown.json
```

Resume supplies `--checkpoint-digest` and only
`sdk-console-receipt-path` to the SDK, which atomically claims the checkpoint,
so replay or concurrent resume fails before adapter work. The aggregate and SDK
projection paths must be distinct, and both exact file hashes are sealed into
the pre-teardown evidence. Every
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
evidence bytes by SHA-256. Upload the evidence, SDK receipt, aggregate and SDK
Console receipts, real-model receipt, and non-secret bindings as one restricted
release artifact.
Keep the full model transcript in a separate restricted artifact if retained.
Do not upload the runner environment, Terraform state, Secrets Manager
responses, or model credentials.

## Inputs that remain external

A live run still needs an OIDC role with narrowly scoped Terraform and Secrets
Manager access, a configured public HTTPS ECS/domain path, an HTTPS fixture
origin reachable from ECS, the database connection secret ARN from the IaC
install contract, a live-model provider credential and model id, and a scoped
Console read + `admin:approve` bearer. The executable contract was audited
against Studio `c0c67666cf5345f0ae86e2644161ba15437ab571`, Console
`6c04acf6bd41f05447221ed7ef98c39bcac56f5f`, and final resealed SDK head
`1e895f886c70bc1e6e8518f07325cc34a7fed081`; the final candidate must re-pin
the exact server, SDK, Studio, Console, DevOps, and IaC commits before the
producer will run.
