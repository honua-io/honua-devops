# Compatibility-Train Release Validation

Release-candidate validation for the cross-repo compatibility train
(`honua-devops#41`).

> **Producer + evaluator.** This gate is the *evaluator*: it consumes a per-repo
> verdict and decides if the train is releasable. The *producer* of that verdict
> — the gate that actively runs every consumer SDK's conformance against a
> candidate and emits the evidence shape consumed here — is
> `scripts/compat-train-conformance-gate.sh` (`honua-devops#68`); see
> [compat-train-conformance-gate.md](compat-train-conformance-gate.md). Run the
> conformance gate to produce `evidence.json`, then this gate to evaluate it.

A Honua release candidate is validated as a **train**: the server plus the
downstream client/SDK repos that must all stay green against the *same*
candidate before the release ships.

There are two ways to validate that, depending on the input you have:

| Script | Input | Answers |
| --- | --- | --- |
| `compat-train-release-validation.sh` | the canonical release-train **manifest** (`honua-server/release/honua-<id>.json`) | "Is every release surface the manifest tracks (server, SDK, admin, Helm, Terraform) green or waived, and what owns each gap?" |
| `compat-train-release-gate.sh` | per-repo run **evidence** (from `compat-train-conformance-gate.sh` or env) | "Were the SDK runs actually live, not the seeded local fallback?" |

Run the **validation** script for the headline release-candidate decision from
the published manifest; run the **gate** to evaluate per-repo conformance
evidence (and prove it came from a real external target). Both are default-safe
(per `AGENTS.md`): they only plan/evaluate — emit a verdict, release-notes, and
an evidence bundle; they never deploy, submit, promote, or roll back. The
manifest-driven validator is documented first, then the per-repo gate.

## One-dispatch orchestrator (`compat-train-rc-validation.yml` + `compat-train-rc-aggregate.sh`)

The pieces above each answer one question and historically ran as separate
workflows that an operator had to wire together by hand (dispatch the conformance
gate, download its evidence, feed it to the release gate, separately validate the
manifest, separately probe). The **RC validation orchestrator** chains them in a
single repeatable dispatch and folds their outputs into **one** machine-readable
release-candidate evidence bundle with a single overall verdict and a single
de-duplicated list of owning follow-up issues:

```
conformance-gate ─evidence─▶ release-gate ┐
release-validation (manifest) ────────────┼─▶ rc-aggregate ─▶ rc-validation-bundle.json
live-probe (re-verify) ───────────────────┘        (#41)        (attached to the run)
```

The aggregator (`scripts/compat-train-rc-aggregate.sh`) **reuses** the four
layers' bundles; it re-implements no check. It computes a `releasable` /
`blocked` verdict that requires `conformance`, `release-gate`, and
`release-validation` to be green (the `live-probe` layer is advisory by default —
most of its surfaces are BLOCKED on un-provisioned infra — and becomes required
with `COMPAT_TRAIN_RC_REQUIRE_PROBE=true`). A required layer that was not run is
`missing`, which is **blocking** (a not-run layer is never a silent pass).

The workflow `.github/workflows/compat-train-rc-validation.yml` runs this on
`workflow_dispatch` with `candidate_image` (or `candidate_version`),
`fixtures_version` (a pinned `geospatial-grpc` fixtures version — required), an
optional `manifest_url`, and a `mode` (`advisory` default, or `live` to fail the
dispatch on any blocking layer). It uploads `rc-validation-bundle.json` plus the
per-layer artifacts as `compat-train-rc-validation-<run_id>` and appends the
RC release-notes to the run's step summary — the evidence that can be linked from
the Honua Roadmap Project. On PR/push it runs only the offline self-test
(`bash -n` + `scripts/smoke-compat-train-rc-aggregate.sh`, which exercises the
releasable path and every block path).

