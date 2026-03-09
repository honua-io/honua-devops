# Desired-State Starter Pack

This directory is the day-one starter pack referenced by `docs/operator-adoption-packaging.md`.

It gives a customer-owned control repo a concrete bootstrap layout for:

- `PlatformStack`
- `ExecutionPolicy`
- `PlatformRelease`
- `Promotion`
- `ServiceBundle`

## Intent

These files are examples, not hidden product-internal artifacts.

Use them to:

- copy the layout into a customer control repo
- rename `roads-api` to the real service name
- swap the sample runtime target to one of `azure-functions`, `lambda`, `aks`, `eks`, `ecs`, or `aca`
- set the real Terraform repository, ref, and secret references
- keep the operator in `plan` mode and `pr-first` approval until lower-environment evidence is stable

Scaffold helper:

- run `scripts/scaffold-desired-state.sh` to generate a fresh service tree with matching object references
- use the checked-in `roads-api` example as the reference shape when reviewing the scaffolded output
- run `scripts/validate-desired-state.sh` after edits to catch broken references and structural drift
- shared naming rules and allowed runtime targets live in `desired-state/conventions.env`

## Included Sample

The starter pack models one service, `roads-api`, across `dev`, `staging`, and `prod`.

Baseline assumptions:

- runtime target: `eks`
- GitOps tool: `honua-gitops`
- Terraform source: `https://github.com/honua-io/honua-terraform@main`
- default approval posture: plan-first, approval required
- break-glass policy exists separately and is not the default reference

## Directory Shape

```text
desired-state/
  platform-stacks/
  execution-policies/
  releases/
    roads-api/
  promotions/
    roads-api/
  bundles/
    roads-api/
```

## Customization Order

1. Update `platform-stacks/` to the real runtime target and secret references.
2. Update `execution-policies/` to match the customer approval and break-glass posture.
3. Update `releases/` with the real service name and revision naming.
4. Update `promotions/` to reflect the real environment flow.
5. Update `bundles/` so their object references match the renamed files.

## Current Limitation

The repo's live deploy path still emits a bootstrap `ServiceBundle`-shaped apply request while `honua-server` catches up to full multi-object reconciliation.

Practical meaning:

- these files are already the intended public control shape
- not every object is reconciled independently yet
- using this structure now avoids future contract churn later
