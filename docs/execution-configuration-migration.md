## Trusted execution configuration and cwd dotenv migration

Working-directory `.env` and `.env.local` are untrusted workspace input. Automatic loading
accepts only `HONUA_DEVOPS_PROVIDER` and these exact model inputs: CODEX, CLAUDE and
LOCAL_LLAMA `MODEL`, `API_KEY`, `ENDPOINT`; BEDROCK `MODEL`, `API_KEY`, `REGION` under
`HONUA_DEVOPS_`. Unix names are case-sensitive; Windows names are case-insensitive.
Existing process values win, then `.env.local` overrides allowed `.env` values.
Unknown keys are ignored, including execution/approval settings, backend URLs/credentials,
audit destinations, issuer keys, substrate/tool paths, PATH, BASH_ENV and AWS/child settings.
There is no provider-prefix wildcard. Model inputs still require the immutable process-only
provider disclosure approval; changing a route/model does not expand that approval.

Configure execution authority in the operator-controlled launcher environment (service
manager, MCP client launcher or CI secret/config injection). Review it outside the workspace;
do not source repository `.env` files as shell scripts or copy arbitrary dotenv keys into
the process environment. Process settings remain trusted administrator input, not a sandbox
against a compromised launcher. Retain the existing plan/pr-first defaults unless separately
authorized. Explicitly configure backend, audit/receipt sinks, approvals, tools and child
credentials needed by the selected operation.

`HONUA_DEVOPS_TERRAFORM_LOCAL_PATH` must name an explicit absolute trusted substrate checkout.
The host no longer discovers `../honua-iac` relative to cwd or accepts a relative configured
path. A missing path remains unconfigured so help, tool listing and MCP catalog hosting work;
provisioning refuses before launching the substrate when it is needed. Selecting a trusted
path is not artifact-provenance or installed-qualification evidence.

`bootstrap-operator-env.sh` still writes configuration data for review; only model keys are
auto-loaded. It warns that execution/backend/audit settings need a trusted launcher. Its
explicit `--run-preflight` passes reviewed script arguments as process environment values,
without sourcing the generated file. Supply an absolute `--terraform-local-path` if needed;
no sibling checkout is inferred. Customer bootstrap uses this same path. Existing model
approval, installed handoff authority and intended tool surface are unchanged.

Both MCP and interactive hosts now audit at the actual function invocation boundary before
serializing results for clients/models. Private typed mutation/actuation facts remain out of
wire JSON. An audit sink failure remains a failed invocation with the existing durable recovery
barrier; client-supplied or streamed function-result content does not establish a mutation.
This source repair does not qualify an installed candidate or revive retired
tools, change release gates, or authorize provider/cloud calls.
