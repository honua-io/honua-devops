# NVIDIA NIM Deployment Guide

`honua-devops` ships a first-class `local-llama` provider that talks to any
OpenAI-compatible inference endpoint, with NVIDIA NIM (NVIDIA Inference
Microservice) as the primary target. This document covers env-var setup,
hosted developer-tier usage on `build.nvidia.com`, self-hosted NIM containers,
AWS Marketplace provisioning, and a troubleshooting checklist.

The `local-llama` provider name applies to NIM-hosted Nemotron, Llama, Qwen,
and the upcoming Honua-GIS-32B variants, as well as to non-NIM OpenAI-
compatible local stacks (vLLM, Ollama, TGI). The same env vars and audit
shape apply regardless of where the endpoint runs.

## 1. Provider env vars

Set the following on the host running `honua-devops` (or via `.env` /
`.env.local` — `.env.local` overrides `.env`, and process env wins over both):

```bash
HONUA_DEVOPS_PROVIDER=local-llama
HONUA_DEVOPS_LOCAL_LLAMA_MODEL=meta/llama-3.3-70b-instruct
HONUA_DEVOPS_LOCAL_LLAMA_API_KEY=<NIM-or-gateway-key>
HONUA_DEVOPS_LOCAL_LLAMA_ENDPOINT=https://integrate.api.nvidia.com/v1
```

- `MODEL` is the NIM model id (e.g. `meta/llama-3.3-70b-instruct`,
  `nvidia/nemotron-4-340b-instruct`, `qwen/qwen-2.5-7b-instruct`,
  `honua/honua-gis-32b` once published).
- `API_KEY` is the NVAPI key from the NVIDIA developer portal for hosted
  endpoints, or the gateway / proxy key for self-hosted deployments.
- `ENDPOINT` is the OpenAI-compatible base URL (must end before
  `/chat/completions`). HTTPS is required for non-loopback hosts;
  `http://localhost:*` is permitted for self-hosted loopback NIM.

Confirm the wiring without a live LLM call:

```bash
dotnet run --project src/Honua.DevOps.Agent -- --list-tools
HONUA_DEVOPS_PROVIDER=local-llama dotnet run --project src/Honua.DevOps.Agent -- --preflight
```

Run a single-turn end-to-end against the configured endpoint:

```bash
dotnet run --project src/Honua.DevOps.Agent -- \
  --provider local-llama \
  --prompt "describe the environment"
```

## 2. Hosted: build.nvidia.com developer tier

1. Sign in at <https://build.nvidia.com> with an NVIDIA developer account.
2. Pick a model (Nemotron / Llama / Qwen) — the model card has an
   OpenAI-compatible code sample.
3. Copy:
   - the model id into `HONUA_DEVOPS_LOCAL_LLAMA_MODEL`,
   - your NVAPI key into `HONUA_DEVOPS_LOCAL_LLAMA_API_KEY`,
   - the API base URL (typically `https://integrate.api.nvidia.com/v1`) into
     `HONUA_DEVOPS_LOCAL_LLAMA_ENDPOINT`.
4. Run `honua-devops --provider local-llama --prompt "describe the environment"`.

The developer tier has rate limits and is meant for evaluation. For production
or air-gapped workloads, see §3 / §4.

## 3. Self-hosted NIM container

The canonical pattern is the NVIDIA-published NIM container running on a
GPU host (single-GPU dev workstation, AKS / EKS with NVIDIA GPU node pool,
or NVIDIA AI Enterprise on-prem). The container exposes an
OpenAI-compatible `/v1/chat/completions` endpoint on port 8000.

```bash
# Pull and run a NIM container (replace the image tag with the model you want).
docker run -d --name honua-nim \
  --gpus all \
  --shm-size=16g \
  -e NGC_API_KEY="$NGC_API_KEY" \
  -p 8000:8000 \
  nvcr.io/nim/meta/llama-3.3-70b-instruct:latest
```

Then point honua-devops at the local container:

```bash
HONUA_DEVOPS_LOCAL_LLAMA_MODEL=meta/llama-3.3-70b-instruct
HONUA_DEVOPS_LOCAL_LLAMA_API_KEY=any-non-empty-string   # NIM container uses
                                                       # this header only when
                                                       # a gateway is in front
HONUA_DEVOPS_LOCAL_LLAMA_ENDPOINT=http://localhost:8000/v1
```

Loopback HTTP is allowed by the endpoint validator. For remote / on-prem hosts
expose the container behind TLS (nginx, Caddy, an envoy sidecar, or an
ingress with cert-manager) and use the HTTPS URL.

## 4. AWS Marketplace NIM provisioning

