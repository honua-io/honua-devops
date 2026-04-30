# Epic Backlog Closure

This note records the in-repo completion surface for the remaining open epics:

- #3 GitOps platform
- #4 AI DevOps

The implementation is intentionally contract-first. The operator now exposes deterministic planning and gate outputs for each backlog area so customer-owned automation can wire real backends without changing the safety model.

## GitOps Platform

`plan_gitops_platform` covers the full GitOps operating contract:

- declarative YAML/JSON resource kinds with `apiVersion`/`kind` schema validation
- repository watching by webhook, polling, or hybrid mode
- deployed commit SHA tracking
- dev -> staging -> prod promotion gates
- drift alert routing and visual diff evidence requirements
- GitHub Actions/GitLab dry-run preview flow
- rollback by known-good commit
- audit evidence for apply, promote, rollback, and reconcile

`plan_gitops_engine` and `deploy_service_gitops` continue to provide the lower-level engine plan, diff, drift, transition, and apply paths.

## AI DevOps

The MCP-style operations tools now map the GA backlog directly:

- `honua_diagnose`
- `honua_explain_slow_queries`
- `honua_recommend_indexes`
- `honua_capacity_forecast`
- `honua_runbook_execute`
- `honua_incident_summary`
- `honua_migration_advisor`
- `honua_auto_remediation_plan`

Edition gates are explicit:

- Community: read-only diagnostics
- Pro: troubleshooting, index recommendations, capacity planning, and migration advisor
- Enterprise: runbook execution, incident response, and auto-remediation planning

Write-capable paths still require execution tier, approval mode, scoped support sessions, audit evidence, and validation checks.
