# Quickstart: honua-devops as an MCP server

`honua-devops --mcp` runs the operator's full tool surface as a **Model Context
Protocol stdio server**, so MCP clients (Claude Code, Codex CLI, or any other
MCP-capable host) can call the same 38 operator tools the interactive agent
uses — same handlers, same schemas, same gates, same audit trail.

In MCP mode the client LLM does the reasoning, so **no model provider
configuration is needed** (`HONUA_DEVOPS_PROVIDER`, `*_MODEL`, `*_API_KEY` for
codex/claude/local-llama are all ignored). Only the backend and runtime-control
variables matter.

## Install without a .NET SDK

Pushing a `v*` tag runs [`.github/workflows/release-mcp.yml`](../.github/workflows/release-mcp.yml),
which publishes, for that tag:

- self-contained single-file archives for `linux-x64`, `osx-x64`, `osx-arm64`,
  and `win-x64` as GitHub Release assets, each with an adjacent
  `.sha256` file in `sha256sum -c` format;
- a container image pushed to `ghcr.io/honua-io/honua-devops`, tagged with the
  release version and its commit SHA. The release also carries a
  `container-image.txt` asset recording the image digest to pin.

The archives contain one executable and no .NET runtime dependency of any kind;
the container's final layer is `runtime-deps` (native dependencies only, no SDK
and no shared framework) running as a non-root user. Nothing below needs
`dotnet` on `PATH`.

> The first release is published when the first `v*` tag is pushed. Until then,
> use the source-development path below. Substitute the tag you are installing
> for `<version>` (for example `v2026.1.0`).

### Binary

Download with the GitHub CLI (`gh auth login` first — the repository is
private, so an unauthenticated download will not resolve):

```bash
VERSION=<version>
ASSET=honua-devops-linux-x64.tar.gz   # or osx-arm64 / osx-x64 / win-x64 (.zip)

gh release download "$VERSION" \
  --repo honua-io/honua-devops \
  --pattern "$ASSET" \
  --pattern "$ASSET.sha256"

sha256sum -c "$ASSET.sha256"          # macOS: shasum -a 256 -c

mkdir -p ~/.local/share/honua-devops
tar -xzf "$ASSET" -C ~/.local/share/honua-devops

claude mcp add honua-devops -- ~/.local/share/honua-devops/Honua.DevOps.Agent --mcp
```

On Windows the archive is a `.zip` and the executable is
`Honua.DevOps.Agent.exe`.

Verify the registration reached `tools/list`:

```bash
claude mcp list                       # honua-devops should report 38 tools
~/.local/share/honua-devops/Honua.DevOps.Agent --list-tools | head -1
# honua-devops exposes 38 operator tools:
```

Upgrade by repeating the download/verify/extract over the same directory with a
new `VERSION`; the registration keeps pointing at the same path. Uninstall with
`claude mcp remove honua-devops && rm -rf ~/.local/share/honua-devops`.

### Container

Pin the digest recorded in the release's `container-image.txt` rather than a
tag:

```bash
IMAGE=ghcr.io/honua-io/honua-devops@sha256:<digest>
docker pull "$IMAGE"

claude mcp add honua-devops -- \
  docker run --rm -i --env-file /abs/path/honua-devops.env "$IMAGE"
```

The entry point already includes `--mcp`, so pass no extra arguments; `-i` is
required because stdin/stdout carry the MCP protocol. Mount a writable
directory when `HONUA_DEVOPS_AUDIT_HOOK_TARGET` uses a `file://` target.
Uninstall with `claude mcp remove honua-devops && docker image rm "$IMAGE"`.

## Register with Claude Code

Source-development path (runs from source; requires the .NET 10 SDK):

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

Verify with `claude mcp list` (the server should report 38 tools), or run the
server directly and check the stderr banner:

```bash
dotnet run --project src/Honua.DevOps.Agent -- --mcp
# stderr: honua-devops MCP stdio server ready (tools=38, mode=plan, tier=plan, approval=pr-first, ...)
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

All 38 tools registered by `CapabilityToolset` (the
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
`provision_infrastructure`, `install_handoff`, `verify_install_handoff`,
`explain_release_package`.

## Governed provisioning and verified handoff

`provision_infrastructure` returns a stable `provisioningOperationId`, exact
saved-plan SHA-256, and one-time challenge. Apply/destroy additionally requires
`approvalReceiptJson` using schema `honua.devops.provision-approval/v1`. The
receipt issuer must appear in `HONUA_DEVOPS_PROVISION_APPROVAL_ISSUER_KEYS`
(`issuer=base64-hmac-key`, semicolon-separated; keys are secret-injected, never
committed). Its base64 HMAC-SHA256 signature covers these newline-separated
UTF-8 fields in order:

`schemaVersion`, `approvalReceiptId`, `issuer`, `keyId` (first 16 hex chars of
SHA-256 over the decoded key), `provisioningOperationId`, lowercase
`planSha256`, `action`, `stack`, `environment`, `decision`, UTC `issuedAtUtc`
and UTC `expiresAtUtc` in round-trip (`O`) format. Receipts must say `approved`,
expire within one hour, and match the exact saved plan. Missing, expired,
substituted, untrusted, malformed, or replayed receipts start no Terraform
apply process.

Handoff emission also requires immutable release inputs:

```dotenv
HONUA_DEVOPS_MCP_PROXY_PACKAGE=@honua/mcp-server@<exact-version>
HONUA_DEVOPS_MCP_PROXY_INTEGRITY=sha512-<registry-integrity>
HONUA_DEVOPS_CANDIDATE_REFERENCE=<exact-server-revision>
```

`install_handoff` consumes the exact `provisioningOperationId` and locally
persisted DevOps apply evidence. It returns `install-handoff-written`, never
ready. `verify_install_handoff` then resolves the secret reference only into
the child proxy environment, checks npm integrity, HTTPS readiness,
authenticated candidate identity, MCP initialize, paged `tools/list`, the
Admin/analysis/esri-gp roster, and `honua_admin_server_status`. Only complete
success writes `honua-install-verification.receipt.json` and the DevOps-owned
`honua-devops-aws-ecs-provision-binding.json`; partial verification writes no
ready binding. `secret://NAME`, AWS Secrets Manager references, and Azure Key
Vault references resolve through the process environment, `aws`, or `az`
respectively without placing credential material in arguments, handoff files,
receipts, or audit records.

> The mutating/decision tools `deploy_service_gitops` and
> `record_gitops_proposal_decision` stay behind the same execution-mode/tier and
> approval gates as the interactive agent (see "Safety model over MCP" below); in
> the default `plan` + `pr-first` posture they return an approval-required
> projection rather than acting. Recovery uses the health-gated fix-forward planner
> `plan_forward_fix` (verify health -> propose a corrected revision -> re-deploy),
> not rollback (see "Release posture" under the safety model).

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

Before executor discovery or proposal routing, the loop validates both required
`generatedAt` values and every required server `evidencePosture` envelope. Response
timestamps may be at most five minutes old and one minute into the future; each
source must also satisfy its server-published `maximumObservationAgeSeconds`.
Missing/malformed/future/stale timestamps, non-actionable or unverified backends,
`partialResult=true`, and non-empty `sourceErrors` return `evidence-incomplete`,
preserve bounded diagnostics, and make zero executor/proposal calls. Required MCP
transport or payload failure returns `observability-unavailable` with empty
actionable findings. `OpsLoopReport.EvidencePosture` records privacy-safe source
identity, observation time, evaluated age, completeness, and suppression reason.

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
