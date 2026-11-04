---
name: honua-deploy-aws
description: Install Honua on AWS ECS/Fargate through the honua-devops MCP operator, with exact-plan review, signed approval and verified handoff. Use for a new AWS installation or resuming one; day-2 operations belong to the installed server.
---

# Deploy Honua on AWS

Use honua-devops as the operator. This skill supplies agent guidance, not an
executor or permission to create resources. Start with `install_honua`; never
run Terraform directly, replace the operator's workflow, or bypass its gates.
The first-install path supports AWS ECS/Fargate, small preset, in an explicitly
allowed non-production environment. Do not substitute Lambda, EKS, Azure or Helm.

## Install and connect the matching operator

Use the same release archive for this skill and the operator binary. Choose a
published release that includes `skills/honua-deploy-aws/SKILL.md`; older archives
without that file cannot install this skill. Record the exact release version.
Read the [MCP quickstart](https://github.com/honua-io/honua-devops/blob/trunk/docs/QUICKSTART-MCP.md)
and [operator deployment guide](https://github.com/honua-io/honua-iac/blob/trunk/docs/operator-deployment.md)
before planning. Pin honua-iac to v0.6.1, including its deploy-identity contract.

For Linux x64, set VERSION to the selected release tag and use a new download
directory. On macOS select osx-arm64 or osx-x64 and use `shasum -a 256 -c`.
On Windows select win-x64.zip, verify its SHA-256 against the adjacent checksum
with PowerShell Get-FileHash before Expand-Archive, and register the absolute
path to Honua.DevOps.Agent.exe instead.

```bash
: "${VERSION:?Set VERSION to a published release containing this skill}"
ASSET=honua-devops-linux-x64.tar.gz
gh release download "$VERSION" --repo honua-io/honua-devops \
  --pattern "$ASSET" --pattern "$ASSET.sha256"
sha256sum -c "$ASSET.sha256" || exit 1
INSTALL_DIR="$HOME/.local/share/honua-devops/$VERSION"
mkdir -p "$INSTALL_DIR"
tar -xzf "$ASSET" -C "$INSTALL_DIR"
test -f "$INSTALL_DIR/skills/honua-deploy-aws/SKILL.md" || exit 1
```

For Claude Code, copy the extracted skill directory into `.claude/skills/` in
the project (or `~/.claude/skills/` for personal use), then register:

```bash
claude mcp add honua-devops -- "$INSTALL_DIR/Honua.DevOps.Agent" --mcp
claude mcp list
```

For Codex, use `.agents/skills/` in the project (or `~/.agents/skills/` for
personal use), then register the same stdio executable:

```bash
codex mcp add honua-devops -- "$INSTALL_DIR/Honua.DevOps.Agent" --mcp
codex mcp list
```

See [Codex MCP registration](https://developers.openai.com/codex/mcp).
Restart the agent session if needed and confirm its discovered tools include
`install_honua`. Stop on a checksum mismatch, missing skill, or missing tool;
do not select a different artifact silently. MCP mode uses the client's model
and needs no operator model-provider key. Terraform and the pinned honua-iac
checkout remain operator-host prerequisites; the binary does not bundle them.
Configure secrets and short-lived SSO/OIDC/assumed-role credentials out of band,
using the quickstart's host settings. Never print credentials, passwords, state
or signing material, and never create IAM users or long-lived access keys.

## Follow the resumable install

Call `install_honua` and retain its installationId. Follow the returned question
and next step, supplying only requested non-secret inputs. Confirm account and
region, customer requirements, intended server image, and the local-state versus
governed remote-state path before planning. Recommend governed state and
short-lived OIDC identity for shared or long-lived installations; explain its
bootstrap resources and obtain permission before those are created. Check
Terraform 1.5 or later (1.10 or later for S3 state locking).

The coordinator calls `describe_environment`, `analyze_customer_requirements`,
`recommend_deployment_topology`, `provision_infrastructure`, `install_handoff`
and `verify_install_handoff`. Let it orchestrate these; do not turn its advisory
topology recommendation into authority to provision a custom topology.

At the approval stage, present the returned account/execution identity, region,
topology, exact deploy-identity actions, resource changes, cost drivers (RDS,
Fargate, load balancer, NAT, storage and optional Redis/logs), saved-plan hash
and plan metadata digest. Save the unchanged plan response privately for the
approver. Ask the user to review this exact plan. Do not regenerate a plan after
approval or treat a chat confirmation as a signed receipt.

Obtain the separately signed receipt using the quickstart's approval-issuer
procedure. For governed use require kms-mac with an independent signing
principal; the operator principal should have VerifyMac only, never GenerateMac.
Do not sign your own approval or request signing authority. Local-hmac-dev is
non-evidentiary development only: its verifier can sign, so it does not establish
independent approval. Human review and KMS permission separation are operator
setup/procedure requirements, not independently proven by the install tool.

Resume using the same installationId, confirmed=true, its retained confirmation
challenge and the signed receipt. The handlers verify receipt signature, trusted
issuer, expiry and exact plan/action/environment bindings, and claim the saved
plan once. They enforce execute mode, execute-lower-env (or break-glass) tier,
direct-allowed approval mode and the environment allowlist for apply; production
apply is refused. Host authorization must be deliberately configured by the
operator owner; never change it to work around a refusal. Respect edition
refusals from tools, but do not claim first-install has an enforced edition
check: this AWS provisioning path does not check edition.

Continue with the same installationId through handoff and verification. Progress
survives process restarts on the same host and authorized target; retain its
private state and evidence. A written handoff or infrastructure-provisioned
status alone is not readiness. Only report installation complete after
install-handoff-verified and stage complete. Distinguish retained historical
verification from a fresh health check. On failure report the actual refusal and
stage; never retry a spent or indeterminate plan, delete state or tear down
resources. Reconcile first; an expired/spent plan needs an explicitly new install,
a newly reviewed plan and fresh approval, not an automatic destructive retry.

## Report and hand off

Report only verified evidence: target, account and region to the user privately,
operator and IaC versions, server image, plan digest, apply receipt, endpoints,
checks performed, evidence locations and the documented teardown procedure.
Do not execute teardown without a separately reviewed destroy plan and approval.
Connect the client to the verified installed server's `/mcp` endpoint using its
documented scoped authentication. Day-2 observation and proposals belong there;
honua-devops is the provisioning operator. Server proposals require separate
principal approval through the server's approval API. Preserve preview/release
qualifications; provisioning does not qualify a server image for production.
