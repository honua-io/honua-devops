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
| `receipt-nightly-d1fc139.json`, `devops-live-nightly-d1fc139.jsonl`, `RECEIPT.md` | The committed receipt. |

## Run

```bash
export PROOF_WORK=/path/outside/the/repo/proof-work
PROOF_SERVER_IMAGE=ghcr.io/honua-io/honua-server:nightly-d1fc139 certification/protected-recovery/boot.sh up

# Server journey. Each cell takes 1-2 minutes; split the list across invocations as needed.
python3 certification/protected-recovery/journey.py --work "$PROOF_WORK" --out part1.json \
  --cells fenced-recovery,tenant-bound-recovery,error-rate-regression,latency-regression

# DevOps code against the same server (opt-in; never runs in CI).
HONUA_DEVOPS_LIVE_PROTECTED_RECOVERY=true \
HONUA_DEVOPS_LIVE_PROTECTED_RECOVERY_WORK="$PROOF_WORK" \
HONUA_DEVOPS_LIVE_PROTECTED_RECOVERY_RECEIPT=devops-live.jsonl \
HONUA_DEVOPS_HONUA_API_BASE_URL=http://127.0.0.1:19191 \
HONUA_DEVOPS_HONUA_API_KEY="$(cat "$PROOF_WORK/admin-key")" \
dotnet test tests/Honua.DevOps.Agent.Tests --filter "FullyQualifiedName~ProtectedRecoveryLiveJourneyTests"

certification/protected-recovery/boot.sh down
```

Two cells record server defects rather than passes: `wrong-body-private-probe` (honua-server#4988) and `platform-admin-cross-tenant` (honua-server#4987). The second is expected to fail until the server fixes it.

## Environment notes

- The server container gets the host's `docker` CLI and socket, and runs as root so it can use the socket. Replicas publish on the host, and the server reaches them through `host.docker.internal`.
- Operation policy (`appsettings.Production.json`) denies operations by default. The proof adds one explicit rule admitting `control-plane.deploy.rollback`, which is what reaches the fence.
- The embedded proxy destination is process state. After a committed candidate, the driver restarts the server so the proxy re-derives the active replica.
