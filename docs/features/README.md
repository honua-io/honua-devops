# Honua DevOps Feature Map

`honua-devops` is private operator tooling for AI-assisted platform operations and customer deployment workflows.

## Current Capabilities

- Provider-pluggable agent runtime with Codex, Claude, and `local-llama` configuration paths.
- Plan/execute modes, execution tiers, approval modes, audit hooks, support-session posture, and break-glass controls.
- Built-in operations toolkit for diagnostics, metrics review, slow-query explanation, index recommendations, capacity forecasts, runbook execution, incident summaries, migration advice, and remediation planning.
- Honua API, OTEL, and `honua-support` backend integration, including diagnosis evidence, scorecard posting, and signed escalation webhook intake for support tickets.
- Desired-state control repo model with typed service bundles, platform stacks, promotions, execution policies, and releases.
- Customer bootstrap scripts that emit validation, preflight, and operator CI workflows.
- Runtime-adapter lifecycle for validate, plan/apply infra, plan/apply release, verify, rollback, drift, and export actual state.
- Release orchestration state machine covering preflight, backup, migration, rollout, smoke, SLO watch, promotion, and rollback.
- SLO release gates, backup/restore game days, secrets lifecycle checks, supply-chain baseline checks, fault injection, and multi-model operator evaluations.
- Console-facing AI DevOps bridge that projects stable, evidence-linked GitOps proposal, unified operation-status, advisory-brief, and read-only release-explanation contracts over honua-server deploy-control without scraping Git or CI (`create_gitops_proposal`, `get_gitops_proposal`, `get_devops_operation_status`, `build_ai_devops_brief`, `explain_release_package`). AI output stays advisory; governed submit/rollback require explicit approval. See `docs/console-ai-devops-bridge.md`.
- Portfolio execution tracker with live QGIS plugin cross-links:
  `docs/strategy/portfolio-60-day-plan.md` links the GPL plugin repo,
  public landing page, and release-owner follow-ups.

## Source Evidence

- Agent runtime and operations toolkit: `src/Honua.DevOps.Agent/`
- Console-facing AI DevOps bridge: `src/Honua.DevOps.Agent/Operations/ConsoleBridge/` and `docs/console-ai-devops-bridge.md`
- Desired-state samples: `desired-state/`
- Operator scripts: `scripts/`
- SLO and alert assets: `observability/`
- CI validation workflows: `.github/workflows/`
- Design/runbooks: `docs/`
- Strategy tracker: `docs/strategy/portfolio-60-day-plan.md`
- QGIS plugin launch script: `docs/launch/qgis-plugin-demo-script.md`

## Boundary

This repository is proprietary operator tooling. Public runtime, SDK, deployment module, and MCP surfaces stay in the public Honua repositories.
