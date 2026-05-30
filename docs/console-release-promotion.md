# Honua Console Release Promotion & Preview Pipeline

Implements `honua-devops#56`. Gives `honua-console` the same release discipline as
the rest of Honua while supporting the single-artifact (unified-runtime) deployment
path described in `honua-console/docs/adr/0001-unified-honua-console-runtime.md`.

Like the rest of `honua-devops`, this lane is **plan-only / evidence-first**: the
gate evaluates CI evidence and emits release notes and a verdict; the preview
planner derives a deterministic descriptor. Neither applies manifests, submits, or
rolls back deploy operations.

## CI expectations for `honua-console`

Every `honua-console` PR is expected to produce deterministic status across these
required stages (the gate blocks promotion on any non-`pass`):

- `install` — dependency install
- `lint`
- `typecheck`
- `unit` — unit tests
- `browser_smoke` — browser smoke; failing this **blocks promotion** of the single
  deployable artifact (explicit acceptance criterion)
- `build` — production build

The required set is overridable via `CONSOLE_REQUIRED_STAGES`.

## Unified-runtime surface parity

The single artifact bundles five surfaces. Each must report an acceptable parity
state before the artifact promotes:

- `console`, `studio`, `catalog`, `share`, `operate`

Accepted states: `ready` (at parity) and `preview` (behind a preview flag). Any other
value (e.g. `regressed`, `missing`) blocks promotion. Set
`CONSOLE_STRICT_SURFACE_PARITY=true` to also block on `preview`-only surfaces when a
fully-at-parity promotion is required.

## Release notes & legacy deployment paths

The gate renders release notes that identify whether the old Portal/Admin deployment
paths are still required, sourced from `legacy.portal_required` / `legacy.admin_required`
in the evidence (or `CONSOLE_LEGACY_PORTAL_REQUIRED` / `CONSOLE_LEGACY_ADMIN_REQUIRED`).
An unknown value is rendered as `UNKNOWN (confirm before promotion)` and emits a
non-blocking warning so reviewers resolve it before retiring the legacy paths.

Write the notes to a file with `CONSOLE_RELEASE_NOTES_OUTPUT=<path>`.

## Evidence format

CI emits a JSON evidence bundle; see `desired-state/console/sample-ci-evidence.json`:

```json
{
  "artifact": { "kind": "unified-runtime-image", "version": "2026.05.0-rc1" },
  "environment": "staging",
  "stages": {
    "install": "pass", "lint": "pass", "typecheck": "pass",
    "unit": "pass", "browser_smoke": "pass", "build": "pass"
  },
  "surfaces": {
    "console": "ready", "studio": "ready", "catalog": "ready",
    "share": "preview", "operate": "ready"
  },
  "legacy": { "portal_required": false, "admin_required": false }
}
```

Individual fields can be overridden by env vars (env wins over JSON), e.g.
`CONSOLE_STAGE_BROWSER_SMOKE=fail`, `CONSOLE_SURFACE_STUDIO=regressed`.

## Running the gate

```bash
# From an evidence file (main-branch merge / release candidate):
./scripts/console-release-gate.sh desired-state/console/sample-ci-evidence.json

# Or from a published evidence URL:
CONSOLE_EVIDENCE_URL=https://ci.example/console-evidence.json ./scripts/console-release-gate.sh
```

Exit `0` means the single Console artifact is eligible for promotion; exit `1` means
promotion is blocked.

## Preview environments

`scripts/console-preview-env.sh` plans an ephemeral preview/staging deployment of the
unified runtime from a branch or release candidate. It runs the release gate first
(so a failing smoke cannot spin up a promotable preview), then emits a deterministic,
plan-only `ConsolePreviewEnvironment` descriptor (namespace, hostname, artifact ref,
TTL) for downstream GitOps.

```bash
./scripts/console-preview-env.sh \
  --ref feature/console-nav-refresh --kind branch \
  --evidence desired-state/console/sample-ci-evidence.json \
  --output preview.json
```

`--kind` is `branch` or `release-candidate`. Use `--skip-gate` only for descriptor
shape debugging.

## CI wiring

`.github/workflows/console-release-promotion.yml` runs on PRs and pushes to `trunk`:
`bash -n` syntax checks, the `smoke-console-release-gate.sh` self-test, and the gate
against the sample evidence. The self-test covers pass/fail paths, browser-smoke
blocking, surface regression, strict parity, env overrides, release notes, and the
preview planner (gated + skip-gate).
