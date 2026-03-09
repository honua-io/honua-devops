# Desired-State Scaffold Script

This document covers the bootstrap scaffold helper added for the `honua-devops#20` adoption work.

Script:

- `scripts/scaffold-desired-state.sh`

## Purpose

The starter pack in `desired-state/` shows the intended control-repo shape, but customers still need a fast way to create a real service tree.

The scaffold script generates:

- `PlatformStack` files for each environment
- shared `ExecutionPolicy` files
- per-environment `PlatformRelease` files
- chained `Promotion` files between adjacent environments
- per-environment `ServiceBundle` files with matching object references

## Example

```bash
./scripts/scaffold-desired-state.sh \
  --service parcels-api \
  --runtime-target aks \
  --revision release/2026.04 \
  --terraform-repository https://github.com/acme/platform-terraform \
  --terraform-ref main \
  --secret-ref ACME_HONUA_ADMIN_API_KEY \
  --secret-ref ACME_DB_CONNECTION
```

## Output Shape

Default output root:

- `desired-state/`

Generated files:

- `desired-state/platform-stacks/*.platformstack.yaml`
- `desired-state/execution-policies/*.executionpolicy.yaml`
- `desired-state/releases/<service>/*.platformrelease.yaml`
- `desired-state/promotions/<service>/*.promotion.yaml`
- `desired-state/bundles/<service>/*.servicebundle.yaml`

## Notes

- Existing files are not overwritten unless `--force` is set.
- The default environment chain is `dev,staging,prod`.
- The last environment in the chain uses `promote`; earlier environments use `sync`.
- Promotion objects are created between each adjacent environment pair.
- The scaffold copies `desired-state/conventions.env` into the generated root so validation rules travel with the generated tree.
- Run `scripts/validate-desired-state.sh` after scaffolding or manual edits.
- Run `scripts/smoke-desired-state-scaffold.sh` to exercise the full scaffold -> validate path in one command.
