# Runtime Adapter Framework

This document defines the shared runtime adapter lifecycle for `honua-devops` work tracked by `honua-devops#15` and `honua-devops#16`.

## Purpose

Runtime adapters let the operator reason about different deployment targets through one uniform control flow.

The adapter framework exists so the operator can:

- validate target-specific prerequisites
- plan and apply infrastructure changes through `honua-iac`
- plan and apply release changes through `honua-gitops` and, where appropriate, `honua-helm`
- verify rollout health, rollback readiness, and drift status through a consistent contract
- export actual state without hiding target-specific gaps

## Shared Lifecycle

Every adapter implements the same lifecycle shape:

1. `validate`
2. `plan infra`
3. `apply infra`
4. `plan release`
5. `apply release`
6. `verify`
7. `rollback`
8. `drift`
9. `export actual state`

## Families

### Serverless

Targets:

- `azure-functions`
- `lambda`

Characteristics:

- infrastructure comes from validated Terraform modules
- release execution is artifact-oriented rather than Helm-native
- migrations should be treated as out-of-band
- traffic shifting is supported

### Kubernetes

Targets:

- `aks`
- `eks`

Characteristics:

- infrastructure comes from validated Terraform modules
- release packaging is Helm-native
- verification uses smoke and rollout health checks
- rollback is Helm-oriented

### Managed Container

Targets:

- `ecs`
- `aca`

Characteristics:

- infrastructure comes from validated Terraform modules
- release execution is service-revision oriented
- traffic shifting is supported
- rollback is revision-oriented

### Batch Job (Geoprocessing)

Targets:

- `gp`

The GP adapter cleanly separates two concerns: provisioning the durable per-environment
substrate (the adapter's job, GitOps-gated) and sizing an individual job (a runtime
SubmitJob-time concern, NOT terraform).

Characteristics:

- the adapter provisions / updates the durable PER-ENVIRONMENT GP substrate — the AWS Batch
  compute-env (Fargate-Spot, scale-to-zero), job queue, IAM roles, ECR repo, and a POOL of
  job-definition ephemeral-storage tiers (`s`/`m`/`l`/`xl` = 20/50/100/200 GiB) — from the
  honua-iac GP substrate stack (gated `enable_gp_substrate=true`)
- this runs RARELY: when GP capability is added or updated in an environment ("provision GP
  capability in env X"), through the plan-first / approval-gated path. It is NOT a per-job
  provision: running terraform per job would add 10s–min latency + state-lock contention,
  and AWS Batch `SubmitJob` already overrides vCPU/memory/timeout/retry per job with zero
  infra change
- infrastructure IS the deliverable: the durable substrate is the "release", so there is no
  separate release backend, no traffic shifting, and no out-of-band migration
- a typed `GpSubstrateConfig` (image / CPU architecture / compute-env max-vcpus / tier pool /
  ECR flag) provisions the substrate; it carries NO per-job vCPU/memory/timeout/retry — those
  are SubmitJob overrides, not terraform inputs
- the adapter binds to the substrate OUTPUTS / ARNs (`gp_job_queue_arn`,
  `gp_job_definition_arns` map `{s,m,l,xl}`, `gp_compute_environment_arn`, `gp_job_role_arn`,
  `gp_execution_role_arn`, `gp_worker_gdal_repository_url`), never to input-variable names —
  the old `gp_batch_*` variable-name coupling was brittle and backwards
- per-job sizing is a pure runtime hint (`GpResourceProfile.ToSizingHint()`): given a job's
  ephemeral-storage need, it selects a job-definition tier (`<=20→s`, `<=50→m`, `<=100→l`,
  `<=200→xl`; above 200 GiB is an error) and produces the `SubmitJob` overrides (loose
  `batch.*` params the server consumes). GPU is an advisory note (the default Fargate-Spot
  substrate has no GPU tiers; GPU needs an opt-in GPU compute-env), not a hard reject
- surfaced as the plan-first, advisory `plan_gp_substrate` tool (substrate provisioning) plus
  the pure `plan_gp_job_sizing` planning aid (tier-select + overrides; no terraform); the
  real `terraform apply` stays behind the same execution/approval gates as the other adapters
- rollback is a terraform-state revert of the substrate stack (re-apply the prior substrate
  config)

## Backend Consumption

Adapters consume the current platform backends this way:

- `honua-iac`: infra planning and apply contract
- `honua-helm`: Kubernetes release packaging and rollout semantics
- `honua-server` control-plane APIs: service-state reconciliation and operational evidence
- OTEL and Honua metrics: rollout verification, drift, and incident evidence

## Current Baseline

The current repo implementation is still baseline-level:

- all seven targets have concrete adapters (six service targets + the `gp` per-env Batch substrate target)
- adapters return structured lifecycle plans, validations, and rollback guidance
- deploy planning and preflight both resolve the real adapter set
- apply execution remains policy-gated and backend-light until deeper operator execution work lands