The NVIDIA AI Enterprise / NIM offering on AWS Marketplace is the
recommended path for state and local government customers in
sovereignty-required environments:

1. Subscribe to the **NVIDIA AI Enterprise** product on AWS Marketplace and
   accept the EULA in your AWS account.
2. Pick the deployment shape:
   - **Single-instance**: launch an EC2 instance from the NIM AMI (g5 / g6 /
     p4d / p5 depending on model size) with a GPU-enabled NVIDIA driver
     baked in. SSH in and `docker run` the NIM container.
   - **EKS**: install the NVIDIA-published Helm chart into a cluster with a
     GPU node pool (`g5.4xlarge` for 70B models, `p4d.24xlarge` for larger).
3. Front the NIM service with an internal ALB / NLB or VPC endpoint. Do
   **not** expose NIM directly on a public listener — terminate TLS at the
   load balancer and require an API key at the gateway.
4. Issue a per-operator API key on the gateway and set:
   - `HONUA_DEVOPS_LOCAL_LLAMA_ENDPOINT=https://nim.<your-domain>/v1`
   - `HONUA_DEVOPS_LOCAL_LLAMA_API_KEY=<gateway key>`
5. For air-gapped / sovereignty deployments, mirror the NIM container into
   an internal ECR and pull from there; honua-devops requires no external
   network beyond the configured endpoint.

## 5. Honua-GIS-32B model card

Honua-GIS-32B is the fine-tuned, domain-specific NIM target that the
`local-llama` provider was designed to feed.

- TODO: link to the published Honua-GIS-32B model card once
  [honua-io/honua-sdk-python#64](https://github.com/honua-io/honua-sdk-python/issues/64)
  ships the model.
- Until then, point `HONUA_DEVOPS_LOCAL_LLAMA_MODEL` at a stock NIM model
  (`meta/llama-3.3-70b-instruct` is the closest base) and treat results as
  the portability baseline.

## 6. Troubleshooting

| Symptom | Likely cause | Fix |
| --- | --- | --- |
| `Missing environment variable HONUA_DEVOPS_LOCAL_LLAMA_MODEL/API_KEY` | One of the required vars is unset or empty. | Set all three vars and re-run. `.env.local` overrides `.env`. |
| `Environment variable HONUA_DEVOPS_LOCAL_LLAMA_ENDPOINT must use https for non-local endpoints.` | Remote endpoint set with plain HTTP. | Front the NIM with TLS. Loopback HTTP (`http://localhost:*`, `127.0.0.1`) is permitted. |
| 401 / 403 from the endpoint | API key wrong, expired, or scoped to a different tenant. | Re-issue the NVAPI / gateway key. NIM gateway keys are header-only — bearer scheme. |
| 404 on `/chat/completions` | Endpoint missing `/v1` suffix or the model id is wrong. | Confirm the endpoint ends in `/v1` and the model id matches what the NIM container advertises (`curl <endpoint>/models`). |
| TLS cert errors against on-prem NIM | Self-signed cert not trusted by the host. | Add the cert to the OS trust store (`update-ca-certificates`) or use a publicly trusted cert. honua-devops trusts whatever the host trusts. |
| Audit JSONL field looks different from Codex / Claude sessions | None expected — the `AuditRecord` shape is provider-agnostic. `Provider` field reads `local-llama` for NIM, `codex` for Codex, `claude` for Claude. | If you see a different shape, file an issue against this repo. |
| `--list-tools` works but `--prompt` hangs | NIM container still loading the model into VRAM. | Wait for the container's `Ready` log line or `/v1/models` to return the model id. |

## 7. CI smoke test

`tests/Honua.DevOps.Agent.Tests/LocalLlamaProviderSmokeTests.cs` exercises
the provider against a recorded NIM chat-completion fixture
(`tests/Honua.DevOps.Agent.Tests/fixtures/nim-chat-completion.json`).
The smoke test:

- spins up an in-process HTTP handler so no external network is touched,
- asserts the captured request hits `https://nim.test/v1/chat/completions`,
- asserts the request carries `Authorization: Bearer <configured key>`,
- asserts the request body includes the configured model id,
- asserts the emitted `AuditRecord` field set is identical to a Codex
  session and that `Provider` is the kebab string `local-llama`.

Run it locally with:

```bash
dotnet test tests/Honua.DevOps.Agent.Tests/Honua.DevOps.Agent.Tests.csproj \
  --filter FullyQualifiedName~LocalLlamaProviderSmokeTests
```

A live nightly run against the build.nvidia.com developer tier is a manual
artifact for now (the CI environment intentionally has no NIM credentials).
