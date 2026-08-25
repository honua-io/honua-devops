# Operator Adoption Packaging

This document captures the customer adoption path for `honua-devops#20`.

## Outcome

A customer platform team or implementation partner should be able to:

- install and run the operator from this repo without Honua-internal context
- choose a safe install mode for day-one adoption
- map the six runtime targets to the right rollout posture
- keep desired state in a customer-owned repo, starting with `honua-devops` as the control repo
- follow reference workflows for plan-only, execute-lower-env, prod promotion, rollback, incident response, and optimization

## Current Packaging Posture

Current bootstrap packaging is intentionally simple:

- source checkout of `honua-devops`
- local or CI execution through `dotnet run`
- environment-driven configuration through `.env`
- customer-owned Terraform source from `honua-iac` or an approved fork/path

Current non-goals:

- no Honua-internal deployment system is required
- no separate GitOps repo is required on day one
- no opaque hosted operator control plane is assumed

## Supported Install Modes

### Mode A: Day-One Control Repo in `honua-devops`

Recommended first install mode.

Use this when:

- the customer is starting fresh with Honua operational automation
- the team wants the shortest path to plan-only validation
- a partner is bootstrapping the first customer environments

Characteristics:

- `honua-devops` is the operator repo and the desired-state repo
- operator docs, scripts, and desired-state objects live together
- easiest mode for preflight, dry-run, and lower-environment rollout adoption

### Mode B: Customer Platform Repo Embedding

Use this when:

- the customer already has a platform operations repo
- the team wants to vendor or mirror `honua-devops` into an existing control repo
- platform governance requires one internal repo boundary

Characteristics:

- keep the operator runtime under a controlled path such as `tools/honua-devops/`
- desired state lives in the surrounding customer repo
- approval and audit policy can align with existing platform review controls

### Mode C: Split Control + Delivery Repos

Use this later, after day-one adoption is stable.

Characteristics:

- one customer-owned control repo stores desired state and workflows
- delivery repos hold application code and release artifacts
- promotion references and execution evidence connect the two repos

This mode is the target steady state for larger customers, but it should not block initial adoption.

## Day-One Repo Layout

Recommended day-one layout when starting directly from `honua-devops`:

```text
honua-devops/
  docs/
  scripts/
  desired-state/
    platform-stacks/
      dev.platformstack.yaml
      staging.platformstack.yaml
      prod.platformstack.yaml
    execution-policies/
      default.executionpolicy.yaml
      break-glass.executionpolicy.yaml
    releases/
      roads-api/
        dev.platformrelease.yaml
        staging.platformrelease.yaml
        prod.platformrelease.yaml
    promotions/
      roads-api/
        dev-to-staging.promotion.yaml
        staging-to-prod.promotion.yaml
    bundles/
      roads-api/
        dev.servicebundle.yaml
        staging.servicebundle.yaml
        prod.servicebundle.yaml
  src/
  tests/
```

Rules:

- keep desired-state objects in the customer-owned repo
- keep infra source references explicit with repo + ref fields
- start with one `ExecutionPolicy` default and one break-glass override
- promote the same revision across environments instead of editing per-environment payloads ad hoc

Starter pack:

- a concrete sample tree now lives in `desired-state/`
- the sample uses `roads-api` and `eks` as the baseline bootstrap example
- customize the names, targets, secret references, and revisions before treating it as customer source of truth
- use `scripts/scaffold-desired-state.sh` when creating a new service tree so object references stay consistent

## Runtime Target Adoption Differences

| Target | Family | Best first install mode | Release posture | Rollback posture |
| --- | --- | --- | --- | --- |
| `azure-functions` | serverless | Mode A | artifact-first, traffic shifting | slot/revision rollback |
| `lambda` | serverless | Mode A | artifact-first, traffic shifting | alias/version rollback |
| `aks` | kubernetes | Mode A or B | Helm-native rollout | Helm rollback |
| `eks` | kubernetes | Mode A or B | Helm-native rollout | Helm rollback |
| `ecs` | managed container | Mode A | service revision rollout | task-set/service rollback |
| `aca` | managed container | Mode A | revision rollout | revision rollback |

Adoption guidance:

- serverless targets are the fastest path for bootstrap because infra and release surfaces are narrower
- Kubernetes targets fit customers with an existing cluster operations team and stronger Helm/GitOps discipline
- managed-container targets sit between the two and work well when customers want app-style rollout without owning deep cluster internals

## Environment Bootstrap

1. Clone `honua-devops` into a customer-owned repo boundary.
2. Run `scripts/bootstrap-operator-env.sh` to write a local `.env.local`, or copy selected values from `.env.example` into `.env` or `.env.local`.
3. Set provider credentials and the Honua + OTEL backend URLs.
4. Point `HONUA_DEVOPS_TERRAFORM_LOCAL_PATH` at `honua-iac` or the approved customer fork.
5. Keep `HONUA_DEVOPS_EXECUTION_MODE=plan` and `HONUA_DEVOPS_APPROVAL_MODE=pr-first` for initial adoption.
6. Run preflight before any operator-assisted workflow.

