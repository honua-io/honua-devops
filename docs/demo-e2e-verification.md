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

# Serving writes, with a SEPARATE staging target and admin key injected by CI:
HONUA_DEMO_BASE_URL=https://demo.honua.io \
HONUA_DEMO_WRITE_BASE_URL=https://staging.honua.io \
  ./scripts/run-demo-e2e.sh --env demo
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
    customer alias. Serving teardown runs before the receipt is finalized, including on failed assertions. Cleanup failure fails the run.
- **`pro_and_ai_live` flag (default false).** Pro/Bedrock/export
  assertions are gated as *expected-pending* until the Pro + Bedrock
  deploys land. The read path always runs.

## Per-hop assertions (real values, not eyeballed)

### Demo A — AI GIS workflow
| Hop | Assertion | Gating |
| --- | --- | --- |
| `demoA.catalog` | `rest/info` 200 + `currentVersion` | always (read) |
| `demoA.import` | import one point with FeatureServer `applyEdits`; read back exactly one record with the returned object ID, marker, and coordinates **(-156.5, 20.8)** | write target + admin key |
| `demoA.cleanup` | delete owned fixture IDs; query confirms **`features: []`**, zero records | after any attempted serving write, including denial |
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

### Demo B — serving rollback (staging write target ONLY)
| Hop | Assertion | Gating |
| --- | --- | --- |
| `demoA.write_preflight` | layer metadata has an object ID and the configured string marker field; fixture namespace is empty | write target + admin key |
| `demoB.rollback` | `applyEdits` with `rollbackOnFailure=true`: valid fixture update followed by an invalid update without an object ID; both edits fail, at least one carries **1008** (operation rolled back); captured layer metadata **and actual feature data** are unchanged | write target + admin key |
| `auth.no_key` | unauthenticated fixture creation refused with **401**; response has no data; authenticated readback is **`features: []`** | write target + admin key |
| `auth.read_only` | same request with staging read-only key refused with **403**, same empty-data assertions | additionally `HONUA_DEMO_READ_ONLY_KEY` |

The serving contract uses actual honua-server routes:
`GET /rest/services/{service}/FeatureServer/{layer}`, `GET .../query`, and
`POST .../applyEdits`. Import here means importing a fixture record through the
serving API. It does **not** certify the asynchronous file-import control plane.
The previous `/api/v1/import/jobs` and `/api/v1/devops/proposals` sketches were
not honua-server routes. Their proposed `Succeeded`/`RolledBack` operation
lifecycle is not claimed by this receipt. Deployment proposal approval and
failure-driven deployment rollback need a separate control-plane certification.
This harness never invokes AWS fault-injection scripts.

Denial accepts exactly either the expected HTTP **401/403 with a zero-byte
body**, or Honua's GeoServices **HTTP 200 with an error-only envelope** whose
`error.code` is respectively 401/403. A JSON envelope may contain only `error`
(with `code`, `message`, `details`); features, edit results, or other data fail
validation. The receipt records the actual HTTP status, refusal code, byte
count, body kind, and authenticated empty feature array. A 404, wrong refusal
code, malformed JSON, or successful write is a failure. The GeoServices form
is supported because honua-server encodes protocol errors in HTTP 200 bodies;
it is not described as a literally empty HTTP body.

The caller must provision a **separate disposable staging point layer** with a
writable string field (default `name`) long enough for the prefix plus a UUID
and rollback suffix. Its schema must accept the two fixture attributes/geometry
without additional required fields. The staging key must allow query, create,
update, and delete; the optional read-only key must allow query and deny writes.
The harness owns only records carrying its random per-run marker, including
the temporary rollback marker. Repeated runs with the same resource prefix are
safe and leave zero fixture records. Concurrent runs receive different markers.
Cleanup is armed before the first denial request, so even incorrectly accepted
unauthorized writes or an import response lost after creation are cleaned up.
SIGINT/SIGTERM enter cleanup; SIGKILL/host loss or an unavailable staging server
can prevent cleanup. Retain the receipt/output and dispose of the staging
target after such a failure.

## Cross-repository workflow contract (Lambda certification)

Check out a pinned revision of `honua-devops` alongside the calling repository.
Invoke the script by path from any working directory; keep its sibling Python
helper. Requirements: Bash, Python 3 (standard library only), curl, and standard
Unix utilities. No .NET build, AWS CLI, AWS credentials, or Honua CLI is required.
The optional Honua CLI is used only for the existing read count probe.

```bash
# All URL/config values and keys are injected by the calling workflow.
bash "$GITHUB_WORKSPACE/honua-devops/scripts/run-demo-e2e.sh" \
  --env lambda-certification \
  --base-url "$HONUA_DEMO_BASE_URL" \
  --output-dir "$RUNNER_TEMP/lambda-serving" \
  --json-summary "$RUNNER_TEMP/lambda-receipt/serving.json"
```

