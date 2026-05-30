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
- Provider-pluggable AI runtime over OpenAI-compatible adapters: `codex`,
  `claude`, `local-llama` (NVIDIA NIM / vLLM / Ollama / TGI). Default `codex`.
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
- Provider selection: `HONUA_DEVOPS_PROVIDER` = `codex` | `claude` | `local-llama`.
  Each provider needs its `_MODEL` and `_API_KEY` env vars (e.g.
  `HONUA_DEVOPS_CODEX_MODEL`, `HONUA_DEVOPS_CODEX_API_KEY`); endpoints optional
  except hosted NIM for `local-llama`. See README "Provider Configuration".

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
  resolve the OpenAI-compatible adapter for codex/claude/local-llama.
- `Operations/` — the operator capability surface. Key types:
  `CapabilityToolset` (registers function tools), `HonuaOperationsToolkit`,
  `BackendGateway`/`SupportGateway` (HTTP), `OperationResponse(+Builder)` and
  `OperationEvidence` (typed evidence bundles), `Redaction`, `PreflightRunner`.
  Subfolders: `Audit`, `OperatorPolicy`, `GitOps`, `DesiredState`,
  `RuntimeAdapters`, `ReleaseOrchestration`, `ServiceBundleReconciliation`,
  `Troubleshooting`, `GuidedFix`, `ConsoleBridge`.
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
  (`generate-client-compat-scoreboard.py`, `run-multi-model-operator-evals.py`);
  `scripts/fault-injection/`.
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

- No top-level `dotnet build`/`dotnet test` CI workflow exists. The .NET build
  is exercised by `Supply Chain Baseline` (`supply-chain-baseline.yml`: restore +
  Release build + SBOM + Trivy gate + Helm provenance) and `Desired State
  Validation` (`desired-state-validation.yml`: restore + `validate-desired-state.sh`).
  Other workflows are smoke/contract/eval lanes (`devops-baseline-contracts.yml`,
  `operator-bootstrap-smoke.yml`, `secrets-lifecycle-contracts.yml`,
  `slo-enforcement-baseline.yml`, `client-compatibility-scoreboard.yml`,
  `multi-model-operator-evals.yml`, `backup-restore-gameday.yml`,
  `console-release-promotion.yml`: `bash -n` + `smoke-console-release-gate.sh` for
  the honua-console release gate / preview planner — see
  `docs/console-release-promotion.md`).
- NuGet locked-restore mode is active when `packages.lock.json` is present;
  changing package versions requires updating the lock file.
- Default-safe posture: keep `plan` mode + `pr-first` approval unless a task
  explicitly requires execution; the bridge records proposals with
  `submitImmediately=false` and never submits/rolls back.
- `create_gitops_proposal` returns a blocked `target-unconfigured` projection
  unless `HONUA_DEVOPS_DEPLOY_TARGET_ID` is set (it does not invent operation ids).
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
