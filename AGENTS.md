# AGENTS.md

## Overview

`honua-devops` is private, proprietary operator tooling: an AI DevOps operator
and solution architect for Honua. It is a .NET 10 console host that exposes
operator-grade capabilities as agent function tools — log/metrics analysis,
troubleshooting and tuning, server upgrade planning, Honua-native GitOps
deployment planning, customer-requirement analysis, support-ticket triage,
topology recommendations, and a Console-facing AI DevOps bridge.

It is NOT part of Honua's open-core runtime promise. Public surfaces live in
`honua-server`, the SDK/mobile repos, and the base MCP data-access surface.
See `LICENSE` (proprietary).

The agent defaults to safe behavior: `plan` execution mode with `pr-first`
approval. It plans and emits evidence bundles; it does not apply manifests,
submit, or roll back deploy operations on its own.

## Tech Stack

- .NET 10 (`net10.0`), C# with `ImplicitUsings` and `Nullable` enabled.
- Microsoft Agent Framework: `Microsoft.Agents.AI.OpenAI` 1.0.0-rc3, `OpenAI` 2.8.0.
- MCP server mode: `ModelContextProtocol.Core` 1.4.0 (official MCP C# SDK) hosting
  the toolset over stdio via `--mcp`; `Microsoft.Extensions.AI` is pinned to match
  the abstractions floor it pulls in.
- Provider-pluggable AI runtime. Three OpenAI-compatible adapters — `codex`,
  `claude`, `local-llama` (NVIDIA NIM / vLLM / Ollama / TGI) — plus `bedrock`,
  a native Amazon Bedrock Converse adapter (`BedrockChatClientAdapter`, via
  `AWSSDK.BedrockRuntime`). Default `codex`.
- Tests: xUnit 2.9.3, `Microsoft.NET.Test.Sdk` 17.14.1, `coverlet.collector`,
  `YamlDotNet` 16.3.0.
- Build uses NuGet lock files (`RestorePackagesWithLockFile=true`, locked mode
  when `packages.lock.json` exists — see `Directory.Build.props`).
- Solution file is `Honua.DevOps.slnx` (XML solution format).
- Supporting tooling: Bash scripts (`scripts/`), Python helpers
  (`scripts/*.py`), Terraform target references, Helm provenance, observability
  assets (Prometheus/Grafana/Alertmanager).

## Setup

- Requires the .NET 10 SDK (`10.0.x`).
- Config is environment-driven. `honua-devops` auto-loads `.env` then `.env.local`
  from the working directory; process env vars take precedence, `.env.local`
  overrides `.env`. Reference defaults live in `.env.example`.
- Bootstrap a local `.env.local`:
  `./scripts/bootstrap-operator-env.sh --provider codex`
- Provider selection: `HONUA_DEVOPS_PROVIDER` = `codex` | `claude` | `local-llama`
  | `bedrock` (alias `aws-bedrock`).
  The three OpenAI-compatible providers each need their `_MODEL` and `_API_KEY`
  env vars (e.g. `HONUA_DEVOPS_CODEX_MODEL`, `HONUA_DEVOPS_CODEX_API_KEY`);
  endpoints optional except hosted NIM for `local-llama`. `bedrock` needs only
  `HONUA_DEVOPS_BEDROCK_MODEL`; `HONUA_DEVOPS_BEDROCK_REGION` (default
  `us-west-2`) and `HONUA_DEVOPS_BEDROCK_API_KEY` are optional, and without the
  key it falls back to the AWS credential chain. See README "Provider
  Configuration".

## Commands

Build / run (from repo root):

```bash
dotnet restore
dotnet build
dotnet run --project src/Honua.DevOps.Agent -- --provider codex
```

CI builds with `dotnet build --configuration Release --no-restore`.

Common CLI modes (all via `dotnet run --project src/Honua.DevOps.Agent -- ...`):

- `--preflight` — backend connectivity + Terraform target discovery
- `--prompt "<text>"` — single-shot prompt (optionally with `--provider`)
- `--listen` — signed honua-support escalation receiver (needs `HONUA_DEVOPS_WEBHOOK_SECRET`)
- `--list-operations --limit 50` / `--show-operation <id>` — operation journal
  (requires `HONUA_DEVOPS_AUDIT_HOOK_TARGET=file:///path/to/audit.jsonl`)
- `--mcp` — MCP stdio server exposing the full operator toolset 1:1 to MCP
  clients (Claude Code / Codex); same gates and audit, no provider key needed
  (see `docs/QUICKSTART-MCP.md`)
- `--list-tools`, `--help`

Test:

```bash
dotnet test
# Live Honua integration (opt-in):
HONUA_DEVOPS_LIVE_INTEGRATION=true \
HONUA_DEVOPS_HONUA_API_BASE_URL=http://localhost:8080 \
dotnet test tests/Honua.DevOps.Agent.Tests/Honua.DevOps.Agent.Tests.csproj --filter LiveHonuaIntegrationTests
# Live local-llama (opt-in): set HONUA_DEVOPS_LIVE_LOCAL_LLAMA=true + the three provider vars,
# --filter LiveLocalLlamaIntegrationTests
```

Without the `HONUA_DEVOPS_LIVE_*` flags, live tests return early so CI never hits a backend.

Validation / smoke (Bash):

```bash
./scripts/validate-desired-state.sh          # validates desired-state objects (runs dotnet test)
./scripts/smoke-desired-state-scaffold.sh
./scripts/smoke-bootstrap-operator-env.sh
./scripts/smoke-contract.sh
./scripts/slo-release-gate.sh
./scripts/helm-provenance-check.sh
```

There is no separate lint command; rely on `dotnet build` (nullable/warnings)
and `bash -n <script>` syntax checks (used in CI). Many scripts have a paired
`smoke-*.sh` self-test.

## Architecture

Entry point `src/Honua.DevOps.Agent/Program.cs` parses CLI options
(`CliOptions`), loads `.env` files (`DotEnvLoader`), then loads three config
roots: `OperationRuntime`, `OperatorPolicy`, and `BackendConfiguration`. It
constructs `BackendGateway` and `SupportGateway` over a shared `HttpClient`,
builds the tool set via `CapabilityToolset.Create(...)`, and runs the chosen
agent provider.

- `Providers/` — `AgentProviderFactory`, `ProviderConfiguration`, `ProviderKind`
  resolve the OpenAI-compatible adapter for codex/claude/local-llama, or
  `BedrockChatClientAdapter` (Bedrock Converse) for bedrock.
- `Operations/` — the operator capability surface. Key types:
  `CapabilityToolset` (registers function tools), `HonuaOperationsToolkit`,
  `BackendGateway`/`SupportGateway` (HTTP), `OperationResponse(+Builder)` and
  `OperationEvidence` (typed evidence bundles), `Redaction`, `PreflightRunner`.
  Subfolders: `Actuation`, `Audit`, `OperatorPolicy`, `GitOps`, `DesiredState`,
  `RuntimeAdapters`, `ReleaseOrchestration`, `ServiceBundleReconciliation`,
  `Troubleshooting`, `GuidedFix`, `ConsoleBridge`.
- `Operations/Actuation/` — the single write seam (honua-devops#153/#151). Every
  mutating `BackendGateway` route requires a grant that only `ActuationSpine` can
  issue, so a backend write cannot precede its durable operation, policy decision,
  and approval. `ActuationResult` + `ActuationResponseGuard` are the matching
  response invariant: a tool's status, its audit `Mutated` flag, and its backend
  steps all derive from one authoritative result, and `executed`/`applied` requires
  a typed actuator, a durable receipt, and a successful mutating backend step.
  `ActuatorRegistry` resolves the typed actuator BEFORE any readiness is reported —
  an unregistered runbook or remediation returns `unsupported-action` with zero
  backend calls.
- `Configuration/`, `Prompts/` — runtime config and prompt assets.
- Backends are external HTTP services configured by env: Honua API
  (`HONUA_DEVOPS_HONUA_API_BASE_URL`), OTEL (`HONUA_DEVOPS_OTEL_BASE_URL`),
  honua-support (`HONUA_DEVOPS_SUPPORT_API_BASE_URL`). Endpoint paths are
  overridable via many `HONUA_DEVOPS_HONUA_*_PATH` vars (see README).

Execution is gated by runtime controls: `HONUA_DEVOPS_EXECUTION_MODE`
(`plan`/`execute`), `HONUA_DEVOPS_EXECUTION_TIER`, `HONUA_DEVOPS_APPROVAL_MODE`
(`pr-first`/`direct-allowed`/`break-glass-only`), and `HONUA_DEVOPS_AUDIT_HOOK_TARGET`.
Each tool call emits one JSONL audit record.

## Directory Layout

- `src/Honua.DevOps.Agent/` — the console host (only production project).
- `tests/Honua.DevOps.Agent.Tests/` — xUnit tests + `fixtures/`.
- `scripts/` — Bash bootstrap/validation/smoke/SLO scripts + Python helpers
  (`generate-client-compat-scoreboard.py`, `run-multi-model-operator-evals.py`,
  `sweep-blocked-labels.py`); `scripts/fault-injection/`.
- `desired-state/` — starter control-repo layout: `bundles`, `releases`,
  `promotions`, `platform-stacks`, `execution-policies`, `conventions.env`,
  `README.md`.
- `docs/` — extensive operator/architecture docs (features, strategy,
  deployments, launch, runbooks, contracts).
- `observability/` — `prometheus`, `grafana`, `alertmanager` assets.
- `compatibility/` — `clients.catalog.json`, `scoreboard`.
- `eval/` — `model-matrix.json`, `fixtures` for multi-model operator evals.
- `.github/workflows/` — CI (see below).
- `Honua.DevOps.slnx`, `Directory.Build.props`, `.env.example`.

## Conventions & Gotchas

- CI inventory — `.github/workflows/` holds exactly 17 workflows. All but
  `release-mcp.yml` run on `pull_request`, `push` to `trunk`, and
  `workflow_dispatch` (`blocked-label-sweep.yml` filters those two by path);
  `demo-e2e.yml`, `multi-model-operator-evals.yml`, and
  `blocked-label-sweep.yml` additionally run on a schedule.

  .NET lanes:
  - `devops-agent-tests.yml` (`DevOps Agent Tests`) — the top-level build+test
    lane: `dotnet restore`, `dotnet build --configuration Release --no-restore`,
    then `dotnet test` over `tests/Honua.DevOps.Agent.Tests`. Live/integration
    tests self-gate on `HONUA_DEVOPS_LIVE_*` and no-op without credentials, so
    the whole suite runs here.
  - `supply-chain-baseline.yml` — restore + Release build + SBOM + Trivy gate +
    `helm-provenance-check.sh`.
  - `desired-state-validation.yml` — restore + `validate-desired-state.sh` +
    `smoke-desired-state-scaffold.sh`.
  - `operator-bootstrap-smoke.yml` — restore + `smoke-bootstrap-operator-env.sh`
    + `smoke-customer-bootstrap.sh`.

  Script/contract/eval smoke lanes (`bash -n` plus the named smoke script, no
  network or secrets):
  - `devops-baseline-contracts.yml`, `secrets-lifecycle-contracts.yml`,
    `slo-enforcement-baseline.yml`, `client-compatibility-scoreboard.yml`,
    `multi-model-operator-evals.yml`, `backup-restore-gameday.yml`.
  - `console-release-promotion.yml` — `smoke-console-release-gate.sh` for the
    honua-console release gate / preview planner; see
    `docs/console-release-promotion.md`.
  - `demo-e2e.yml` — two tiers: always-on `smoke-demo-e2e.sh` (offline), plus an
    opt-in/scheduled live read-path run of `run-demo-e2e.sh` against the demo
    environment; see `docs/demo-e2e-verification.md`.

  Compatibility-train lanes — `compat-train-conformance-gate.yml` and
  `compat-train-release-validation.yml`, plus the RC orchestrator
  `compat-train-rc-validation.yml`, chain the per-repo/per-surface jobs
  (conformance producer -> live-evidence gate -> manifest validation -> live
  probe) into one aggregated RC evidence bundle via
  `scripts/compat-train-rc-aggregate.sh` (honua-devops#41) — see
  `docs/compat-train-release-validation.md`.

  Hygiene lanes:
  - `blocked-label-sweep.yml` — daily re-verification of every open
    `state/blocked` label against the live state of the blockers it cites
    (`scripts/sweep-blocked-labels.py`, honua-devops#167). READ-ONLY: the sweep
    publishes a markdown report to the job summary and mutates nothing; the
    script's `--enforce` flag refuses without `ENFORCE_SWEEP=true` and refuses
    even with it, because no mutation path is implemented yet. Cross-repo reads
    need `SWEEPER_GH_TOKEN`; without it the lane sweeps this repo only. The
    PR-safe floor is `scripts/smoke-blocked-label-sweep.sh` (offline, stub `gh`).
    See `docs/blocked-label-convention.md`.

  Release lane — `release-mcp.yml` builds and publishes the `--mcp` artifacts
  (per-RID single-file binaries, checksums, GHCR image) on a `v*` tag push, and
  runs the identical build as a dry run on `workflow_dispatch` without pushing
  (honua-devops#148).
- NuGet locked-restore mode is active when `packages.lock.json` is present;
  changing package versions requires updating the lock file.
- Default-safe posture: keep `plan` mode + `pr-first` approval unless a task
  explicitly requires execution; the bridge records proposals with
  `submitImmediately=false` and never submits/rolls back.
- `create_gitops_proposal` returns a blocked `target-unconfigured` projection
  unless `HONUA_DEVOPS_DEPLOY_TARGET_ID` is set (it does not invent operation ids).
- Adding a mutating backend route means adding it to `BackendMutationCatalog` and
  taking an `ActuationSpine` grant; `BackendMutationCatalogTests` fails otherwise.
  Planning/diff may only call non-mutating routes or `PreviewManifestAsync`, which
  pins `dryRun=true` and refuses a write request before sending it.
- A mutation is refused before it starts when the deploy target, the idempotency
  key, or the audit/receipt sink (`HONUA_DEVOPS_AUDIT_HOOK_TARGET`) is unavailable.
- Auto-bundle (forwarding a Honua API key to support) is disabled by default and
  host-allowlisted via `HONUA_DEVOPS_SUPPORT_AUTOBUNDLE_*` when enabled.
- The webhook `--listen` receiver requires `X-Honua-Signature: sha256=<HMAC-SHA256>`
  matching `HONUA_DEVOPS_WEBHOOK_SECRET`.
- Never commit secrets; config flows through env / `.env.local` (gitignored).
- Live integration / NIM tests are opt-in only; do not enable them in default runs.

## Shared dev-environment rules (multi-agent WSL)

This machine runs many agents concurrently (**Codex + Claude**, often via agentflow with multiple tabs/agents). To prevent host lockups and lost work, every agent MUST follow these:

1. **Heavy builds/tests are throttled by a shared lock.** `dotnet` and `npm` are PATH-shimmed, so their build/test/publish/pack and ci/install/test/run-build/run-test subcommands automatically run under a global semaphore (default 1 concurrent, `HONUA_BUILD_SLOTS`). For other heavy tools, call the wrapper explicitly: `with-build-lock pytest ...`, `with-build-lock cargo build`, `with-build-lock make build`. The lock is shared across ALL of this user's processes (every Codex/Claude tab, agentflow children). Do not bypass it for compiles or test suites. Long-running servers (`dotnet run`, `npm run dev`) are intentionally NOT locked — never wrap those.

2. **Commit and push when you finish a task** so your worktree can be reclaimed. An hourly job (`honua-clean`) removes a worktree ONLY when it is clean AND fully pushed (merged, remote-gone, or idle >=2d). Dirty or unpushed worktrees are NEVER touched — but uncommitted/unpushed work blocks reclamation and is at risk if the instance is reset. Build artifacts (bin/obj and untracked node_modules) are reclaimed automatically and safely.

3. **Commit hygiene — no agent attribution.** Author every commit as the repo owner only (git identity: Mike McDougall <mike@honua.io>). Do **NOT** add any agent/tool attribution to commits: no `Co-Authored-By: Claude ...`, no `Co-Authored-By: Codex ...` (or other bot co-authors), and no "Generated with Claude Code" / "Generated with Codex" / "🤖" lines in the message or PR body. Write a plain, descriptive commit message and stop.
