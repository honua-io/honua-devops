# Manual Cloud Runbooks (AWS + Azure)

Runbooks for apply -> smoke -> destroy validation loops.

## Preconditions

1. `gh` is authenticated with access to `honua-io/honua-terraform`.
2. Terraform secrets are set in GitHub:

```bash
/home/makani/honua-terraform/scripts/bootstrap-gh-secrets.sh \
  --env-file /home/makani/honua-terraform/scripts/tf-secrets.local.sh
```

3. Terraform validation repo variables are set in GitHub:

```bash
source <(/home/makani/honua-terraform/scripts/tf-pass-secrets.sh export --scope publish)
/home/makani/honua-terraform/scripts/bootstrap-gh-vars.sh
```

This seeds the currently usable image refs and stack-selection vars. Today that means:

- AWS ECS, AWS Lambda, ACA, and k8s can be wired automatically.
- Azure Functions stays unset until `honua-server` has an ACR mirror or you pass `--functions-image` explicitly.

Check validation readiness:

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
source <(scripts/tf-pass-secrets.sh export)
scripts/run-aws-terraform-integration.sh \
  --stack both \
  --aot \
  --ecs-image ghcr.io/honua-io/honua-server:latest-aot \
  --serverless-image 585192672263.dkr.ecr.us-west-2.amazonaws.com/honua-server:latest-lambda-aot
```

### Azure apply/smoke/destroy

```bash
cd /home/makani/honua-terraform
source <(scripts/tf-pass-secrets.sh export)
scripts/run-azure-terraform-integration.sh \
  --stack aca \
  --aot \
  --aca-image ghcr.io/honua-io/honua-server:latest-aot
```

### Kubernetes apply/smoke/destroy

```bash
cd /home/makani/honua-terraform
source <(scripts/tf-pass-secrets.sh export)
HONUA_K8S_IMAGE=ghcr.io/honua-io/honua-server:latest-aot \
scripts/run-k8s-terraform-integration.sh
```

## Smoke Verification Contract

After endpoint discovery from Terraform outputs or workflow logs, run:

```bash
HONUA_SMOKE_BASE_URL="https://your-endpoint" \
HONUA_SMOKE_API_KEY="$HONUA_ADMIN_API_KEY" \
./scripts/smoke-contract.sh
```

The smoke contract checks:

- readiness endpoint
- liveness endpoint
- admin version endpoint when an API key is available

Local smoke verification for the contract itself:

```bash
./scripts/smoke-contract-smoke.sh
```

## Admin UI Verification

After the endpoint-level smoke passes, perform a manual control-plane verification pass:

1. Open the environment's admin UI route or companion `honua-server-admin` deployment URL.
2. Confirm the sign-in screen or authenticated shell renders without asset or API boot errors.
3. Confirm the reported environment or version matches the deployed revision.
4. Confirm the expected service or layer inventory is visible.
5. Record the URL, timestamp, operator, and any screenshots or notes in the validation log.

If the validation profile does not deploy an admin UI, record `admin-ui: not present in profile` and rely on the authenticated admin version endpoint as the minimum control-plane proof.

## Cleanup Verification

After destroy completes, verify that cleanup really happened:

### GitHub workflow path

1. Confirm the workflow summary shows destroy completed without retained resources.
2. Check workflow logs for final resource identifiers, resource group names, or stack names.
3. Verify the corresponding cloud resources are gone before ending the run.

### Local Terraform path

1. Confirm the integration script exits successfully after destroy.
2. Re-run the relevant cloud inventory query against the resource group, stack, or tagged resources captured during apply.
3. Confirm no application endpoint, public IP, load balancer, or function app remains reachable.

Example follow-up checks:

- AWS: verify the validation stack name is gone from CloudFormation or that tagged validation resources no longer appear.
- Azure: verify the validation resource group is deleted or the tagged validation resources are gone.
- Kubernetes: verify the namespace, ingress, and load balancer resources were removed.

## Cost and Cleanup Controls

- Keep `deployment_profile=ephemeral` for default validation loops.
- Keep `no_destroy=false` unless intentionally debugging.
- Use TTL defaults (`HONUA_TTL_HOURS`) for cloud resources.
- If resources are kept intentionally, schedule explicit cleanup the same day.
- Record who approved any persistent run and why the default ephemeral path was insufficient.

## Execution Record Template

Capture this for each manual run:

- date and operator
- cloud and runtime profile
- apply result
- smoke contract result
- admin UI verification result
- destroy result
- cleanup verification result
- cost-control exceptions or retained resources
