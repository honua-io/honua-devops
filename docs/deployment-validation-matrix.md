# Deployment Validation Matrix and Smoke Contract

This matrix defines the deployment validation contract for the current release
train, `release/2026.1` (and its corrective successor cut `release/2026.1.1`),
tracked by honua-io/honua-release#120.

> The matrix was originally written as the first-pass contract for an April 1,
> 2026 campaign gated on `release/apr-2026`. That branch and date are historical;
> the runtime/mode rows and launch classes below carried forward unchanged to the
> 2026.1 train.

## Matrix

Legend:
- `Must Pass`: launch gate for `release/2026.1`
- `Experimental`: can ship first-pass with caveats
- `Deferred`: not a launch gate

| Runtime | Mode | Validation Path | Launch Class | Expected Result |
| --- | --- | --- | --- | --- |
| AWS Lambda | AOT | `honua-iac/scripts/run-aws-terraform-integration.sh --stack serverless --aot` | Must Pass | Pass |
| AWS Lambda | JIT | `honua-iac/scripts/run-aws-terraform-integration.sh --stack serverless` | Experimental | Pass or documented caveat |
| AWS ECS | JIT | `honua-iac/scripts/run-aws-terraform-integration.sh --stack ecs` | Must Pass | Pass |
| AWS ECS | AOT | `honua-iac/scripts/run-aws-terraform-integration.sh --stack ecs --aot` | Experimental | Pass or documented caveat |
| Azure Functions | AOT | `honua-iac/scripts/run-azure-terraform-integration.sh --stack functions --aot` | Must Pass | Pass |
| Azure Functions | JIT | `honua-iac/scripts/run-azure-terraform-integration.sh --stack functions` | Experimental | Pass or documented caveat |
| Azure Container Apps | JIT | `honua-iac/scripts/run-azure-terraform-integration.sh --stack aca` | Must Pass | Pass |
| Azure Container Apps | AOT | `honua-iac/scripts/run-azure-terraform-integration.sh --stack aca --aot` | Experimental | Pass or documented caveat |
| Kubernetes (Helm) | JIT | `honua-iac/scripts/run-k8s-terraform-integration.sh` | Must Pass | Pass |
| Kubernetes (Helm) | AOT | `honua-iac/scripts/run-k8s-terraform-integration.sh --aot` | Experimental | Pass or documented caveat |

## Validation Config Profiles

### Required GitHub secrets

- `ARM_CLIENT_ID`
- `ARM_CLIENT_SECRET`
- `ARM_TENANT_ID`
- `ARM_SUBSCRIPTION_ID`
- `AWS_ACCESS_KEY_ID`
- `AWS_SECRET_ACCESS_KEY`
- `HONUA_ADMIN_PASSWORD`
- `HONUA_DB_PASSWORD`

### Required repo variables for the current stack selection

- `HONUA_AWS_ECS_IMAGE` when `HONUA_AWS_VALIDATION_STACK=ecs|both`
- `HONUA_AWS_SERVERLESS_IMAGE` when `HONUA_AWS_VALIDATION_STACK=serverless|both`
- `HONUA_ACA_IMAGE` when `HONUA_AZURE_VALIDATION_STACK=aca|both`
- `HONUA_FUNCTIONS_IMAGE` when `HONUA_AZURE_VALIDATION_STACK=functions|both`
- `HONUA_K8S_IMAGE` when you run the k8s path

With the current bootstrap helper, the practical default is:

- `HONUA_AWS_VALIDATION_STACK=both`
- `HONUA_AZURE_VALIDATION_STACK=aca`
- `HONUA_AWS_ECS_IMAGE=ghcr.io/honua-io/honua-server:latest-aot`
- `HONUA_AWS_SERVERLESS_IMAGE=<account>.dkr.ecr.<region>.amazonaws.com/honua-server:latest-lambda-aot`
- `HONUA_ACA_IMAGE=ghcr.io/honua-io/honua-server:latest-aot`
- `HONUA_K8S_IMAGE=ghcr.io/honua-io/honua-server:latest-aot`

### Optional repo variables for broader coverage

- `HONUA_ACA_PREVIOUS_IMAGE`
- `HONUA_FUNCTIONS_PREVIOUS_IMAGE`
- `HONUA_AWS_ECS_PREVIOUS_IMAGE`
- `HONUA_AWS_SERVERLESS_PREVIOUS_IMAGE`
- `HONUA_K8S_PREVIOUS_IMAGE`
- `HONUA_AWS_ECS_CANARY_IMAGE`
- `HONUA_AWS_VALIDATION_REGION`
- `HONUA_AZURE_VALIDATION_REGION`

## Smoke Contract

Use `scripts/smoke-contract.sh` for a uniform post-deploy check.

Contract:
1. Readiness endpoint must return HTTP `2xx/3xx`.
2. Liveness endpoint must return HTTP `2xx/3xx`.
3. If API key is provided, admin version endpoint must return HTTP `2xx/3xx`.
4. Output includes per-probe status and overall pass/fail exit code.

Example:

```bash
HONUA_SMOKE_BASE_URL="https://your-endpoint" \
HONUA_SMOKE_API_KEY="$HONUA_ADMIN_API_KEY" \
./scripts/smoke-contract.sh
```

Contract verification:

```bash
./scripts/smoke-contract-smoke.sh
```

## Reuse

This smoke contract is the shared endpoint-level validation step for the 2026.1 deployment campaign.

- `docs/manual-cloud-runbooks.md` uses it as the common apply -> smoke -> destroy validation step.
- Later operator rollout and desired-state flows should reuse the same contract rather than inventing a second smoke path.

## Current Verification Status

- The smoke contract is validated locally and in CI with `scripts/smoke-contract-smoke.sh`.
- The matrix above is the source of truth for the live AWS/Azure/Kubernetes validation campaign.
- Real cloud execution evidence still needs to be recorded per runtime/profile outside this repo before calling the launch matrix complete.
