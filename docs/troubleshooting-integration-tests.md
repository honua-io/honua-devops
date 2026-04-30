# Troubleshooting Integration Tests

This document defines the issue #25 real-cloud troubleshooting harness.

## Scenario Contract

Each `FaultScenario` in `src/Honua.DevOps.Agent/Operations/Troubleshooting/FaultCatalog.cs`
defines:

- scenario id and name
- fault category
- target cloud/runtime
- injection method
- expected symptoms
- expected log, metric, and health evidence
- safe remediation options
- rollback and cleanup path
- remediation scope

The catalog currently contains more than 100 scenarios and covers AWS and Azure
targets, read-only diagnosis, advisory-only remediation, and write-capable lower
environment remediation.

## Runnable Seed Set

The first real-cloud cycle is wired for these scenarios:

| Scenario | Fault | Inject | Verify Injected | Restore | Verify Restored |
| --- | --- | --- | --- | --- | --- |
| `FAULT-001` | Invalid Postgres password secret | yes | yes | yes | yes |
| `FAULT-009` | Broken OIDC issuer/audience config | yes | yes | yes | yes |
| `FAULT-010` | Bad image tag causing rollout failure | yes | yes | yes | yes |
| `FAULT-015` | Broken OTEL exporter target | yes | yes | yes | yes |
| `FAULT-016` | Manual GitOps drift | yes | yes | yes | yes |

Every script supports `FAULT_DRY_RUN=true` for contract validation without
touching cloud resources.

## Required Environment

Set these before running a real cycle:

```bash
export FAULT_ENV=staging
export FAULT_REGION=us-west-2
export FAULT_RESOURCE_PREFIX=honua
export FAULT_DRY_RUN=false
```

AWS scenarios require the AWS CLI to be authenticated. Azure scenarios require
the Azure CLI to be authenticated. Kubernetes scenarios require `kubectl` to be
pointed at the lower-environment cluster.

## Cycle Order

Run the same scenario id through:

```bash
scripts/fault-injection/FAULT-010-inject.sh
scripts/fault-injection/FAULT-010-verify-injected.sh
scripts/fault-injection/FAULT-010-restore.sh
scripts/fault-injection/FAULT-010-verify-restored.sh
```

The `ScriptBasedFaultInjector` and `FaultInjectionOrchestrator` execute that
same order from code. The blind-evaluation harness then builds an incident-only
prompt so the agent sees observed symptoms, not the scenario id or injector
implementation.

## Reporting

The harness emits:

- injection result and evidence
- injected-state verification result
- blind-evaluation prompt/result slot
- restoration result
- restored-state verification result
- total cycle duration

Diagnosis quality is represented by `DiagnosisScorecard`, which separates root
cause correctness, evidence quality, remediation safety, policy compliance,
rollback guidance, recovery verification, and final service-health outcome.
