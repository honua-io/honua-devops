# Quickstart: honua-devops as an MCP server

`honua-devops --mcp` runs the operator's full tool surface as a **Model Context
Protocol stdio server**, so MCP clients (Claude Code, Codex CLI, or any other
MCP-capable host) can call the same 37 operator tools the interactive agent
uses — same handlers, same schemas, same gates, same audit trail.

In MCP mode the client LLM does the reasoning, so **no model provider
configuration is needed** (`HONUA_DEVOPS_PROVIDER`, `*_MODEL`, `*_API_KEY` for
codex/claude/local-llama are all ignored). Only the backend and runtime-control
variables matter.

## Install without a .NET SDK

Each `v*` GitHub Release contains self-contained archives and SHA-256 files for
Linux x64, macOS x64/arm64, and Windows x64. Download the archive for the host,
verify its adjacent checksum, extract it, and register the contained
`Honua.DevOps.Agent` (`.exe` on Windows) directly:

```bash
sha256sum -c honua-devops-linux-x64.tar.gz.sha256
tar -xzf honua-devops-linux-x64.tar.gz
claude mcp add honua-devops -- "$PWD/Honua.DevOps.Agent" --mcp
```

The repository also ships a multi-stage container whose final image contains a
self-contained binary and no .NET SDK:

```bash
docker build -t honua-devops:mcp .
docker run --rm -i --env-file /abs/path/honua-devops.env honua-devops:mcp
```

The container entry point already includes `--mcp`; keep stdin/stdout attached
because they carry the MCP protocol. Mount an audit directory when using a
`file://` audit target.

### Terraform runtime contract for the container

The final image is chiseled and contains only the operator binary. It
deliberately does **not** redistribute the Terraform binary and does not bundle a
honua-iac checkout, so `provision_infrastructure` is unavailable there by default
and returns a `terraform-unavailable` refusal that makes zero calls and starts no
process.

To enable provisioning from the container, mount both prerequisites and point the
operator at them:

```bash
docker run --rm -i \
  --env-file /abs/path/honua-devops.env \
  -v /usr/local/bin/terraform:/usr/local/bin/terraform:ro \
  -v /abs/path/to/honua-iac:/honua-iac:ro \
  -e PATH=/usr/local/bin:/usr/bin:/bin \
  -e HONUA_DEVOPS_TERRAFORM_LOCAL_PATH=/honua-iac \
  honua-devops:mcp
```

`HONUA_DEVOPS_TERRAFORM_BIN` overrides the executable path when Terraform is
mounted somewhere that is not on `PATH`. The honua-iac mount must contain
`infrastructure/terraform/examples/aws/{main.tf,variables.tf}`; otherwise the tool
refuses with `terraform-root-invalid`. Terraform needs writable state and plugin
directories, so mount the checkout read-write (or set `TF_DATA_DIR`) when you
intend to run `plan`/`apply` rather than only verifying the refusal.

Every other tool works in the container without these mounts.

## Register with Claude Code

Source-development path (requires the .NET 10 SDK):

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

Verify with `claude mcp list` (the server should report 37 tools), or run the
server directly and check the stderr banner:

