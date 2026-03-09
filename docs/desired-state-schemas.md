# Desired-State Schemas

This document defines the first desired-state object model for `honua-devops` tracked by `honua-devops#13`.

API version for the initial contract:

- `honua.io/v1alpha1`

## Design Rules

- Objects are declarative and environment-scoped.
- Secrets are referenced, never embedded as plaintext fields.
- Promotion and rollback references are first-class fields, not ad hoc annotations.
- Defaulting should be explicit and narrow.
- Backward-compatible additions are allowed within `v1alpha1`; breaking shape changes require a new API version.

## Object Set

### `PlatformStack`

Represents the environment-level runtime target and infrastructure source.

Key fields:

- environment
- terraform repository and ref
- validated runtime targets
- secret reference keys

### `PlatformRelease`

Represents the release intent for a service in one environment.

Key fields:

- service
- environment
- revision
- requested action
- GitOps tool
- change summary

### `ServiceBundle`

Represents the GIS and service state that should exist for a published workload.

Key fields:

- service
- environment
- revision
- action
- change summary
- terraform source
- references to `PlatformStack`, `PlatformRelease`, `ExecutionPolicy`, and optional `Promotion`

### `Promotion`

Represents the movement of a validated release from one environment to the next.

Key fields:

- service
- source environment
- target environment
- revision
- execution policy reference

### `ExecutionPolicy`

Represents the guardrails that control how a change may execute.

Key fields:

- execution mode
- execution tier
- allowed environments
- required checks
- requires approval
- allows break-glass

## Relationships

Normal deployment flow:

1. `PlatformStack` defines where an environment runs.
2. `PlatformRelease` defines what revision should run there.
3. `ExecutionPolicy` defines whether that change may execute.
4. `Promotion` defines cross-environment advancement.
5. `ServiceBundle` binds those concerns to the concrete GIS/service surface.

## Defaulting

Bootstrap defaults used by the current repo implementation:

- `plan` mode defaults to execution tier `plan`
- `execute` mode defaults to execution tier `execute-lower-env`
- allowed environments default to `dev`, `staging`, `prod`
- required checks default to `manifest-diff`, `smoke-contract`, and `release-evidence`

## Bootstrap Implementation Note

The codebase now carries typed schema definitions for all five objects, but the live manifest apply path still emits a bootstrap service-focused payload while `honua-server` catches up to the full reconciliation model.

Practical effect:

- the deploy path sends a typed `ServiceBundle`-shaped spec
- that spec already contains references to `PlatformStack`, `PlatformRelease`, `Promotion`, and `ExecutionPolicy`
- full multi-object reconciliation can land later without redefining the public contract
