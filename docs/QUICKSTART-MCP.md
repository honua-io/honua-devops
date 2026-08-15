# Quickstart: honua-devops as an MCP server

`honua-devops --mcp` runs the operator's full tool surface as a **Model Context
Protocol stdio server**, so MCP clients (Claude Code, Codex CLI, or any other
MCP-capable host) can call the same 24 operator tools the interactive agent
uses — same handlers, same schemas, same gates, same audit trail.

In MCP mode the client LLM does the reasoning, so **no model provider
configuration is needed** (`HONUA_DEVOPS_PROVIDER`, `*_MODEL`, `*_API_KEY` for
codex/claude/local-llama are all ignored). Only the backend and runtime-control
variables matter.

## Register with Claude Code

Quick start (runs from source; requires the .NET 10 SDK):

```bash
claude mcp add honua-devops -- dotnet run --project /abs/path/to/honua-devops/src/Honua.DevOps.Agent -- --mcp
```

Recommended for daily use — publish once so startup is fast and stdout is never
polluted by restore/build output:

```bash
dotnet publish src/Honua.DevOps.Agent -c Release -o artifacts/mcp
claude mcp add honua-devops -- /abs/path/to/honua-devops/artifacts/mcp/Honua.DevOps.Agent --mcp
```

(On Windows the published binary is `Honua.DevOps.Agent.exe`.)

Pass backend configuration with `--env`, or rely on the working directory's
`.env` / `.env.local` (the server auto-loads both; process env vars win):

```bash
claude mcp add honua-devops \
  --env HONUA_DEVOPS_HONUA_API_BASE_URL=http://localhost:8080 \
  --env HONUA_DEVOPS_HONUA_API_KEY=<admin-api-key> \
  --env HONUA_DEVOPS_OTEL_BASE_URL=http://localhost:4318 \
  --env HONUA_DEVOPS_AUDIT_HOOK_TARGET=file:///abs/path/honua-devops-audit.jsonl \
  -- /abs/path/to/honua-devops/artifacts/mcp/Honua.DevOps.Agent --mcp
```

Verify with `claude mcp list` (the server should report 24 tools), or run the
server directly and check the stderr banner:

```bash
dotnet run --project src/Honua.DevOps.Agent -- --mcp
# stderr: honua-devops MCP stdio server ready (tools=24, mode=plan, tier=plan, approval=pr-first, ...)
```

## Register with Codex CLI

```bash
codex mcp add honua-devops -- dotnet run --project /abs/path/to/honua-devops/src/Honua.DevOps.Agent -- --mcp
```

or in `~/.codex/config.toml`:

```toml
[mcp_servers.honua-devops]
command = "dotnet"
args = ["run", "--project", "/abs/path/to/honua-devops/src/Honua.DevOps.Agent", "--", "--mcp"]

[mcp_servers.honua-devops.env]
HONUA_DEVOPS_HONUA_API_BASE_URL = "http://localhost:8080"
HONUA_DEVOPS_HONUA_API_KEY = "<admin-api-key>"
HONUA_DEVOPS_OTEL_BASE_URL = "http://localhost:4318"
HONUA_DEVOPS_AUDIT_HOOK_TARGET = "file:///abs/path/honua-devops-audit.jsonl"
```

## Required / useful environment

Backends (same variables as every other `honua-devops` mode — see README
"Backend Integration" for the full list):

| Variable | Purpose |
| --- | --- |
| `HONUA_DEVOPS_HONUA_API_BASE_URL` | Honua API (readiness, admin, metrics, manifest, deploy-control). Default `http://localhost:8080`. |
| `HONUA_DEVOPS_HONUA_API_KEY` | Sent as `X-API-Key` for Honua admin/metrics contracts. |
| `HONUA_DEVOPS_OTEL_BASE_URL` | OTEL log/metric queries. Default `http://localhost:4318`. |
| `HONUA_DEVOPS_OTEL_API_KEY` | Optional OTEL bearer token. |
| `HONUA_DEVOPS_SUPPORT_API_BASE_URL` | Optional; enables `process_pending_tickets`. |
| `HONUA_DEVOPS_DEPLOY_TARGET_ID` | Optional; without it `create_gitops_proposal` returns a blocked `target-unconfigured` projection. |

