# Compatibility-Train Conformance Gate

The **producer** half of the cross-repo compatibility train (`honua-devops#68`,
epic `honua-io/geospatial-grpc#18`).

Given a **candidate** under test — a server image/version (e.g.
`honua-server:<rc-tag>`) or an SDK version — this gate actively runs **every
consumer SDK's shared-fixture conformance against that candidate**, collects the
per-repo green/break verdict, and **BLOCKS the bump** (non-zero exit) if any
consumer breaks. It emits the verdict in the evidence shape that
`scripts/compat-train-release-gate.sh` (`honua-devops#41`) already consumes, so
the two compose:

```
compat-train-conformance-gate.sh  ── evidence.json ──▶  compat-train-release-gate.sh (#41)
        (produces the verdict)                                (evaluates the verdict)
```

`#41` was a passive evaluator: it trusted each repo's `status` on faith. This
gate is the missing producer of that `status` — it executes the consumers
against the candidate rather than taking the verdict as given.

## Why this gate exists

`honua-server#1238` was a server data-projection change that broke mobile
FeatureServer/OGC reads **in the field, caught by users — not CI**. The
JSONB-attribute projection shape is one of the canonical fixtures every consumer
round-trips here, so this gate is the mechanism that would have stopped that
candidate from being promoted to consumers.

## Default-safe posture (per `AGENTS.md`)

The gate only **plans/evaluates**. It dispatches read-only conformance runs and
evaluates their verdicts; it never deploys, submits, promotes, or rolls back the
candidate. A break **blocks** the promotion path — it does not roll anything
back.

## The single source of truth: pinned shared fixtures

The shared `geospatial-grpc` conformance fixtures (`geospatial-grpc#3`:
`conformance/fixtures` + `conformance/golden` + `run.sh`, language-agnostic
`buf convert` round-tripping) are the single source of truth. They are **pinned
by version, never copied**. The gate refuses to run on an unpinned (`latest` or
empty) fixtures version (`exit 2`) so the candidate is always validated against
a known canonical contract revision. The pin must equal each consumer's own
pinned `CONFORMANCE_FIXTURES_VERSION`.

## Consumer registry

`compatibility/consumers.conformance.json` is the train registry. Each consumer
entry maps the repo to its conformance workflow and the dispatch input that
targets a candidate:

| Consumer | Workflow | Candidate input | Enrolled |
| --- | --- | --- | --- |
| `honua-sdk-dotnet` | `conformance.yml` | `server_image` | yes |
| `honua-sdk-js` | `integration.yml` | `base_url` (+ `server_commit`) | yes |
| `honua-sdk-python` | `conformance.yml` | `server_image` | yes |
| `honua-mobile` | `live-server-integration.yml` | `honua_server_image` | yes |
| `honua-qgis-plugin` | _(none yet)_ | — | no |

An **unenrolled** consumer (no conformance workflow yet, e.g. `honua-qgis-plugin`)
is skipped with an explicit note and is **not** treated as silently green. Set
`COMPAT_TRAIN_REQUIRE_ALL=true` to make an unenrolled consumer block instead.

## Known-expected server gaps (never fake green)

The registry's `known_server_gaps` mirrors the already-tracked nightly
`honua-server` gaps that the consumer conformance jobs `xfail` with explicit
issue references:

- `honua-server#1238` — FeatureServer/OGC JSONB-attribute projection
- `honua-server#1166` — temporal query support
- `honua-server#1167` — replica / offline-sync endpoints
- `honua-server#1237` — analysis (process) list/estimate endpoints

A consumer break whose **only** failing fixtures map to a tracked gap is reported
**green-with-known-gaps** — recorded in the evidence (`known_gaps[]`) and the
report, **never silently swallowed**. Any **new/untracked** failing fixture still
**BLOCKS**, even when it appears alongside a tracked gap (a gap can never mask
new drift). The gate never blanket-applies `continue-on-error` and never fakes
green. When a server fix lands, drop the issue from `known_server_gaps` and the
consumer flips its `xfail` to required.

## Modes

- **`dispatch`** (`COMPAT_TRAIN_DISPATCH=true`, needs `gh`): dispatch each
  enrolled consumer's conformance workflow against the candidate via
  `gh workflow run`, wait for completion, and read the conclusion. This is the
  `workflow_dispatch` lane in CI.
