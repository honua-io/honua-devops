# Runtime Adapter Framework

This document defines the shared runtime adapter lifecycle for `honua-devops` work tracked by `honua-devops#15` and `honua-devops#16`.

## Purpose

Runtime adapters let the operator reason about different deployment targets through one uniform control flow.

The adapter framework exists so the operator can:

- validate target-specific prerequisites
- plan and apply infrastructure changes through `honua-terraform`
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

## Backend Consumption

Adapters consume the current platform backends this way:

- `honua-terraform`: infra planning and apply contract
- `honua-helm`: Kubernetes release packaging and rollout semantics
- `honua-server` control-plane APIs: service-state reconciliation and operational evidence
- OTEL and Honua metrics: rollout verification, drift, and incident evidence

## Current Baseline

The current repo implementation is still baseline-level:

- all six targets have concrete adapters
- adapters return structured lifecycle plans, validations, and rollback guidance
- deploy planning and preflight both resolve the real adapter set
- apply execution remains policy-gated and backend-light until deeper operator execution work lands