Runtime controls (defaults shown are the safe defaults):

| Variable | Default |
| --- | --- |
| `HONUA_DEVOPS_EXECUTION_MODE` | `plan` |
| `HONUA_DEVOPS_EXECUTION_TIER` | `plan` |
| `HONUA_DEVOPS_APPROVAL_MODE` | `pr-first` |
| `HONUA_DEVOPS_AUDIT_HOOK_TARGET` | `stdout-evidence` (re-routed to stderr in MCP mode; see below) |

## Worked example (Claude Code)

```text
> describe my environment

[honua-devops:describe_environment]
Claude calls describe_environment, which probes readiness, edition,
capabilities, and manifest scope of the connected Honua API, then summarizes:
edition, available services, deploy targets, allowed environments.

> plan a deployment of honua to aws lambda

[honua-devops:analyze_customer_requirements → recommend_deployment_topology → deploy_service_gitops]
Claude maps the requirement to the validated `lambda` Terraform target,
recommends a topology (WAF/ingress/rate-limit posture), and produces a GitOps
deployment plan across dev/staging/prod. In the default plan mode nothing is
applied: the response is a plan with evidence, policy gates, and the approval
path (pr-first) called out.

> diagnose slow tiles

[honua-devops:honua_diagnose → honua_explain_slow_queries]
Claude runs the read-only edition-aware diagnostics over health/metrics/error
telemetry and explains slow query signatures (spatial index, cache, pool
bottlenecks) with prioritized remediation and validation checks.
```

## Exposed tools (1:1 with the interactive agent)

`describe_environment`, `find_recent_operations`, `analyze_logs`,
`analyze_metrics`, `tune_performance`, `troubleshoot_incident`,
`plan_server_upgrade`, `plan_gitops_engine`,
`generate_metadata_release_changeset`,
`explain_metadata_release_changeset`, `deploy_service_gitops`,
`analyze_customer_requirements`, `recommend_deployment_topology`,
`triage_support_ticket`, `process_pending_tickets`,
`get_support_ticket_console_view`, `honua_diagnose`,
`honua_explain_slow_queries`, `honua_runbook_execute`,
`honua_auto_remediation_plan`, `create_gitops_proposal`,
`get_gitops_proposal`, `get_devops_operation_status`,
`build_ai_devops_brief`, `explain_release_package`.

## Safety model over MCP

The MCP layer is a thin adapter over `CapabilityToolset`; **no gate is
re-implemented and none can be bypassed by the client**:

- Execution-mode/tier, approval-mode, and edition gates live inside the tool
  handlers and read the `HONUA_DEVOPS_*` environment of the server process.
  An MCP client cannot escalate them; gate posture is fixed at registration
  time by whoever configures the server.
- Defaults stay `plan` + `pr-first`: planning tools emit evidence bundles and
  never apply manifests, submit, or roll back deploy operations.
- `honua_runbook_execute` and `honua_auto_remediation_plan` keep their
  Enterprise edition gate and execute-tier gate. In the default plan tier they
  return `confirmation-required` / `runbook-plan-ready` /
  `auto-remediation-approval-required` instead of executing — the
  approval-required response, not the action.
- `create_gitops_proposal` still records proposals with
  `submitImmediately=false` and stays blocked (`target-unconfigured`) until
  `HONUA_DEVOPS_DEPLOY_TARGET_ID` is set.
- Every MCP tool call emits exactly one JSONL audit record (same schema as the
  interactive host; `Provider` is `mcp`). Because stdout carries the MCP wire
  protocol, the default `stdout-evidence` target is automatically re-routed to
  stderr (`stderr-evidence`); set
  `HONUA_DEVOPS_AUDIT_HOOK_TARGET=file:///path/audit.jsonl` to keep a journal
  that `--list-operations` / `--show-operation` and `find_recent_operations`
  can read.
- Argument redaction is unchanged: audited arguments and summaries pass
  through the same `Redaction` scrubbing.

This server is part of the private operator surface (proprietary license). It
is not the public geospatial-mcp data-access surface — see
`docs/contract-boundaries.md`.