Required inputs are `--env` / `HONUA_DEMO_ENV` and `--base-url` /
`HONUA_DEMO_BASE_URL`. Optional `--write-base-url` /
`HONUA_DEMO_WRITE_BASE_URL` plus `HONUA_DEMO_ADMIN_KEY` enable serving writes
without `--pro-ai-live`. Omit either to leave serving write/denial hops PENDING.
`--output-dir` / `HONUA_DEMO_OUTPUT_DIR` selects the artifact directory;
`--json-summary <path>` copies the final JSON receipt to an explicit path,
creating parent directories. It works on both assertion success and failure,
and can name the evidence file itself. Paths are relative to the caller's cwd.
Keys should be passed through environment variables, not command arguments.

Write fixtures default to the read service/layer IDs (`--service-id`,
`--layer-id`), with independent overrides `HONUA_DEMO_WRITE_SERVICE_ID`,
`HONUA_DEMO_WRITE_LAYER_ID`, and `HONUA_DEMO_WRITE_MARKER_FIELD`. Configure these
to the disposable staging layer. `HONUA_DEMO_RESOURCE_PREFIX` labels each run;
`--timeout-seconds` / `HONUA_DEMO_TIMEOUT_SECONDS` bounds each HTTP request.
Write requests use only the admin key; denial requests explicitly use no key or
the staging read-only key. Redirects are refused for all serving write requests.
Distinct URL strings cannot prove environment isolation: callers must supply a
staging alias that does not resolve to the customer deployment.

The receipt (`schema_version: 1`) contains `status`, `counts`, input target
labels, and `hops[]` with stable `id`, `workflow`, `status`, `driver`, `detail`,
and `asserted_values` fields. Serving hops expose typed numeric, boolean, and
array values, including object ID, coordinates, rollback error code, equality
checks, and zero-record results. Existing read hops retain their asserted
values in `detail`. No keys or authorization headers are emitted.

Exit codes: **0** means every executed assertion passed (PENDING is allowed),
**1** is a configuration error, **2** means an assertion/cleanup failed.
Configuration errors may occur before a receipt exists. A Lambda lane requiring
write certification must require `demoA.import`, `demoB.rollback`,
`demoA.cleanup`, and `auth.no_key` to be **PASS**, plus `auth.read_only` when its
key is supplied. Do not treat exit 0 alone as proof that writes ran. Upload the
output directory and summary with `if: always()`.

## Assertions gated behind `pro_and_ai_live`

These are **expected-pending** until the Pro + Bedrock deploys land (they
additionally require a distinct staging write target and admin key):

- `demoA.ai_generate` (Bedrock proposal `Generated` + real graph)
- `demoA.publish`
- `demoA.export.png` / `demoA.export.pdf` (byte-level `%PDF` / PNG magic)

Everything else (`demoA.catalog`, `demoA.query`, `demoA.features`,
`demoA.render`, and the entire read-only publish-all matrix) **always runs**.

## Secret locations (no secret values in this repo)

Names only: injected at runtime from the calling repository/org or its staging GitHub environment. This lane does not provision or read secret values. The existing daily job remains a read-only lane; Lambda certification supplies its own staging inputs:

| Var | Kind | Purpose |
| --- | --- | --- |
| `HONUA_DEMO_BASE_URL` | repo variable | customer-facing read base URL |
| `HONUA_DEMO_API_KEY` | secret | optional read API key (`X-API-Key`) |
| `HONUA_DEMO_WRITE_BASE_URL` | repo variable | staging/ephemeral write target |
| `HONUA_DEMO_ADMIN_KEY` | repo/org or calling workflow environment secret | admin/write key for the staging target only |
| `HONUA_DEMO_READ_ONLY_KEY` | repo/org or calling workflow environment secret | optional staging query-only key for the 403 denial check |
| `HONUA_DEMO_WRITE_SERVICE_ID`, `HONUA_DEMO_WRITE_LAYER_ID`, `HONUA_DEMO_WRITE_MARKER_FIELD` | calling workflow variables | disposable staging fixture layer and string marker field |
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

## Historical read-path evidence

Previously run on `demo.honua.io` (read/query/render/OGC already green at v18). Pro/AI/
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

## Offline self-test

Run `./scripts/smoke-demo-e2e.sh`. It clears inherited demo configuration and
starts two local HTTP stubs on dynamically allocated loopback ports. Cases cover
read pass/fail, the equal-URL guardrail (zero mutations), missing config, write
pass, repeated runs, both empty-body and GeoServices denials, bad value readback,
import failure after persistence, rollback data loss, wrong denial status,
leaked denial data, accepted unauthorized writes, and cleanup failure. It
compares the exported summary with the evidence and checks the stub has no
remaining fixture records except in the deliberately broken cleanup case.
These are harness contract tests, not evidence of a live staging deployment.
