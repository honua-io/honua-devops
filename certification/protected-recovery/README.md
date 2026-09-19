# Protected recovery: live journey

Live proof for honua-devops#191, carrying honua-release#321 box 3. It runs against a booted honua-server candidate whose protected activation is real. The server itself:

- launches the workload replicas through the host container runtime (`honua-yarp-rolling`);
- swaps its embedded proxy at cutover;
- retains the prior replica through the post-activation observation window;
- runs its telemetry gate and recovery fence.

The harness only injects faults and records what the server, the replicas and the proxy report.

| file | role |
| --- | --- |
| `boot.sh` | `up` / `restart` / `down`: PostGIS, Redis, a Prometheus query stub, three workload images with distinct image ids, and the server with one `SelfHostedRolling` target and a static-key OIDC issuer for tenant-bound principals. Secrets are minted per run into `$PROOF_WORK`, outside the repository. |
| `telemetry-stub/stub.py` | Prometheus query API whose answers are the injected fault: healthy, breach, empty or stale. |
| `workload/` | The workload being rolled. Each build bakes a distinct revision. |
| `journey.py` | Server journey cells. Records every request, response, phase transition and replica/proxy snapshot. |
| `summarize.py` | Merges run parts and the DevOps transcript into `receipt-<tag>.json` and `RECEIPT.md`. |
| `receipt-nightly-80e23be.json`, `devops-live-nightly-80e23be.jsonl`, `devops-live-nightly-80e23be.trx`, `RECEIPT.md` | Current receipt and test-runner verdict. The d1fc139 artifacts retain the original failing cross-tenant evidence. |

## Run

```bash
export PROOF_WORK=/path/outside/the/repo/proof-work
PROOF_SERVER_IMAGE=ghcr.io/honua-io/honua-server@sha256:9869f044b1c5d0de15aef6c87cc3d60383037ee9a4346d08bb9c83f2c56cc176 certification/protected-recovery/boot.sh up

# Server journey. Each cell takes 1-2 minutes; split the list across invocations as needed.
python3 certification/protected-recovery/journey.py --work "$PROOF_WORK" --out part1.json \
  --cells platform-admin-cross-tenant,fenced-recovery,tenant-bound-recovery,error-rate-regression,latency-regression

# DevOps code against the same server (opt-in; never runs in CI).
HONUA_DEVOPS_LIVE_PROTECTED_RECOVERY=true \
HONUA_DEVOPS_LIVE_PROTECTED_RECOVERY_WORK="$PROOF_WORK" \
HONUA_DEVOPS_LIVE_PROTECTED_RECOVERY_RECEIPT=devops-live.jsonl \
HONUA_DEVOPS_HONUA_API_BASE_URL=http://127.0.0.1:19191 \
HONUA_DEVOPS_HONUA_API_KEY="$(cat "$PROOF_WORK/admin-key")" \
dotnet test tests/Honua.DevOps.Agent.Tests --filter "FullyQualifiedName~ProtectedRecoveryLiveJourneyTests" \
  --logger "trx;LogFileName=protected-recovery-live.trx"

certification/protected-recovery/boot.sh down
```

`platform-admin-cross-tenant` is a required regression: the two original #4987
exploits must return exactly 403 `recovery_fence_actor_mismatch` without changing
the operation or replicas, and a same-name actor in a different tenant must get
`recovery_fence_tenant_mismatch`. It checks redaction of sealed identity on list
and detail reads, then proves the sealed principal can still restore the prior
revision through the real proxy. `wrong-body-private-probe` remains a separate
diagnostic for honua-server#4988, not a recovery qualification cell.

Run all eleven required cells and all five live .NET scenarios on the same pinned
image. `summarize.py` requires `--devops-trx` as well as `--devops`: transcripts
alone are written even after failed assertions. It rejects missing, duplicate or
failed recovery classes, a wrong inspected image identity, and missing or failed
live test results. The live flag must be set; a default no-op test run has no
matching transcripts and cannot produce a qualified receipt.

## Environment notes

- The server container gets the host's `docker` CLI and socket, and runs as root so it can use the socket. Replicas publish on the host, and the server reaches them through `host.docker.internal`.
- Operation policy (`appsettings.Production.json`) denies operations by default. The proof adds one explicit rule admitting `control-plane.deploy.rollback`, which is what reaches the fence.
- The embedded proxy destination is process state. After a committed candidate, the driver restarts the server so the proxy re-derives the active replica.
