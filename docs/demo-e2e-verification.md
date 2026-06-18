# Deployed-Environment Demo Verification Harness (L3)

This is the **L3 verification layer**: it proves the full, customer-facing demo
workflows actually work against the **deployed** demo environment
(`demo.honua.io` — real Lambda + Postgres + real Maui data), not merely that
per-PR unit tests pass.

Cross-refs: **honua-devops#97** (demo program), **honua-devops#98** (publish-all
matrix).

- Harness: [`scripts/run-demo-e2e.sh`](../scripts/run-demo-e2e.sh)
- Self-test: [`scripts/smoke-demo-e2e.sh`](../scripts/smoke-demo-e2e.sh)
- CI: [`.github/workflows/demo-e2e.yml`](../.github/workflows/demo-e2e.yml)

## One command

```bash
# Read-path against the live customer-facing demo (PENDING for Pro/AI/Demo B):
HONUA_DEMO_BASE_URL=https://demo.honua.io ./scripts/run-demo-e2e.sh --env demo

# Full suite once Pro + Bedrock land, with a SEPARATE staging write target:
HONUA_DEMO_BASE_URL=https://demo.honua.io \
HONUA_DEMO_WRITE_BASE_URL=https://staging.honua.io \
HONUA_DEMO_ADMIN_KEY=*** \
  ./scripts/run-demo-e2e.sh --env demo --pro-ai-live
```

The harness prints a per-workflow / per-hop **PASS / FAIL / PENDING** table with
the real asserted values, writes an evidence bundle to
`artifacts/demo-e2e/<env>-<UTC>/` (`demo-e2e-evidence.json` + `demo-e2e-report.md`),
and **exits non-zero (2) on any non-pending failure**. PENDING hops never fail
the run.

## Hard design rules (target is a real, customer-facing env)

- **Parameterized, never hardcoded.** Base URL, env name and auth come from
  flags / env vars at runtime. **No secrets in source** — see *Secret locations*.
- **Read vs write split.**
  - READ-path checks (query, render, export-read, matrix probes, interop) run
    against **live `demo.honua.io`** (`HONUA_DEMO_BASE_URL`).
  - WRITE / destructive checks (Demo A import/publish; Demo B failure-injection
    + rollback) target a **separate non-customer-facing write target**
    (`HONUA_DEMO_WRITE_BASE_URL` — a `staging` Lambda alias / ephemeral env)
    under a scoped, self-cleaning `HONUA_DEMO_RESOURCE_PREFIX`.
  - **Guardrail:** if `HONUA_DEMO_WRITE_BASE_URL` equals the customer-facing
    read URL, all write/destructive hops are **REFUSED** and reported PENDING —
    the harness never injects a failure into, or leaves artifacts on, the
    customer alias. Teardown runs on EXIT (idempotent, self-cleaning).
- **`pro_and_ai_live` flag (default false).** Pro/Bedrock/export and Demo B
  write assertions are gated as *expected-pending* until the Pro + Bedrock
  deploys land. The read path always runs.

## Per-hop assertions (real values, not eyeballed)

### Demo A — AI GIS workflow
| Hop | Assertion | Gating |
| --- | --- | --- |
| `demoA.catalog` | `rest/info` 200 + `currentVersion` | always (read) |
| `demoA.import` | import → poll job to **`Succeeded`** | `pro_and_ai_live` + write target |
| `demoA.query` | FeatureServer count **≥ `--min-feature-count`** (real number) | always (read; honua CLI w/ HTTP fallback) |
| `demoA.features` | feature query returns a **non-zero feature array** | always (read) |
| `demoA.ai_generate` | Bedrock returns a **valid** proposal: `status=Generated` + a real graph (≥1 node), **not** `Unsupported`/error | `pro_and_ai_live` + write target |
| `demoA.publish` | publish artifact created | `pro_and_ai_live` + write target |
| `demoA.render` | MapServer `export` 200 + **valid PNG magic bytes** | always (read) |
| `demoA.export.png` / `.pdf` | export bytes are a **valid PNG header** / begin with **`%PDF`** | `pro_and_ai_live` + write target |

### Publish-all matrix (honua-devops#98) — read-only, per-protocol table
`FeatureServer.query`, `MapServer.export`, `WMS.GetMap`, `WMTS.tile`,
`OGCFeatures.items`, `STAC.catalog`, `OData.metadata`,
`GeocodeServer.findAddressCandidates`, `OGCTiles` — each probed and **validated**
(JSON shape / image magic / XML EDMX). A protocol that 404s (not yet published)
reports PENDING, not FAIL, so the table reflects the deploy frontier rather than a
false regression.

### Demo B — ops (staging write target ONLY)
| Hop | Assertion | Gating |
| --- | --- | --- |
| `demoB.proposal` | proposal → approve → submit → operation reaches **`Succeeded`** | `pro_and_ai_live` + write target |
| `demoB.rollback` | inject failing change/health-check → operation reaches **`RolledBack`** **AND** layer schema+data **actually reverted** (state captured before/after and diffed) | `pro_and_ai_live` + write target |