| Variable | Default | Purpose |
| --- | --- | --- |
| `COMPAT_TRAIN_CONFORMANCE_EVIDENCE` | _(none)_ | conformance-gate evidence JSON (`repos.<repo>`) |
| `COMPAT_TRAIN_RELEASE_GATE_RESULT` | _(none)_ | `pass`/`fail` — the release-gate exit verdict |
| `COMPAT_TRAIN_VALIDATION_BUNDLE` | _(none)_ | manifest validation bundle |
| `COMPAT_TRAIN_PROBE_BUNDLE` | _(none)_ | live-probe bundle (optional) |
| `COMPAT_TRAIN_RC_REQUIRE_PROBE` | `false` | Require the live-probe layer to be green |
| `COMPAT_TRAIN_RC_MODE` | `live` | `live` fails on any blocking layer; `advisory` exits 0 |
| `COMPAT_TRAIN_RC_BUNDLE_OUTPUT` | `rc-validation-bundle.json` | Aggregated RC evidence bundle |
| `COMPAT_TRAIN_RC_NOTES_OUTPUT` | _(none)_ | Optional RC release-notes output file |

## Manifest-driven validation (`compat-train-release-validation.sh`)

This is the operator-side release-candidate validation **surface** for
`honua-devops#41`. It consumes the release-train manifest published by
`honua-server` and evaluates every release signal it records:

- `releaseGates[]` — server-surface gates (SDK compatibility, real-client
  interop, security nightly, license activation, ...);
- `repositoryLanes[]` — per-repo lanes (SDK CI/staging, mobile SDK train, admin
  docs, Helm chart metadata, ...);
- `releaseLaneCriteria[]` — cross-cutting lane criteria;
- `candidate.image` — the immutable release-candidate image must be published.

Each signal is mapped to one of the **required release surfaces**
(`server sdk admin helm terraform`, override with
`COMPAT_TRAIN_REQUIRED_SURFACES`). A surface needs at least one green/waived
signal to count as covered; an uncovered required surface is a blocking gap with
a generated follow-up. An item passes when its `evidenceState` is `passed`, or
when it carries an approved `waiver` (honor with `COMPAT_TRAIN_ALLOW_WAIVERS`,
default `true`).

Output:

- a per-check `[PASS]`/`[WAIVED]`/`[FAIL]` log grouped by surface, plus exact
  versions (`releaseId`, candidate `ref`, channel) and environment;
- the **owning follow-up issue(s)** for every gap — the manifest's blocker URLs,
  plus a synthetic follow-up for any uncovered required surface;
- a **machine-readable evidence bundle** (the release-train scoreboard) written
  to `COMPAT_TRAIN_BUNDLE_OUTPUT` (default `release-validation-bundle.json`) for
  attaching to the release gate / roadmap Project;
- a release-notes block (optionally to `COMPAT_TRAIN_RELEASE_NOTES_OUTPUT`).

In the default `live` mode any blocked signal or uncovered surface fails the run
(exit 1); `COMPAT_TRAIN_MODE=advisory` reports gaps but exits 0.

```bash
# Validate a candidate from the canonical manifest + published scoreboard.
COMPAT_TRAIN_SCOREBOARD_MATRIX=compatibility/scoreboard/compatibility-matrix.json \
COMPAT_TRAIN_BUNDLE_OUTPUT=compatibility/scoreboard/release-validation-<id>.json \
  ./scripts/compat-train-release-validation.sh \
  ../honua-server/release/honua-<id>.json
```

A committed example bundle (advisory run against the 2026-05 Preview manifest)
lives at `compatibility/scoreboard/release-validation-2026-05-preview.json`; it
records the surfaces still gapped and the issues that own them.

| Variable | Default | Purpose |
| --- | --- | --- |
| `COMPAT_TRAIN_MANIFEST` / arg / `COMPAT_TRAIN_MANIFEST_URL` | _(required)_ | Release-train manifest path or URL |
| `COMPAT_TRAIN_REQUIRED_SURFACES` | `server sdk admin helm terraform` | Surfaces that must each have green/waived evidence |
| `COMPAT_TRAIN_MODE` | `live` | `live` fails on any gap; `advisory` reports but exits 0 |
| `COMPAT_TRAIN_ALLOW_WAIVERS` | `true` | Honor approved `waiver` entries as passes |
| `COMPAT_TRAIN_SCOREBOARD_MATRIX` | _(none)_ | Optional client-compat scoreboard; latest release must have 0 fails |
| `COMPAT_TRAIN_ENVIRONMENT` | _(manifest channel)_ | Environment label for the bundle |
| `COMPAT_TRAIN_BUNDLE_OUTPUT` | `release-validation-bundle.json` | Where to write the evidence bundle |
| `COMPAT_TRAIN_RELEASE_NOTES_OUTPUT` | _(none)_ | Optional release-notes output file |

