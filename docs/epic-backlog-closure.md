# Epic Backlog Closure

This note records the in-repo completion surface for the epics closed against
this repository:

- #3 GitOps platform
- #4 AI DevOps

The implementation is intentionally contract-first. The operator exposes
deterministic planning and gate outputs for each backlog area so customer-owned
automation can wire real backends without changing the safety model.

> **Tool names below are verified against
> `src/Honua.DevOps.Agent/Operations/CapabilityToolset.cs`.** That file is the
> single authority for the shipped tool surface; anything not registered there
> is backlog, not capability.

## GitOps Platform

The shipped GitOps tools are `plan_gitops_engine` (snapshot-only engine
planning), `deploy_service_gitops` (plan/diff/drift/transition and the governed
apply path), `create_gitops_proposal` / `get_gitops_proposal` /
`record_gitops_proposal_decision` (Console proposal bridge), and
`rollback_gitops_operation` (registered only when rollback is explicitly
enabled — see `OperationRuntime.RollbackEnabled`; the default recovery path is
`plan_forward_fix`).

Covered today by those tools:

- declarative YAML/JSON resource kinds with `apiVersion`/`kind` schema validation
- deployed commit SHA tracking
- dev -> staging -> prod promotion gates
- drift evidence and visual diff evidence requirements
- audit evidence for apply, promote, rollback, and reconcile

Not shipped — backlog, not capability:

- repository watching by webhook, polling, or hybrid mode (no persistent
  reconciliation loop exists; see `docs/honua-gitops-engine.md` "Current
  Limitations")
- a GitHub Actions/GitLab dry-run preview flow driven by the operator
- a single aggregate "platform" planner tool over the above; there is no
  `plan_gitops_platform` tool and none is planned

## AI DevOps

The shipped day-2 operations tools are:

- `honua_observe_diagnose_propose` (primary day-2 loop)
- `honua_diagnose`
- `honua_explain_slow_queries`
- `honua_runbook_execute`
- `honua_auto_remediation_plan`

Index recommendations, capacity forecasting, incident summarization, and
migration advice are **not** separate tools. Where they are covered at all today
they are outputs of the tools above (for example, `honua_explain_slow_queries`
returns index-related remediation, and `honua_diagnose` returns prioritized
findings). There are no `honua_recommend_indexes`, `honua_capacity_forecast`,
`honua_incident_summary`, or `honua_migration_advisor` tools, and none are on
the near-term backlog. The closest active work is
honua-io/honua-devops#156 (remediation actuator vocabulary).

Edition gates are explicit (see `HonuaOperationsToolkit`):

- Community: read-only health diagnostics
- Pro: troubleshooting, tuning, capacity, and migration planning
- Enterprise: runbook execution, incident response, and auto-remediation planning

Write-capable paths still require execution tier, approval mode, scoped support
sessions, audit evidence, and validation checks.
