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
a forward change through `deploy_service_gitops`).

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

For 2026.1 honua-devops ships **no** day-2 operations tools: it is the
provisioning executor (`provision_infrastructure`, `install_handoff`,
`verify_install_handoff`) plus read-only planners and explainers. The former
ops loop (`honua_observe_diagnose_propose`, `honua_diagnose`,
`honua_explain_slow_queries`, `honua_runbook_execute`,
`honua_auto_remediation_plan`, `plan_server_upgrade`, `plan_forward_fix`,
`analyze_logs`, `analyze_metrics`, `tune_performance`, `troubleshoot_incident`)
was deleted by owner ruling on 2026-10-08. Day-2 observe/diagnose/propose is
honua-server's own `/mcp` surface (`honua_ops_*`, `honua_propose_*`), with
approval by a separate principal via `POST /api/v1/admin/proposals/{id}/approve`.

Index recommendations, capacity forecasting, incident summarization, and
migration advice are not honua-devops tools and are not on its backlog.

Edition gates that remain are on planners (see `HonuaOperationsToolkit`):
`plan_deliverable_lifecycle` requires Pro, and its cross-environment promotion
step requires Enterprise.

Write-capable paths still require execution tier, approval mode, scoped support
sessions, audit evidence, and validation checks.
