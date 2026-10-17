---
type: reference
description: Private ECS provisioning integration behind the unified public MCP connection and canonical server approval workflow.
---

# Unified agent workspace integration

Contract: `honua.agent-devops-integration/v1`, version `2026-10-17.1`.
The public `honua-agent-mcp` gateway optionally connects the existing private
`honua-devops --mcp` process. The host fixes the executable, arguments, environment
and current authorization binding. These are never model-selectable tool inputs.
The gateway prefixes the canonical tool names with `honua_agent_devops_` and
forwards their discovered schemas unchanged. Every discovery/action rechecks the
bound credential, target and caller permissions; every canonical handler retains
its own policy, audit, approval and exact-plan checks. There is one public MCP
registration and no replacement executor.

The permitted private surface is provisioning (`describe_environment`,
`provision_infrastructure`, `install_handoff`, `verify_install_handoff`), evidence
(`find_recent_operations`, `get_gitops_proposal`, `get_devops_operation_status`),
and scoped Git/deploy planning (`plan_gitops_engine`,
`generate_metadata_release_changeset`, `explain_metadata_release_changeset`,
`plan_metadata_release_gitops`, `create_gitops_proposal`, `deploy_service_gitops`),
plus optional `create_scoped_gitops_pull_request` and
`revert_scoped_gitops_pull_request`.
The caller cannot invoke `record_gitops_proposal_decision`, private experimental
rollback, arbitrary process execution, or any unlisted tool through this bridge.

## ECS provisioning and authenticated handoff

The concrete supported provisioning adapter is `aws-ecs`, size `small`, using
`Provisioning/HonuaOperationsToolkit.Provisioning.cs` and the existing exact-plan
Terraform substrate. Plan produces a durable provisioning operation, saved-plan
metadata digest and review challenge. A separate trusted issuer signs approval
for that exact operation, plan, action, stack and environment. Apply consumes the
saved plan; it does not regenerate a different plan after approval. The canonical
executor still gates production/break-glass actions. Cloud credentials and signing
keys remain outside the conversation. A local HMAC development receipt is
explicitly non-evidentiary; production evidence uses the separate KMS issuer.

`install_handoff` emits a secretless unverified handoff from captured operator
contract v2 output. `verify_install_handoff` resolves its secret reference only
inside the child transport and verifies health, Admin authentication, MCP
negotiation and the actual tool roster before producing a binding receipt.
Provisioning success alone never means an installed target is ready.

## Git and Studio configuration

The existing metadata release change-set builder takes a server release package
and generates scoped PR-ready files, compatibility evidence and rollback policy.
It is read-only: it does not write Git or open a PR. Studio versions must enter
through the canonical server package/version contract; do not synthesize feature
edits or arbitrary repository files. The optional scoped adapter commits and pushes a deterministic review branch,
opens a GitHub PR, and records the actual commit/PR and canonical server operation.
It requires the current base commit, execute mode, propose tier and durable audit.
It never pushes the base branch, force-pushes, merges, approves or submits deployment.
A retry produces the same commit/branch and reconciles an existing PR. A single-parent
configuration commit may be reverted through a new reviewed branch/PR; all paths
and the resulting diff are checked against the same scope and secret guards.

Host configuration is optional and contains no model-selectable paths or commands:
`HONUA_DEVOPS_SCOPED_GIT_REMOTE`, `HONUA_DEVOPS_SCOPED_GIT_REPOSITORY` (owner/repo),
`HONUA_DEVOPS_SCOPED_GIT_BASE_BRANCH` (default trunk),
`HONUA_DEVOPS_SCOPED_GIT_ALLOWED_PREFIXES` (default desired-state/releases/), and
`HONUA_DEVOPS_GITHUB_TOKEN` (transport only). Paths are additionally restricted to
release configuration and Studio version directories. The existing process runner
and ActuationSpine audit preflight are reused. GitHub API is fixed to api.github.com,
with redirects disabled. No additional cloud executor or approval inbox is created.
A generated diff alone remains no proof of a Git write or reconciliation.

## Version operations, approval and recovery

Day-2 health/version/recovery remains honua-server's canonical `/mcp` tools:
`honua_ops_*`, `honua_propose_*` and discovered operation contracts. The human
approval inbox and its separately authenticated principal remain authoritative.
The model can create a scoped proposal and inspect its canonical operation; it
cannot approve it. An `AwaitingApproval` result is a handoff requiring the human
inbox, not proof the deployment executed.

After approval, record the actual terminal deployment status and reread service
health/revision. Recover a lost mutation response by canonical command/operation
identity before retrying. Protected recovery consumes the existing sealed grant,
known-good/candidate revisions, observation policy and durable desired-intent
ledger. It preserves restored desired state and candidate quarantine under its
existing CAS/fence rules. Do not introduce a parallel rollback tool or a second
model decision to execute deterministic approved compensation.

ECS provisioning support does **not** qualify ECS version rollback/recovery.
Current [protected recovery qualification](protected-deployment-recovery.md)
identifies its exact installed backend; its SelfHostedRolling proof cannot be
transferred to ECS. The server has a real AWS SDK ECS/ALB deploy backend (`AwsEcsAlbDeployBackend`)
and opt-in `AwsEcsAlbRealCertificationTests` /
`AwsEcsAlbRollbackFailureRealCertificationTests`. The unified journey still needs
a separately authorized isolated live upgrade,
failure injection, compensation, restart/reconcile and actual revision/health
verification against the ECS adapter. Lambda/EKS are unsupported in this bridge
until real adapters and equivalent execution evidence exist. Runtime adapter
planning strings do not establish adapter execution support.

## Evidence levels

- Contract tests: allowlist, authorization, schemas, opaque failures and no replay.
- Real private-process integration: initialized artifact plus read-only planner.
- Live qualification: exact artifact/adapter/principal/target, reviewed approved
  plan, actual deployed state and recovery/Git evidence. No mock or syntactic
  validation can replace this level.

See [MCP quickstart](QUICKSTART-MCP.md), [operation lineage](operation-lineage.md),
and [protected recovery](protected-deployment-recovery.md).
