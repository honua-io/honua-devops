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

- Log analysis and root-cause guidance
- Metrics analysis and performance tuning plans
- Troubleshooting and optimization workflows
- Server upgrade planning with rollback gates
- GitOps-driven multi-environment deployment planning
- Customer requirements analysis with deployment recommendations
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
- `HONUA_DEVOPS_GITOPS_TOOL` (`flux` default, supports `argocd` too)
- `HONUA_DEVOPS_ALLOWED_ENVIRONMENTS` (comma-separated, default `dev,staging,prod`)

## Run

```bash
dotnet restore
dotnet build
dotnet run --project src/Honua.DevOps.Agent -- --provider codex
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

- Baseline scaffolding is in place with interactive chat mode and provider selection.
- Next slices should add concrete MCP tools and cloud execution connectors for safe action-taking workflows.