```bash
dotnet run --project src/Honua.DevOps.Agent -- --mcp
# stderr: honua-devops MCP stdio server ready (tools=37, mode=plan, tier=plan, approval=pr-first, ...)
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

All 37 tools registered by `CapabilityToolset` (the
`ListTools_ExposesEveryOperatorToolOneToOne` test asserts this list matches the
live MCP surface 1:1):

`describe_environment`, `honua_observe_diagnose_propose`,
`find_recent_operations`, `analyze_logs`,
`analyze_metrics`, `tune_performance`, `troubleshoot_incident`,
`plan_server_upgrade`, `plan_gitops_engine`,
`generate_metadata_release_changeset`,
`explain_metadata_release_changeset`, `plan_metadata_release_gitops`,
`deploy_service_gitops`, `plan_forward_fix`,
`inspect_metadata_release`, `analyze_customer_requirements`,
`recommend_deployment_topology`, `triage_support_ticket`,
`process_pending_tickets`, `get_support_ticket_console_view`,
`honua_diagnose`, `honua_explain_slow_queries`, `honua_runbook_execute`,
`honua_auto_remediation_plan`, `plan_deliverable_lifecycle`,
`create_gitops_proposal`, `plan_gp_substrate`, `plan_gp_job_sizing`,
`plan_azure_gp_substrate`, `plan_azure_gp_job_sizing`,
`get_gitops_proposal`, `record_gitops_proposal_decision`,
`get_devops_operation_status`, `build_ai_devops_brief`,
`provision_infrastructure`, `install_handoff`,
`explain_release_package`.

> The mutating/decision tools `deploy_service_gitops` and
> `record_gitops_proposal_decision` stay behind the same execution-mode/tier and
> approval gates as the interactive agent (see "Safety model over MCP" below); in
> the default `plan` + `pr-first` posture they return an approval-required
> projection rather than acting. Recovery uses the health-gated fix-forward planner
> `plan_forward_fix` (verify health -> propose a corrected revision -> re-deploy),
> not rollback (see "Release posture" under the safety model).

`provision_infrastructure` executes Terraform with a direct argument list over
an allowlisted deployable root. For 2026.1 it accepts only `stack=aws-ecs`
(mapped to the actual `honua-iac/infrastructure/terraform/examples/aws` root)
and `size=small`. `plan` never applies. `apply` requires execute mode,
execute-lower-env or break-glass tier, direct-allowed approval, a non-production
environment, and the exact confirmation challenge returned by the tool. It
accepts only the unexpired, hash-checked saved plan produced by the earlier
`plan` call and passes that exact artifact to `terraform apply`.
`destroy` adds a BreakGlass gate. Secret variables are rejected from the tool
argument: keep them in a gitignored `terraform.tfvars` or process-scoped
`TF_VAR_*` values.

The `plan` response carries reviewable evidence, not just a change count: it runs
`terraform show` against the saved plan and returns the per-resource change roster,
an explicit list of any replacements and deletions, and a digest of the redacted
plan text. Review that roster before repeating the call with the confirmation
challenge. Terraform must be installed and on `PATH` (see "Terraform runtime
contract for the container" above); when it is not, the tool refuses with
`terraform-unavailable` before starting any process.

When `environment` is supplied without an explicit `variablesJson.name_prefix`,
the default prefix is derived from that environment (`environment=staging` plans
`honua-staging`), so a staging plan cannot silently target the development cell's
resources.

After smoke checks, `install_handoff` writes a versioned, secretless proxy
contract. It records `HONUA_BASE_URL`, `HONUA_MCP_REMOTE_URL`, and only the
secret-store reference for `HONUA_ADMIN_KEY`; resolve the key into the client
process environment at launch. The key itself is never read, returned, or
written by the handoff tool. The contract also names three required AI
capability families and fails closed when a post-install MCP `tools/list` probe
does not expose them: the default-on `honua_admin_*` family (including
honua_admin_server_status), the `analysis` profile's buffer, overlay,
statistics, reproject, join, and export tools, and the `esri-gp` profile's list,
describe, and execute-task tools. Cloud server configuration must include
`Mcp__Profiles__0=base`, `Mcp__Profiles__1=analysis`, and
`Mcp__Profiles__2=esri-gp`; admin is a default operation family rather than a
profile switch.

`honua_observe_diagnose_propose` is the primary day-2 operator entry point. It
reads honua_ops_health, honua_ops_findings, honua_alert_events,
honua_operate_events, honua_platform_release_status, and
honua_deploy_operations from the connected server over a bounded MCP session.
It caps history at 50 entries and 168 hours, caps the MCP response at 1 MiB and
each projected text value at 2,048 characters, de-duplicates deterministic
finding ids, and reports the live `supportedKinds` executor catalog. With
`proposeRecommendedAction=true`, it routes at most one supported finding through
`POST findings/{findingId}/propose` only at execution tier `propose` or higher.
That server endpoint reconstructs the intentionally hidden execution payload and
applies the existing operation gateway, autonomy, and Console approval policy;
the DevOps brain never synthesizes a payload or approves/executes the operation.

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

### Release posture: single-environment deploy + fix-forward

The MVP operate model is a **single-environment deploy** verified against health,
where an unhealthy outcome is recovered by rolling **forward** — never back:

- **Rollback is experimental and OFF by default.** The `rollback_gitops_operation`
  tool is *not advertised* (removed from the catalog), and the handler self-refuses
  with `experimental-disabled`. The code is retained but gated behind
  `HONUA_DEVOPS_EXPERIMENTAL_ROLLBACK=true`. With it enabled the catalog exposes 36
  tools (rollback in addition to `plan_forward_fix`).
- **Cross-environment promotion is experimental and OFF by default.**
  `deploy_service_gitops` refuses a `promote` action or any multi-environment
  request with `experimental-disabled`; single-environment `sync`/`apply` is
  unaffected. Retained behind `HONUA_DEVOPS_EXPERIMENTAL_CROSS_ENV_PROMOTION=true`.
- **Recovery is `plan_forward_fix`** (always advertised): it verifies live
  readiness + deploy preflight (and a prior operation's terminal/smoke evidence),
  then returns an ordered forward-convergence plan (diagnose -> propose corrected
  revision -> re-deploy through the governed create path -> re-verify), never a
  rollback. It is read-only and plan-only.

This server is part of the private operator surface (proprietary license). It
is not the public geospatial-mcp data-access surface — see
`docs/contract-boundaries.md`.
