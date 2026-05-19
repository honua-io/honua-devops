# honua-devops

AI DevOps operator and solution architect for Honua.

Current operator capabilities are summarized in [docs/features/README.md](docs/features/README.md).

This repository is the execution vehicle for the operator control system tracked in [honua-devops#11](https://github.com/honua-io/honua-devops/issues/11):

- Operate Honua like a senior platform operator (install, configure, optimize, monitor, troubleshoot, upgrade).
- Act as a solution engineer and architect to design and deploy Honua workloads to cloud environments.
- Customize deployment topology per environment (WAF/no WAF, nginx proxy/no proxy, edge rate limiting posture, scaling shape).
- Provide provider-pluggable AI runtime with at least `codex` and `claude`.

Mission: raise the technical and delivery bar high enough to disrupt the GIS professional services status quo.

## License and Availability

`honua-devops` is private operator tooling and is **not** part of Honua's
open-core runtime promise.

- Public/open surfaces remain in `honua-server`, the official SDK repos, the
  mobile repos, and the base MCP data-access surface.
- This repository covers operator-grade AI DevOps/copilot workflows such as
  rollout planning, delegated operations, and implementation-partner delivery.
- Licensing for this repository is proprietary. See [LICENSE](LICENSE).

## Stack

- .NET 10 console host
- Microsoft Agent Framework (`Microsoft.Agents.AI`, `Microsoft.Agents.AI.OpenAI`)
- OpenAI-compatible provider adapters (`codex`, `claude`)
- Built-in operations toolset (function tools for logs, metrics, troubleshooting, tuning, upgrades, GitOps deploys, customer requirement analysis)

## Built-In Capabilities

- Log analysis and root-cause guidance (via OTEL endpoints)
- Metrics analysis and performance tuning plans (via OTEL + Honua API)
- Troubleshooting and optimization workflows (via Honua API)
- Honua support ticket triage through `honua-support` (`process_pending_tickets`)
- Server upgrade planning with rollback gates (via Honua API)
- GitOps-driven multi-environment deployment planning (Honua-native GitOps; see `honua-server` #351/#363)
- Full GitOps platform planning (`plan_gitops_platform`) for repo watching, commit tracking, promotion gates, drift alerts, CI/CD previews, rollback, and audit evidence
- AI DevOps MCP-style tools (`honua_diagnose`, slow-query explanation, index recommendation, capacity forecast, runbook execution, incident summary, migration advisor, and auto-remediation planning)
- Customer requirements analysis with deployment recommendations (mapped to validated Terraform templates for `azure-functions`, `lambda`, `eks`, `aks`, `ecs`, `aca`)
- Topology recommendations (WAF/no WAF, nginx/no proxy, edge rate limiting)

## Provider Configuration

Set `HONUA_DEVOPS_PROVIDER` to `codex` or `claude` (defaults to `codex`).
Reference defaults live in `.env.example`.
`honua-devops` auto-loads `.env` and `.env.local` from the working directory, with process environment variables taking precedence and `.env.local` overriding `.env`.

### Codex provider

- `HONUA_DEVOPS_CODEX_MODEL` (required)
- `HONUA_DEVOPS_CODEX_API_KEY` (required)
- `HONUA_DEVOPS_CODEX_ENDPOINT` (optional, for custom OpenAI-compatible endpoint)

### Claude provider

- `HONUA_DEVOPS_CLAUDE_MODEL` (required)
- `HONUA_DEVOPS_CLAUDE_API_KEY` (required)
- `HONUA_DEVOPS_CLAUDE_ENDPOINT` (optional, for custom OpenAI-compatible endpoint)

## Runtime Controls

- `HONUA_DEVOPS_EXECUTION_MODE` (`plan` default, or `execute`)
- `HONUA_DEVOPS_EXECUTION_TIER` (`plan` default for plan mode; `execute-lower-env` default for execute mode)
- `HONUA_DEVOPS_APPROVAL_MODE` (`pr-first` default; also supports `direct-allowed`, `break-glass-only`)
- `HONUA_DEVOPS_AUDIT_HOOK_TARGET` (`stdout-evidence` default)
- `HONUA_DEVOPS_SUPPORT_SESSION_ACCESS` (`disabled` default; also supports `read-only`, `operator-scoped`)
- `HONUA_DEVOPS_SUPPORT_SESSION_TTL_MINUTES` (`60` default)
- `HONUA_DEVOPS_SUPPORT_SESSION_CUSTOMER_VISIBLE` (`true` default)
- `HONUA_DEVOPS_BREAK_GLASS_POST_REVIEW_REQUIRED` (`true` default)
- `HONUA_DEVOPS_GITOPS_TOOL` (`honua-gitops` default; also supports `flux`, `argocd`)
- `HONUA_DEVOPS_ALLOWED_ENVIRONMENTS` (comma-separated, default `dev,staging,prod`)
- `HONUA_DEVOPS_TERRAFORM_REPO` (validated template repo, default `https://github.com/honua-io/honua-terraform`)
- `HONUA_DEVOPS_TERRAFORM_REF` (template repo ref, default `main`)
- `HONUA_DEVOPS_TERRAFORM_TARGETS` (default `azure-functions,lambda,eks,aks,ecs,aca`)
- `HONUA_DEVOPS_TERRAFORM_LOCAL_PATH` (optional local repo path for target auto-discovery; default sibling `../honua-terraform`)
- `HONUA_DEVOPS_DEPLOY_TARGET_ID` (optional Honua deploy-control target; enables real `/api/v1/admin/deploy/*` preflight, plan, and operation calls)

## Backend Integration

Primary operational backends:

- Honua API (`HONUA_DEVOPS_HONUA_API_BASE_URL`)
- OTEL endpoints (`HONUA_DEVOPS_OTEL_BASE_URL`)
- Honua support API (`HONUA_DEVOPS_SUPPORT_API_BASE_URL`, optional)

Optional auth:

- `HONUA_DEVOPS_HONUA_API_KEY` (sent as `X-API-Key` for Honua admin/metrics contracts)
- `HONUA_DEVOPS_OTEL_API_KEY` (sent as `Authorization: Bearer ...` for OTEL queries)
- `HONUA_DEVOPS_SUPPORT_API_BEARER_TOKEN` (sent as `Authorization: Bearer ...` for authenticated `honua-support` deployments)

Support ticket integration:

- `HONUA_DEVOPS_SUPPORT_API_BASE_URL` enables `process_pending_tickets`
- `HONUA_DEVOPS_SUPPORT_API_TICKETS_PATH` defaults to `/api/v1/tickets`
- `HONUA_DEVOPS_SUPPORT_API_BEARER_TOKEN` should be set to an operator support token outside local development.
- Diagnosis posts include guided-fix output plus `OperationEvidence` and `DiagnosisScorecard` payloads for ticket audit and score tracking.

Health probes:

- `HONUA_DEVOPS_HONUA_READINESS_PATH` (default `/healthz/ready`)
- `HONUA_DEVOPS_OTEL_HEALTH_PATH` (default `/`)

OTEL path overrides:

- `HONUA_DEVOPS_OTEL_LOGS_PATH`
- `HONUA_DEVOPS_OTEL_METRICS_PATH`

Honua endpoint contract overrides (defaults map to implemented `honua-server` routes):

- `HONUA_DEVOPS_HONUA_ADMIN_ERRORS_PATH` (`/api/v1/admin/observability/errors`)
- `HONUA_DEVOPS_HONUA_ADMIN_TELEMETRY_PATH` (`/api/v1/admin/observability/telemetry`)
- `HONUA_DEVOPS_HONUA_METRICS_HEALTH_PATH` (`/api/v1/metrics/health`)
- `HONUA_DEVOPS_HONUA_METRICS_PERFORMANCE_PATH` (`/api/v1/metrics/performance`)
- `HONUA_DEVOPS_HONUA_METRICS_DATABASE_PATH` (`/api/v1/metrics/database`)
- `HONUA_DEVOPS_HONUA_METRICS_CACHE_PATH` (`/api/v1/metrics/cache`)
- `HONUA_DEVOPS_HONUA_METRICS_MEMORY_PATH` (`/api/v1/metrics/memory`)
- `HONUA_DEVOPS_HONUA_QUERY_CACHE_STATS_PATH` (`/api/v1/admin/performance/database/query-cache/statistics`)
- `HONUA_DEVOPS_HONUA_ADMIN_VERSION_PATH` (`/api/v1/admin/version`)
- `HONUA_DEVOPS_HONUA_ADMIN_CAPABILITIES_PATH` (`/api/v1/admin/capabilities`)
- `HONUA_DEVOPS_HONUA_MANIFEST_EXPORT_PATH` (`/api/v1/admin/manifest`)
- `HONUA_DEVOPS_HONUA_MANIFEST_APPLY_PATH` (`/api/v1/admin/manifest/apply`)
- `HONUA_DEVOPS_HONUA_DEPLOY_PREFLIGHT_PATH` (`/api/v1/admin/deploy/preflight`)
- `HONUA_DEVOPS_HONUA_DEPLOY_PLAN_PATH` (`/api/v1/admin/deploy/plan`)
- `HONUA_DEVOPS_HONUA_DEPLOY_OPERATIONS_PATH` (`/api/v1/admin/deploy/operations`)
- `HONUA_DEVOPS_HONUA_MANIFEST_DRIFT_PATH` (`/api/v1/admin/manifest/drift`)
- `HONUA_DEVOPS_HONUA_MANIFEST_VERSIONS_PATH` (`/api/v1/admin/manifest/versions`)

Legacy aliases still accepted for compatibility:

- `HONUA_DEVOPS_HONUA_HEALTH_PATH`
- `HONUA_DEVOPS_HONUA_TROUBLESHOOT_PATH`
- `HONUA_DEVOPS_HONUA_TUNE_PATH`
- `HONUA_DEVOPS_HONUA_UPGRADE_PATH`
- `HONUA_DEVOPS_HONUA_DEPLOY_PATH`
- `HONUA_DEVOPS_HONUA_REQUIREMENTS_PATH`
- `HONUA_DEVOPS_HONUA_TOPOLOGY_PATH`

- `HONUA_DEVOPS_BACKEND_TIMEOUT_SECONDS`

Live Honua integration tests are opt-in:

```bash
HONUA_DEVOPS_LIVE_INTEGRATION=true \
HONUA_DEVOPS_HONUA_API_BASE_URL=http://localhost:8080 \
HONUA_DEVOPS_HONUA_API_KEY="$HONUA_ADMIN_PASSWORD" \
dotnet test tests/Honua.DevOps.Agent.Tests/Honua.DevOps.Agent.Tests.csproj \
  --filter LiveHonuaIntegrationTests
```

Set `HONUA_DEVOPS_DEPLOY_TARGET_ID` to also exercise the real deploy-control plan contract. The current live tests do not create, submit, or roll back deploy operations.

## Run

Bootstrap a local `.env.local` for onboarding:

```bash
./scripts/bootstrap-operator-env.sh --provider codex
```

Bootstrap smoke check:

```bash
./scripts/smoke-bootstrap-operator-env.sh
```

Customer bootstrap command:

```bash
./scripts/bootstrap-customer-repo.sh --service roads-api --runtime-target eks
```

The customer bootstrap command also emits a starter GitHub Actions workflow at `.github/workflows/honua-operator-validation.yml` so desired-state validation is wired into the customer repo immediately.
It now also emits `.github/workflows/honua-operator-preflight.yml` for manual backend/terraform preflight checks and `bootstrap/configure-honua-operator-ci.sh` to load the expected repo vars/secrets with `gh`.

```bash
dotnet restore
dotnet build
dotnet run --project src/Honua.DevOps.Agent -- --provider codex
```

Preflight checks (backend connectivity + terraform target discovery):

```bash
dotnet run --project src/Honua.DevOps.Agent -- --preflight
```

Single-shot prompt:

```bash
dotnet run --project src/Honua.DevOps.Agent -- --provider claude --prompt "Diagnose elevated 5xx on service roads-prod"
```

Topology planning prompt example:

```bash
dotnet run --project src/Honua.DevOps.Agent -- --provider codex --prompt "Design a prod topology for Honua with no WAF, nginx ingress, and edge rate limiting at ALB."
```

Customer requirement analysis example:

```bash
dotnet run --project src/Honua.DevOps.Agent -- --provider codex --prompt "Analyze requirements for a state GIS portal and recommend deployment topology, scaling, and GitOps rollout across dev/staging/prod."
```

## Customer Adoption

The recommended first install mode is to start directly from `honua-devops` as the customer-owned control repo, keep execution in `plan` mode with `pr-first` approval, and use the repo itself to store desired-state objects until a split-repo model is justified.

Reference material:

- `desired-state/README.md` for the starter control-repo layout and sample typed objects
- `docs/desired-state-scaffold.md` for the scaffold script that generates new service trees
- `docs/operator-adoption-packaging.md` for install modes, repo layouts, target-specific adoption differences, and reference workflows
- `docs/operator-policy-and-delegated-ops.md` for approvals, support sessions, and break-glass posture
- `docs/manual-cloud-runbooks.md` for cloud bootstrap and validation loops
- `docs/epic-backlog-closure.md` for the #3/#4 GitOps and AI DevOps closure surface

Onboarding helper:

- `scripts/bootstrap-operator-env.sh` writes a local `.env.local` and can immediately run preflight

## Current Status

- Provider-pluggable agent scaffold is in place with live Honua API and OTEL endpoint wiring.
- Honua-native GitOps and customer-requirement analysis workflows are wired as callable tools.
- Preflight mode validates backend reachability and Terraform target discovery before live runs.
- Operator execution tiers now gate GitOps behavior for read-only, lower-env execute, prod promotion, and break-glass paths.
- Upgrade and GitOps responses now emit structured evidence bundles with effective action, policy gate, target environments, and required checks.
- Runtime targets now resolve through a typed adapter catalog so preflight and deploy planning surface family-specific verify, rollback, drift, and migration semantics.
- The operator now has a shared runtime-adapter lifecycle in code for `validate -> plan/apply infra -> plan/apply release -> verify -> rollback -> drift -> export actual state`.
- Upgrade and deploy planning now emit an explicit release-orchestration state machine covering preflight, backup, migration, rollout, smoke, SLO watch, promote, and rollback, plus typed promotion and rollback semantics.
- Deploy planning now emits a typed ServiceBundle reconciliation map for capabilities, export, metadata subset apply, connections, publishing, policy, styles, and imports, plus explicit drift/export state.
- Deploy planning now also emits a typed in-repo `honua-gitops` engine plan with per-environment diff, drift, gate state, explicit from/to state transitions, mutation flags, approval requirements, and supported operations.
- Operator policy is now explicit in runtime output and evidence: approval mode, audit hook target, support-session posture, and break-glass post-review requirements.
- The Azure-first operator host now has a typed orchestration planner and `plan_azure_operator_workflow` tool that maps analyze, publish, build, and deploy workflows to MCP/gRPC/honua-server contract responsibilities without redefining those surfaces.
- Honua support ticket processing now wires `honua-support` into the runtime toolset and posts diagnosis evidence/scorecards back to ticket records.
- Multi-model operator eval automation now consumes the server-side eval report and can run Claude, Codex, and local Llama lanes through a shared model matrix.
- Remaining GitOps and AI DevOps epic surfaces are now represented by contract-first tools with edition, approval, audit, rollback, and validation gates.

## DevOps Delivery Artifacts

- Deployment validation matrix and smoke contract: `docs/deployment-validation-matrix.md`
- Manual AWS/Azure runbooks (apply -> smoke -> destroy): `docs/manual-cloud-runbooks.md`
- Backup and restore game-day: `docs/backup-restore-gameday.md`
- SLO release gate baseline: `docs/slo-release-gates.md`
- SLO observability assets: `observability/`
- Client compatibility scoreboard: `docs/client-compatibility-scoreboard.md`
- Secrets lifecycle: `docs/secrets-lifecycle.md`
- Supply-chain baseline and CI policy: `docs/supply-chain-baseline.md`
- Operating cadence and close hygiene: `docs/operating-cadence.md`
- Operator control contract: `docs/operator-control-contract.md`
- Desired-state schema contract: `docs/desired-state-schemas.md`
- Runtime adapter framework: `docs/runtime-adapter-framework.md`
- honua-gitops engine: `docs/honua-gitops-engine.md`
- Release orchestration state machine: `docs/release-orchestration-state-machine.md`
- ServiceBundle reconciliation: `docs/service-bundle-reconciliation.md`
- Operator policy and delegated ops: `docs/operator-policy-and-delegated-ops.md`
- Operator adoption packaging: `docs/operator-adoption-packaging.md`
- Guided-fix workflow: `docs/guided-fix-workflow.md`
- Troubleshooting integration tests: `docs/troubleshooting-integration-tests.md`
- Desired-state starter pack: `desired-state/README.md`
- Desired-state scaffold helper: `docs/desired-state-scaffold.md`
- Contract boundaries and consumption matrix: `docs/contract-boundaries.md`
- Azure operator orchestration host: `docs/azure-operator-orchestration-host.md`
- Multi-model operator evals: `docs/multi-model-operator-evals.md`

Validation:

```bash
./scripts/validate-desired-state.sh
```

Conventions for desired-state naming and allowed runtime targets live in `desired-state/conventions.env`.

Scaffold smoke check:

```bash
./scripts/smoke-desired-state-scaffold.sh
```

Helper scripts:

- `scripts/bootstrap-operator-env.sh`
- `scripts/smoke-bootstrap-operator-env.sh`
- `scripts/bootstrap-customer-repo.sh`
- `scripts/install-customer-ci.sh`
- `scripts/smoke-customer-bootstrap.sh`
- `scripts/smoke-contract.sh`
- `scripts/slo-release-gate.sh`
- `scripts/smoke-slo-release-gate.sh`
- `scripts/slo-release-watch.sh`
- `scripts/smoke-slo-release-watch.sh`
- `scripts/validate-slo-assets.sh`
- `scripts/generate-client-compat-scoreboard.py`
- `scripts/smoke-client-compat-scoreboard.sh`
- `scripts/run-multi-model-operator-evals.py`
- `scripts/smoke-multi-model-operator-evals.sh`
- `scripts/run-backup-restore-gameday.sh`
- `scripts/smoke-backup-restore-gameday.sh`
- `scripts/rotate-operator-secrets.sh`
- `scripts/revoke-operator-secrets.sh`
- `scripts/smoke-secret-lifecycle.sh`
- `scripts/post-weekly-backlog-review.sh`
- `scripts/check-terraform-secrets.sh`
- `scripts/dispatch-terraform-validation.sh`
- `scripts/helm-provenance-check.sh`