The paired self-test `scripts/smoke-compat-train-release-validation.sh` exercises
the success path and every blocking path (blocked gate, uncovered surface,
waiver accept/reject, advisory mode, failing scoreboard, bundle shape). It runs
in CI via `.github/workflows/compat-train-release-validation.yml`.

### Attaching the evidence bundle to the release gate

The same workflow has a `workflow_dispatch` lane (`release-validation-dispatch`)
that runs the **real** validator and **attaches the evidence bundle to the run**,
satisfying the `honua-devops#41` criterion "evidence bundle output is attached to
the release gate". Trigger it with a `manifest_url` (the canonical
`honua-server/release/honua-<id>.json`) and a `mode` (`advisory`, the default, or
`live`). With no `manifest_url` it re-derives an advisory bundle from the
committed 2026-05 Preview example
(`compatibility/scoreboard/release-validation-2026-05-preview.json`) so the
dispatch is self-contained. The lane writes `release-validation-bundle.json` and
`release-validation-notes.md`, appends the release-notes block to the run's step
summary, and uploads both as the `compat-train-release-validation-<run_id>`
artifact — the machine-readable evidence that can be linked from the Honua
Roadmap Project. This mirrors the conformance gate's dispatch lane, which
uploads its `compat-train-conformance-<run_id>` evidence the same way.

## Active live-probe re-verification (`compat-train-live-probe.sh`)

The manifest-driven validator above is a *transcriber*: it trusts the
`evidenceState` each signal records in the manifest. honua-devops#41 also asks
the pipeline to **execute smoke checks across the named surfaces** — to
independently re-verify, not just re-read. `compat-train-live-probe.sh` is that
active layer. Given the same release-train manifest it runs a set of **pluggable,
honest probes**, each of which runs only what the environment can actually reach:

