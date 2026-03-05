# honua-devops

AI DevOps operator and solution architect for Honua.

This repository is the execution vehicle for [honua-server issue #364](https://github.com/honua-io/honua-server/issues/364):

- Operate Honua like a senior platform operator (install, configure, optimize, monitor, troubleshoot, upgrade).
- Act as a solution engineer and architect to design and deploy Honua workloads to cloud environments.
- Customize deployment topology per environment (WAF/no WAF, nginx proxy/no proxy, edge rate limiting posture, scaling shape).
- Provide provider-pluggable AI runtime with at least `codex` and `claude`.

Mission: raise the technical and delivery bar high enough to disrupt the GIS professional services status quo.

## Stack

- .NET 10 console host
- Microsoft Agent Framework (`Microsoft.Agents.AI`, `Microsoft.Agents.AI.OpenAI`)
- OpenAI-compatible provider adapters (`codex`, `claude`)
- Built-in operations toolset (function tools for logs, metrics, troubleshooting, tuning, upgrades, GitOps deploys, customer requirement analysis)

## Built-In Capabilities

- Log analysis and root-cause guidance (via OTEL endpoints)
- Metrics analysis and performance tuning plans (via OTEL + Honua API)
- Troubleshooting and optimization workflows (via Honua API)
- Server upgrade planning with rollback gates (via Honua API)
- GitOps-driven multi-environment deployment planning (Honua-native GitOps; see `honua-server` #351/#363)
- Customer requirements analysis with deployment recommendations (mapped to validated Terraform templates for `azure-functions`, `lambda`, `eks`, `aks`, `ecs`, `aca`)
- Topology recommendations (WAF/no WAF, nginx/no proxy, edge rate limiting)

## Provider Configuration

Set `HONUA_DEVOPS_PROVIDER` to `codex` or `claude` (defaults to `codex`).
Reference defaults live in `.env.example`.

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
- `HONUA_DEVOPS_GITOPS_TOOL` (`honua-gitops` default; also supports `flux`, `argocd`)
- `HONUA_DEVOPS_ALLOWED_ENVIRONMENTS` (comma-separated, default `dev,staging,prod`)
- `HONUA_DEVOPS_TERRAFORM_REPO` (validated template repo, default `https://github.com/honua-io/honua-terraform`)
- `HONUA_DEVOPS_TERRAFORM_REF` (template repo ref, default `main`)
- `HONUA_DEVOPS_TERRAFORM_TARGETS` (default `azure-functions,lambda,eks,aks,ecs,aca`)
- `HONUA_DEVOPS_TERRAFORM_LOCAL_PATH` (optional local repo path for target auto-discovery; default sibling `../honua-terraform`)

## Backend Integration

Primary operational backends:

- Honua API (`HONUA_DEVOPS_HONUA_API_BASE_URL`)
- OTEL endpoints (`HONUA_DEVOPS_OTEL_BASE_URL`)

Optional auth:

- `HONUA_DEVOPS_HONUA_API_KEY` (sent as `X-API-Key` for Honua admin/metrics contracts)
- `HONUA_DEVOPS_OTEL_API_KEY` (sent as `Authorization: Bearer ...` for OTEL queries)

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

Legacy aliases still accepted for compatibility:

- `HONUA_DEVOPS_HONUA_HEALTH_PATH`
- `HONUA_DEVOPS_HONUA_TROUBLESHOOT_PATH`
- `HONUA_DEVOPS_HONUA_TUNE_PATH`
- `HONUA_DEVOPS_HONUA_UPGRADE_PATH`
- `HONUA_DEVOPS_HONUA_DEPLOY_PATH`
- `HONUA_DEVOPS_HONUA_REQUIREMENTS_PATH`
- `HONUA_DEVOPS_HONUA_TOPOLOGY_PATH`

- `HONUA_DEVOPS_BACKEND_TIMEOUT_SECONDS`

## Run

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

## Current Status

- Provider-pluggable agent scaffold is in place with live Honua API and OTEL endpoint wiring.
- Honua-native GitOps and customer-requirement analysis workflows are wired as callable tools.
- Preflight mode validates backend reachability and Terraform target discovery before live runs.