Demo B reuses the repo's fault-injection lifecycle in
[`scripts/fault-injection/`](../scripts/fault-injection/) (`FAULT-010-*`), honoring
`FAULT_DRY_RUN` and always running `restore` in teardown.

## Assertions gated behind `pro_and_ai_live`

These are **expected-pending** until the Pro + Bedrock deploys land (they
additionally require a distinct staging write target):

- `demoA.import` (import → job `Succeeded`)
- `demoA.ai_generate` (Bedrock proposal `Generated` + real graph)
- `demoA.publish`
- `demoA.export.png` / `demoA.export.pdf` (byte-level `%PDF` / PNG magic)
- `demoB.proposal` (proposal→approve→submit→`Succeeded`)
- `demoB.rollback` (fault → `RolledBack` + schema/data revert diff)

Everything else (`demoA.catalog`, `demoA.query`, `demoA.features`,
`demoA.render`, and the entire read-only publish-all matrix) **always runs**.

## Secret locations (no secret values in this repo)

Sourced from env at runtime only; in CI from repo/org configuration:

| Var | Kind | Purpose |
| --- | --- | --- |
| `HONUA_DEMO_BASE_URL` | repo variable | customer-facing read base URL |
| `HONUA_DEMO_API_KEY` | secret | optional read API key (`X-API-Key`) |
| `HONUA_DEMO_WRITE_BASE_URL` | repo variable | staging/ephemeral write target |
| `HONUA_DEMO_ADMIN_KEY` | secret | admin/write key for the write target |
| `HONUA_DEMO_E2E_ENABLED` | repo variable | opt-in switch for the scheduled live read-path job |

## Referenced gates (wired, not reimplemented)

The harness focuses on the deployed demo workflows. The following independent
gates cover adjacent surfaces and are **invoked / dispatched separately** — this
is how to trigger each and read pass/fail:

### honua-mobile — `live-server-integration.yml` (mobile edit→sync E2E)
Goes green once Pro is live. Full mobile edit→sync against a real seeded server.
```bash
gh workflow run live-server-integration.yml --repo honua-io/honua-mobile --ref trunk \
  -f honua_server_image=honuaio/honua-server:nightly
```
Read: `gh run list --workflow live-server-integration.yml --repo honua-io/honua-mobile`;
green = `LiveHonuaServerInteractionTests` passed (hard gate). Evidence artifact
`live-server-integration-<run_id>`.

### client-interop-nightly — real-client interop matrix
**Dispatch the FULL matrix.** A single lane (`-f lanes=gdal`) scopes strict mode
to that lane only and produces a **false pass** — always pass all five lanes.
```bash
gh workflow run client-interop-nightly.yml --repo honua-io/honua-server --ref trunk \
  -f lanes='gdal,pyqgis,openlayers,cesium,arcgis-stub'
```
Read: the `baseline-diff` job conclusion; green = no regressions vs
`tests/baselines/client-compat`. Evidence: `gap-report` + `evidence-client-compat-<lane>`.

### geobench — performance
```bash
gh workflow run benchmark-on-release.yml --repo honua-io/geobench --ref trunk \
  -f release_tag=<tag> -f regression_threshold=0.20
```
Read: run conclusion; green = all metrics (p50/p95/p99 latency, RPS, error rate,
cold-start) within the regression budget (default 20%). Evidence:
`geobench-results-<tag>-<ts>` + `report.md` in the job summary.

## Proof: read-path run against the current live demo

Run on `demo.honua.io` (read/query/render/OGC already green at v18). Pro/AI/
export/Demo-B hops correctly report PENDING (expected until deploy):

```
PASS=10  FAIL=0  PENDING=9   (exit 0)

demoA.catalog    PASS  rest/info 200, currentVersion=10.81
demoA.query      PASS  FeatureServer count=51245 (>= 1)
demoA.features   PASS  query returned 2 feature(s)
demoA.render     PASS  MapServer/export 200, valid PNG (51273 bytes)
matrix.FeatureServer.query  PASS  200 valid (application/json)
matrix.MapServer.export     PASS  200 valid (image/png)
matrix.OGCFeatures.items    PASS  200 valid (application/json)
matrix.STAC.catalog         PASS  200 valid (application/json)
matrix.OData.metadata       PASS  200 valid (application/xml)
matrix.OGCTiles             PASS  200 valid (application/json)
demoA.import / ai_generate / publish / export   PENDING (pro_and_ai_live=false)
matrix.WMS.GetMap / WMTS.tile / GeocodeServer   PENDING (not-yet-published, 404)
demoB.proposal / rollback                        PENDING (pro_and_ai_live=false)
```