| Probe | Verifies | When BLOCKED (documented missing dependency) |
| --- | --- | --- |
| `github-run` | re-fetches the conclusion of every GitHub Actions run URL the manifest cites and confirms it is still `success` | no `gh` auth and no `GITHUB_TOKEN` -> `needs gh auth login OR GITHUB_TOKEN` |
| `candidate-image` | the candidate image tag+digest are published | manifest `candidate.image.tag/digest` still null -> `needs a published RC image tag+digest` |
| `server-health` | HTTP smoke (`/healthz/ready`, `/healthz/live`) against a live candidate | no `HONUA_PROBE_BASE_URL` -> `needs a real external staging target` (the honua-sdk-python#53 / #41 staging-URL gap) |
| `helm-metadata` | the chart `appVersion` points at the candidate, not a placeholder | no `HONUA_PROBE_HELM_CHART` / chart unversioned -> `needs a versioned chart (honua-helm#1)` |
| `terraform-plan` | a candidate IaC plan applies against the seeded demo | no live target/creds -> `needs honua-iac#30 + cloud creds` |

**Correctness rule (the #41 contract):** a probe that cannot truly run reports
state `blocked` with a `missing` dependency. It **never emits a green it did not
verify**. A probe state is one of `passed` (actively re-verified green), `failed`
(actively re-verified and *not* green — a real regression the manifest may have
under-reported), or `blocked` (a documented gap). A surface rolls up to
`verified` only with >=1 `passed` and zero `failed` probes; a surface with only
blocked probes is `blocked`, never silently green.

Default-safe and read-only (per `AGENTS.md`): the probe performs only GET
requests and read-only status lookups; it never deploys, promotes, submits, or
rolls back. In the default `advisory` mode it always exits 0 (so it can be folded
into the evaluator's bundle and run on a still-gapped preview). In
`HONUA_PROBE_MODE=live` it exits non-zero only when a probe that *actually ran*
came back `failed` — blocked-on-missing-dependency probes are gaps, not
regressions, and never fail the run.

```bash
# Active re-verification against the canonical manifest (re-checks every cited
# GitHub run; server/helm/terraform stay BLOCKED until their infra is wired).
GITHUB_TOKEN=... \
HONUA_PROBE_BUNDLE_OUTPUT=live-probe-bundle.json \
  ./scripts/compat-train-live-probe.sh \
  ../honua-server/release/honua-2026-05-preview.json

# Activate the creds-gated probes once the infra exists:
HONUA_PROBE_BASE_URL=https://staging.honua.example \
HONUA_PROBE_HELM_CHART=path/to/Chart.yaml \
  ./scripts/compat-train-live-probe.sh ../honua-server/release/honua-2026-05-preview.json
```

| Variable | Default | Purpose |
| --- | --- | --- |
| `HONUA_PROBE_MANIFEST` / arg / `HONUA_PROBE_MANIFEST_URL` | _(required)_ | Release-train manifest path or URL |
| `HONUA_PROBE_MODE` | `advisory` | `live` exits non-zero on a real failed probe; `advisory` always exits 0 |
| `HONUA_PROBE_BASE_URL` | _(none)_ | Live candidate base URL for the `server-health` probe |
| `HONUA_PROBE_HELM_CHART` | _(none)_ | Path/dir of a `Chart.yaml` for the `helm-metadata` probe |
| `HONUA_PROBE_BUNDLE_OUTPUT` | `live-probe-bundle.json` | Where to write the probe bundle |
| `HONUA_PROBE_MAX_RUNS` | `40` | Cap on cited GitHub runs to re-verify |
| `GITHUB_TOKEN` | _(none)_ | Enables the `github-run` probe when `gh` is not authed |

Against the live 2026-05 Preview manifest this probe re-verifies all seven cited
GitHub runs (five still green; `server-security-nightly` and
`sdk-python-staging-integration` confirmed *failing*, matching their manifest
blockers) and reports the image, server-health, Helm, and Terraform surfaces as
BLOCKED on their named dependencies. The `release-validation-dispatch` workflow
lane runs this probe with the runner's `GITHUB_TOKEN` and folds the
`live-probe-bundle.json` into the uploaded evidence artifact. Its self-test
`scripts/smoke-compat-train-live-probe.sh` proves the BLOCKED-not-faked contract
fully offline and runs in CI.

### Probe-bundle schema (`compat-train-live-probe`)

```jsonc
{
  "schemaVersion": 1,
  "kind": "compat-train-live-probe",
  "generatedFrom": "<manifest path/url>",
  "releaseId": "...", "channel": "...", "candidateRef": "...", "mode": "advisory",
  "summary": { "probes": N, "passed": N, "failed": N, "blocked": N },
  "surfaceProbeCoverage": [
    { "surface": "server", "passed": N, "failed": N, "blocked": N,
      "status": "verified | failed | blocked" }
  ],
  "probes": [
    { "id": "github-run:<signal>", "surface": "server", "state": "passed | failed | blocked",
      "detail": "...", "missing": null /* or the missing dependency string */ }
  ],
  "references": { "manifest": "...", "ownedBy": ".../issues/41",
                  "evaluator": "scripts/compat-train-release-validation.sh" }
}
```

## Per-repo live-evidence gate (`compat-train-release-gate.sh`)

This gate evaluates the per-repo release-candidate evidence plus the published
client-compatibility scoreboard and decides whether the train is releasable.

Default-safe posture (per `AGENTS.md`): the gate only plans/evaluates. It emits
a release-notes block and a machine-readable verdict (exit code); it never
deploys, submits, promotes, or rolls back.

## Why this gate exists

`honua-sdk-python#53` reported that the SDK Python release run was green but had
used the **seeded local fallback** (`HONUA_BASE_URL=http://localhost:5000`,
`local_stack=true`) rather than a live external candidate target. A green run on
local-fallback evidence is **not** acceptable release-candidate evidence: it
does not prove the candidate works against a real deployment.

In its default `live` mode this gate refuses to certify any repo whose evidence
came from the seeded local fallback (or whose `local_stack` signal is unknown),
so the train cannot be declared validated on local-fallback evidence alone.

Note: provisioning the real external staging URL/API key for the SDK repos
(setting `HONUA_BASE_URL` and target metadata in each repo's GitHub Actions
`staging` environment) is a separate deployment task. This gate is the
consumer that enforces the resulting evidence is live.

## Train repos

Default train (`COMPAT_TRAIN_REPOS`):

```
honua-server honua-sdk-python honua-sdk-js honua-mobile honua-qgis-plugin
```

Override for a partial train (e.g. a single-SDK hotfix) by setting
`COMPAT_TRAIN_REPOS` or supplying a `repos` map in the evidence file.

## Inputs

Per-repo evidence may come from an evidence JSON file/URL or from env vars
(env takes precedence, matching `scripts/slo-release-gate.sh` and
`scripts/console-release-gate.sh`).

Evidence JSON shape:

```json
{
  "candidate": { "version": "2026.06.0-rc.1" },
  "environment": "staging",
  "repos": {
    "honua-server": {
      "status": "pass",
      "local_stack": false,
      "base_url": "https://staging.honua.example/api",
      "commit": "abc123"
    },
    "honua-sdk-python": {
      "status": "pass",
      "local_stack": false,
      "base_url": "https://staging.honua.example/api",
      "commit": "7c39a0d"
    }
  },
  "scoreboard_matrix": "compatibility/scoreboard/compatibility-matrix.json"
}
```

Per-repo env overrides use `COMPAT_TRAIN_REPO_<REPO_KEY>_<FIELD>` where
`<REPO_KEY>` is the repo name upper-cased with non-alphanumerics replaced by `_`
(e.g. `COMPAT_TRAIN_REPO_HONUA_SDK_PYTHON_STATUS`,
`COMPAT_TRAIN_REPO_HONUA_SDK_PYTHON_LOCAL_STACK`).

## Decision rules

A repo passes when its run `status` is green **and**, in `live` mode, its
`local_stack` is explicitly `false` (a known live target). The train passes when
every required repo passes and, if a scoreboard matrix is supplied, the latest
release in that matrix has no failing client/protocol statuses.

Blocking conditions:

- any required repo with missing or non-green run evidence;
- in `live` mode, any green repo whose run used the seeded local fallback
  (`local_stack=true`) or whose `local_stack` is unknown;
- a scoreboard latest-release `fail` count `> 0`;
- a scoreboard latest-release `pending` count `> 0` when
  `COMPAT_TRAIN_STRICT_SCOREBOARD=true`.

## Configuration

| Variable | Default | Purpose |
| --- | --- | --- |
| `COMPAT_TRAIN_REPOS` | server + SDK set | Repos that make up the train |
| `COMPAT_TRAIN_MODE` | `live` | `live` rejects local-fallback evidence; `any` accepts it (CI/dev) |
| `COMPAT_TRAIN_STRICT_SCOREBOARD` | `false` | Treat scoreboard `pending` clients as blocking |
| `COMPAT_TRAIN_SCOREBOARD_MATRIX` | _(none)_ | Path to `compatibility-matrix.json` |
| `COMPAT_TRAIN_CANDIDATE_VERSION` | _(from evidence)_ | RC version label for notes |
| `COMPAT_TRAIN_ENVIRONMENT` | `staging` | Target environment label |
| `COMPAT_TRAIN_GATE_MODE` | `certify` | Action label for notes |
| `COMPAT_TRAIN_RELEASE_NOTES_OUTPUT` | _(none)_ | Write the release-notes block to a file |

## Usage

```bash
# Certify a candidate from an evidence file plus the published scoreboard.
COMPAT_TRAIN_SCOREBOARD_MATRIX=compatibility/scoreboard/compatibility-matrix.json \
  ./scripts/compat-train-release-gate.sh path/to/rc-evidence.json
```

The paired self-test `scripts/smoke-compat-train-release-gate.sh` exercises the
success path and every blocking path (local-fallback rejection, unknown
`local_stack`, failing/missing repo runs, env override precedence, strict and
non-strict scoreboard handling). It runs in CI via
`.github/workflows/compat-train-release-validation.yml`.
