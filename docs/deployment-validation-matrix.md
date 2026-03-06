# Deployment Validation Matrix and Smoke Contract

This matrix defines the first-pass deployment validation contract for April 1, 2026.

## Matrix

Legend:
- `Must Pass`: launch gate for `release/apr-2026`
- `Experimental`: can ship first-pass with caveats
- `Deferred`: not a launch gate

| Runtime | Mode | Validation Path | Launch Class | Expected Result |
| --- | --- | --- | --- | --- |
| AWS Lambda | AOT | `honua-terraform/scripts/run-aws-terraform-integration.sh --stack serverless --aot` | Must Pass | Pass |
| AWS Lambda | JIT | `honua-terraform/scripts/run-aws-terraform-integration.sh --stack serverless` | Experimental | Pass or documented caveat |
| AWS ECS | JIT | `honua-terraform/scripts/run-aws-terraform-integration.sh --stack ecs` | Must Pass | Pass |
| AWS ECS | AOT | `honua-terraform/scripts/run-aws-terraform-integration.sh --stack ecs --aot` | Experimental | Pass or documented caveat |
| Azure Functions | AOT | `honua-terraform/scripts/run-azure-terraform-integration.sh --stack functions --aot` | Must Pass | Pass |
| Azure Functions | JIT | `honua-terraform/scripts/run-azure-terraform-integration.sh --stack functions` | Experimental | Pass or documented caveat |
| Azure Container Apps | JIT | `honua-terraform/scripts/run-azure-terraform-integration.sh --stack aca` | Must Pass | Pass |
| Azure Container Apps | AOT | `honua-terraform/scripts/run-azure-terraform-integration.sh --stack aca --aot` | Experimental | Pass or documented caveat |
| Kubernetes (Helm) | JIT | `honua-terraform/scripts/run-k8s-terraform-integration.sh` | Must Pass | Pass |
| Kubernetes (Helm) | AOT | `honua-terraform/scripts/run-k8s-terraform-integration.sh --aot` | Experimental | Pass or documented caveat |

## Secret Profiles

### Required for baseline AWS + Azure live validation

- `ARM_CLIENT_ID`
- `ARM_CLIENT_SECRET`
- `ARM_TENANT_ID`
- `ARM_SUBSCRIPTION_ID`
- `AWS_ACCESS_KEY_ID`
- `AWS_SECRET_ACCESS_KEY`
- `HONUA_ADMIN_PASSWORD`
- `HONUA_DB_PASSWORD`

### Required for full cross-runtime coverage

- `HONUA_AWS_SERVERLESS_IMAGE`

### Optional but recommended for full first-pass coverage

- `AWS_SESSION_TOKEN`
- `HONUA_ACA_IMAGE`
- `HONUA_ACA_PREVIOUS_IMAGE`
- `HONUA_FUNCTIONS_IMAGE`
- `HONUA_FUNCTIONS_PREVIOUS_IMAGE`
- `HONUA_AWS_ECS_IMAGE`
- `HONUA_AWS_ECS_PREVIOUS_IMAGE`
- `HONUA_AWS_SERVERLESS_PREVIOUS_IMAGE`
- `HONUA_K8S_IMAGE`
- `HONUA_K8S_PREVIOUS_IMAGE`

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