- **`results`** (default, offline): consume a pre-collected results JSON
  (`COMPAT_TRAIN_RESULTS_FILE`) and/or `COMPAT_TRAIN_REPO_<KEY>_STATUS` env
  overrides. This is how the smoke self-test and offline CI exercise every
  pass/break path with no live cluster or `gh` auth.

Results JSON shape (per repo):

```json
{
  "repos": {
    "honua-mobile": {
      "conclusion": "failure",
      "base_url": "ghcr.io/honua-io/honua-server:2026.06.0-rc.1",
      "commit": "rc1abc",
      "local_stack": false,
      "failing_fixtures": [
        { "fixture": "feature_query_response.json", "field": "features[].attributes", "gap_issue": "honua-server#1238" }
      ]
    }
  }
}
```

`gap_issue` is the tracked issue the failing fixture belongs to (or `null` for
new drift). A `failure` with no per-fixture detail is treated as a hard,
untracked break — never silently passed.

## Configuration

| Variable | Default | Purpose |
| --- | --- | --- |
| `COMPAT_TRAIN_CANDIDATE_IMAGE` | _(none)_ | Candidate server image/tag (one of image/version required) |
| `COMPAT_TRAIN_CANDIDATE_VERSION` | _(none)_ | Candidate SDK version label |
| `COMPAT_TRAIN_CANDIDATE_COMMIT` | _(none)_ | Candidate server commit (passed to `server_commit`-style inputs) |
| `COMPAT_TRAIN_FIXTURES_VERSION` | _(required)_ | Pinned geospatial-grpc fixtures version; `latest`/empty rejected |
| `COMPAT_TRAIN_REPOS` | every enrolled consumer | Space-separated train set |
| `COMPAT_TRAIN_REQUIRE_ALL` | `false` | Make unenrolled consumers block |
| `COMPAT_TRAIN_DISPATCH` | `false` | `true` dispatches live consumer runs via `gh` |
| `COMPAT_TRAIN_RESULTS_FILE` | _(none)_ | Pre-collected per-repo results (results mode) |
| `COMPAT_TRAIN_CONSUMER_REGISTRY` | `compatibility/consumers.conformance.json` | Train registry |
| `COMPAT_TRAIN_EVIDENCE_OUTPUT` | _(none)_ | Write the `#41`-consumable evidence JSON |
| `COMPAT_TRAIN_REPORT_OUTPUT` | _(none)_ | Write the human-readable report |
| `COMPAT_TRAIN_DISPATCH_REF` | `trunk` | Branch to dispatch consumer workflows on |
| `COMPAT_TRAIN_DISPATCH_TIMEOUT` | `2400` | Seconds to wait per consumer run |

## Usage

```bash
# Live: validate an RC server image against every enrolled consumer and produce
# the evidence the #41 release gate consumes.
COMPAT_TRAIN_DISPATCH=true \
COMPAT_TRAIN_CANDIDATE_IMAGE=ghcr.io/honua-io/honua-server:2026.06.0-rc.1 \
COMPAT_TRAIN_FIXTURES_VERSION=0.1.0-alpha.1 \
COMPAT_TRAIN_EVIDENCE_OUTPUT=conformance-evidence.json \
  ./scripts/compat-train-conformance-gate.sh

# Then evaluate the produced evidence with the existing #41 release gate:
COMPAT_TRAIN_MODE=any ./scripts/compat-train-release-gate.sh conformance-evidence.json
```

## CI

`.github/workflows/compat-train-conformance-gate.yml`:

- On PR/push: `bash -n` + the paired `scripts/smoke-compat-train-conformance-gate.sh`
  self-test prove the gate itself is correct (pass path + every block path),
  fully offline.
- On `workflow_dispatch` (`candidate_image`/`candidate_version` +
  `fixtures_version`): the gate dispatches every enrolled consumer's conformance
  against the candidate, uploads the evidence + report, and blocks on any break.
  Wire this dispatch job as a required check on the promotion path so a breaking
  candidate cannot be promoted.

The paired self-test covers: all-green PASS; an untracked consumer break;
the `#1238` JSONB-projection shape presented as untracked drift; a
known-expected gap-only run (PASS, recorded); new drift alongside a tracked gap
(BLOCK); missing candidate (`exit 2`); missing/`latest` fixtures (`exit 2`); a
missing required-consumer verdict; env-status override; and that the produced
evidence is accepted by the `#41` release gate.
