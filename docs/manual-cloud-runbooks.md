# Manual Cloud Runbooks (AWS + Azure)

Runbooks for apply -> smoke -> destroy validation loops.

## Preconditions

1. `gh` is authenticated with access to `honua-io/honua-terraform`.
2. Terraform secrets are set in GitHub:

```bash
/home/makani/honua-terraform/scripts/bootstrap-gh-secrets.sh \
  --env-file /home/makani/honua-terraform/scripts/tf-secrets.local.sh
```

3. Optional full-coverage image secrets are configured:
   `HONUA_ACA_IMAGE`, `HONUA_FUNCTIONS_IMAGE`, `HONUA_AWS_ECS_IMAGE`, `HONUA_AWS_SERVERLESS_PREVIOUS_IMAGE`, `HONUA_K8S_IMAGE`, etc.

Check secret readiness:

```bash
./scripts/check-terraform-secrets.sh
```

## Option A: Dispatch GitHub manual validation workflow (recommended)

AWS + Azure combined ephemeral validation:

```bash
./scripts/dispatch-terraform-validation.sh \
  --cloud both \
  --profile ephemeral \
  --run-live true \
  --run-k8s true \
  --run-aks false \
  --run-eks false \
  --no-destroy false
```

Azure-only:

```bash
./scripts/dispatch-terraform-validation.sh --cloud azure --profile ephemeral
```

AWS-only:

```bash
./scripts/dispatch-terraform-validation.sh --cloud aws --profile ephemeral
```

Notes:
- `ephemeral` + `--no-destroy false` is the default launch-safe posture (auto-cleanup).
- `persistent` mode requires manual approval and explicit `APPROVED` confirmation.

## Option B: Local script execution from honua-terraform

### AWS apply/smoke/destroy

```bash
cd /home/makani/honua-terraform
source scripts/tf-secrets.local.sh
scripts/run-aws-terraform-integration.sh --stack both --aot
```

### Azure apply/smoke/destroy

```bash
cd /home/makani/honua-terraform
source scripts/tf-secrets.local.sh
scripts/run-azure-terraform-integration.sh --stack both --aot
```

### Kubernetes apply/smoke/destroy

```bash
cd /home/makani/honua-terraform
source scripts/tf-secrets.local.sh
scripts/run-k8s-terraform-integration.sh
```

## Smoke Verification Contract

After endpoint discovery from Terraform outputs or workflow logs, run:

```bash
HONUA_SMOKE_BASE_URL="https://your-endpoint" \
HONUA_SMOKE_API_KEY="$HONUA_ADMIN_API_KEY" \
./scripts/smoke-contract.sh
```

## Cost and Cleanup Controls

- Keep `deployment_profile=ephemeral` for default validation loops.
- Keep `no_destroy=false` unless intentionally debugging.
- Use TTL defaults (`HONUA_TTL_HOURS`) for cloud resources.
- If resources are kept intentionally, schedule explicit cleanup the same day.
