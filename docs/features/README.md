# Honua DevOps Feature Map

`honua-devops` is private operator tooling for AI-assisted platform operations and customer deployment workflows.

## Current Capabilities

- Provider-pluggable agent runtime with Codex, Claude, `local-llama`, and Amazon Bedrock configuration paths (`HONUA_DEVOPS_PROVIDER` = `codex` | `claude` | `local-llama` | `bedrock`).
- Plan/execute modes, execution tiers, approval modes, audit hooks, support-session posture, and break-glass controls.
- Built-in operations toolkit for log/metrics analysis (`analyze_logs`, `analyze_metrics`), performance tuning (`tune_performance`), incident troubleshooting (`troubleshoot_incident`), diagnostics (`honua_diagnose`), slow-query explanation (`honua_explain_slow_queries`), runbook execution (`honua_runbook_execute`), and remediation planning (`honua_auto_remediation_plan`). Capacity forecasting, index recommendation, incident summarization, and migration advice are *not* separate tools — see `docs/epic-backlog-closure.md`.
- Primary day-2 `honua_observe_diagnose_propose` loop over bounded honua-server MCP health/findings/alerts/timeline/platform-release/deploy evidence, with at-most-one deterministic finding-id proposal through the server-owned gateway and approval lane.
- Honua API, OTEL, and `honua-support` backend integration, including diagnosis evidence, scorecard posting, and signed escalation webhook intake for support tickets.
- Desired-state control repo model with typed service bundles, platform stacks, promotions, execution policies, and releases.
- Customer bootstrap scripts that emit validation, preflight, and operator CI workflows.
- Runtime-adapter lifecycle for validate, plan/apply infra, plan/apply release, verify, rollback, drift, and export actual state.
- Release orchestration state machine covering preflight, backup, migration, rollout, smoke, SLO watch, promotion, and rollback.
- SLO release gates, backup/restore game days, secrets lifecycle checks, supply-chain baseline checks, fault injection, and multi-model operator evaluations.
- Console-facing AI DevOps bridge that projects stable, evidence-linked GitOps proposal, unified operation-status, advisory-brief, and read-only release-explanation contracts over honua-server deploy-control without scraping Git or CI (`create_gitops_proposal`, `get_gitops_proposal`, `get_devops_operation_status`, `build_ai_devops_brief`, `explain_release_package`). AI output stays advisory; governed submit/rollback require explicit approval. See `docs/console-ai-devops-bridge.md`.

## Source Evidence

The authoritative list of shipped tools is
`src/Honua.DevOps.Agent/Operations/CapabilityToolset.cs`. If a tool name appears
in a doc but not in that file, the doc is wrong.

- Agent runtime and operations toolkit: `src/Honua.DevOps.Agent/`
- Console-facing AI DevOps bridge: `src/Honua.DevOps.Agent/Operations/ConsoleBridge/` and `docs/console-ai-devops-bridge.md`
- Desired-state samples: `desired-state/`
- Operator scripts: `scripts/`
- SLO and alert assets: `observability/`
- CI validation workflows: `.github/workflows/`
- Design/runbooks: `docs/`
- Current program: the 2026.1 terminal AI delivery arc, tracked in
  honua-io/honua-release#120. `docs/strategy/portfolio-60-day-plan.md` is a
  superseded historical snapshot (its 60-day window closed ~2026-07-20).
- QGIS plugin launch script: `docs/launch/qgis-plugin-demo-script.md`

## Boundary

This repository is proprietary operator tooling. Public runtime, SDK, deployment module, and MCP surfaces stay in the public Honua repositories.
