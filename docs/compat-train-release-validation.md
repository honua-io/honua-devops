# Compatibility-Train Release Validation

Release-candidate validation for the cross-repo compatibility train
(`honua-devops#41`).

A Honua release candidate is validated as a **train**: the server plus the
downstream client/SDK repos that must all stay green against the *same*
candidate before the release ships. This gate evaluates the per-repo
release-candidate evidence plus the published client-compatibility scoreboard
and decides whether the train is releasable.

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