Bootstrap command:

```bash
./scripts/bootstrap-operator-env.sh --provider codex
dotnet restore
dotnet build
dotnet run --project src/Honua.DevOps.Agent -- --preflight
```

Bootstrap smoke command:

```bash
./scripts/smoke-bootstrap-operator-env.sh
```

Customer bootstrap command:

```bash
./scripts/bootstrap-customer-repo.sh --service roads-api --runtime-target eks
```

That command now also writes a starter GitHub Actions workflow into the customer repo at `.github/workflows/honua-operator-validation.yml` so desired-state validation is in place from day one.
It also writes `.github/workflows/honua-operator-preflight.yml` for manual CI preflight and `bootstrap/configure-honua-operator-ci.sh` to seed the required GitHub repo vars/secrets from local env values.

Expected initial posture:

- execution mode: `plan`
- execution tier: `plan`
- approval mode: `pr-first`
- support session access: `disabled`

## Reference Workflows

### Install and Preflight

Use this first in every new customer environment:

```bash
dotnet run --project src/Honua.DevOps.Agent -- --preflight
```

Success means:

- Honua API and OTEL are reachable
- Terraform local path resolves
- configured runtime targets map to adapters

### Plan-Only Deploy

Recommended first rollout workflow.

Example:

```bash
HONUA_DEVOPS_EXECUTION_MODE=plan \
HONUA_DEVOPS_EXECUTION_TIER=plan \
dotnet run --project src/Honua.DevOps.Agent -- --provider codex --prompt \
"Use deploy_service_gitops to plan rollout of roads-api revision release/2026.03 to dev,staging with change summary 'initial customer validation'."
```

Output expectation:

- dry-run evidence
- adapter-specific validate/plan/verify guidance
- release orchestration stages
- ServiceBundle reconciliation steps

### Lower-Environment Execution

Use only after plan-only evidence is accepted.

Example:

```bash
HONUA_DEVOPS_EXECUTION_MODE=execute \
HONUA_DEVOPS_EXECUTION_TIER=execute-lower-env \
HONUA_DEVOPS_APPROVAL_MODE=direct-allowed \
dotnet run --project src/Honua.DevOps.Agent -- --provider codex --prompt \
"Use deploy_service_gitops to apply roads-api revision release/2026.03 to dev with change summary 'validated lower-env rollout'."
```

Guardrails:

- non-prod only
- requires release evidence and smoke validation
- should feed later prod promotion, not bypass it

### Production Promotion

Use only for a revision already validated outside production.

Example:

```bash
HONUA_DEVOPS_EXECUTION_MODE=execute \
HONUA_DEVOPS_EXECUTION_TIER=promote-prod \
HONUA_DEVOPS_APPROVAL_MODE=direct-allowed \
dotnet run --project src/Honua.DevOps.Agent -- --provider codex --prompt \
"Use deploy_service_gitops to promote roads-api revision release/2026.03 to prod with change summary 'promote validated staging release'."
```

Guardrails:

- `prod` promotion must use action `promote`
- lower-environment evidence should already exist
- approval record and release evidence are part of the required checks

### Rollback

Use when smoke or SLO validation fails after rollout.

Prompt shape:

```text
Use plan_server_upgrade or deploy_service_gitops to produce rollback actions, required checks, and adapter rollback guidance for the affected environment.
```

Rollback posture:

- keep requested action, effective action, and policy gate in evidence
- use runtime-adapter rollback steps rather than ad hoc target-specific commands
- open break-glass only when the normal promotion path is too slow for recovery

### Incident Response

Start in read-only tiers:

- `analyze_logs`
- `analyze_metrics`
- `troubleshoot_incident`

Use `break-glass` only when:

- the customer accepts elevated risk
- operator justification is explicit
- incident context and rollback intent are recorded
- post-action review is scheduled

### Optimization

After stability:

- use `tune_performance` for workload-specific tuning plans
- use `recommend_deployment_topology` when WAF, ingress, or edge posture should change
- capture the result as desired-state changes rather than applying one-off manual fixes

## Adoption Roles

### Customer Platform Team

Owns:

- control repo
- execution policy
- approval and audit settings
- infra and runtime target selection

### Implementation Partner

Owns:

- bootstrap and first-wave rollout execution
- documentation handoff
- customer-specific reference workflow adaptation

Should not own the long-term source of truth after handoff.

### Honua Support

Owns:

- scoped diagnostics and recovery help
- support-session work under explicit customer policy

Should not require standing write access to the customer control repo.

## Safe Adoption Sequence

1. Start with Mode A and `plan` mode.
2. Validate runtime adapters and desired-state layout in lower environments.
3. Move to `execute-lower-env` only after plan evidence is stable.
4. Introduce `promote-prod` after lower-environment evidence is reproducible.
5. Split repos later only if customer governance or scale requires it.

This keeps the first customer experience simple while preserving a clean path to stricter enterprise operating models.
