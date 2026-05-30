#!/usr/bin/env bash

# Self-test for scripts/console-release-gate.sh and scripts/console-preview-env.sh.
# Exercises pass/fail paths, surface-parity strict mode, browser-smoke blocking,
# release-notes rendering, and the preview-environment planner (gated + skip-gate).

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
GATE="$REPO_ROOT/scripts/console-release-gate.sh"
PREVIEW="$REPO_ROOT/scripts/console-preview-env.sh"

require_command() {
  if ! command -v "$1" >/dev/null 2>&1; then
    echo "[ERROR] required command not found: $1" >&2
    exit 1
  fi
}

require_command jq

WORKDIR="$(mktemp -d)"
cleanup() { rm -rf "$WORKDIR"; }
trap cleanup EXIT

pass_evidence="$WORKDIR/pass.json"
cat >"$pass_evidence" <<'EOF'
{
  "artifact": { "kind": "unified-runtime-image", "version": "2026.05.0-rc1" },
  "environment": "staging",
  "stages": {
    "install": "pass",
    "lint": "pass",
    "typecheck": "pass",
    "unit": "pass",
    "browser_smoke": "pass",
    "build": "pass"
  },
  "surfaces": {
    "console": "ready",
    "studio": "ready",
    "catalog": "ready",
    "share": "preview",
    "operate": "ready"
  },
  "legacy": { "portal_required": false, "admin_required": false }
}
EOF

fail_smoke_evidence="$WORKDIR/fail-smoke.json"
cat >"$fail_smoke_evidence" <<'EOF'
{
  "artifact": { "kind": "unified-runtime-image", "version": "2026.05.0-rc2" },
  "environment": "staging",
  "stages": {
    "install": "pass",
    "lint": "pass",
    "typecheck": "pass",
    "unit": "pass",
    "browser_smoke": "fail",
    "build": "pass"
  },
  "surfaces": {
    "console": "ready",
    "studio": "ready",
    "catalog": "ready",
    "share": "ready",
    "operate": "ready"
  },
  "legacy": { "portal_required": true, "admin_required": "unknown" }
}
EOF

regressed_surface_evidence="$WORKDIR/regressed-surface.json"
cat >"$regressed_surface_evidence" <<'EOF'
{
  "artifact": { "kind": "unified-runtime-image", "version": "2026.05.0-rc3" },
  "environment": "staging",
  "stages": {
    "install": "pass",
    "lint": "pass",
    "typecheck": "pass",
    "unit": "pass",
    "browser_smoke": "pass",
    "build": "pass"
  },
  "surfaces": {
    "console": "ready",
    "studio": "regressed",
    "catalog": "ready",
    "share": "ready",
    "operate": "ready"
  },
  "legacy": { "portal_required": false, "admin_required": false }
}
EOF

echo "== 1. gate passes on fully-green evidence =="
notes_out="$WORKDIR/notes.md"
CONSOLE_EVIDENCE_FILE="$pass_evidence" CONSOLE_RELEASE_NOTES_OUTPUT="$notes_out" "$GATE"
grep -q "Console release gate PASSED" "$WORKDIR/notes.md" 2>/dev/null || true
if ! grep -q "## Legacy deployment paths" "$notes_out"; then
  echo "[ERROR] release notes missing legacy deployment section" >&2
  exit 1
fi
if ! grep -q "retired (safe to remove)" "$notes_out"; then
  echo "[ERROR] release notes did not render retired legacy Portal/Admin status" >&2
  exit 1
fi

echo "== 2. failing browser smoke blocks promotion =="
if CONSOLE_EVIDENCE_FILE="$fail_smoke_evidence" "$GATE" >"$WORKDIR/fail-smoke.log" 2>&1; then
  echo "[ERROR] gate unexpectedly passed with failing browser smoke" >&2
  cat "$WORKDIR/fail-smoke.log" >&2
  exit 1
fi
grep -q "browser smoke did not pass" "$WORKDIR/fail-smoke.log"
grep -q "promotion of the single Console artifact is blocked" "$WORKDIR/fail-smoke.log"

echo "== 3. regressed surface parity blocks promotion =="
if CONSOLE_EVIDENCE_FILE="$regressed_surface_evidence" "$GATE" >"$WORKDIR/regressed.log" 2>&1; then
  echo "[ERROR] gate unexpectedly passed with a regressed surface" >&2
  exit 1
fi
grep -q "surface_parity studio=regressed" "$WORKDIR/regressed.log"

echo "== 4. strict surface parity blocks a preview-only surface =="
if CONSOLE_EVIDENCE_FILE="$pass_evidence" CONSOLE_STRICT_SURFACE_PARITY=true "$GATE" >"$WORKDIR/strict.log" 2>&1; then
  echo "[ERROR] strict mode unexpectedly passed with a preview surface" >&2
  exit 1
fi
grep -q "surface_parity share=preview (blocked by strict mode)" "$WORKDIR/strict.log"

echo "== 5. missing evidence (no env, no file) fails closed =="
if CONSOLE_REQUIRED_STAGES="install build" "$GATE" >"$WORKDIR/missing.log" 2>&1; then
  echo "[ERROR] gate unexpectedly passed with no evidence" >&2
  exit 1
fi
grep -q "missing (no evidence supplied)" "$WORKDIR/missing.log"

echo "== 6. env vars override evidence stage status =="
if CONSOLE_EVIDENCE_FILE="$pass_evidence" CONSOLE_STAGE_BUILD=fail "$GATE" >"$WORKDIR/override.log" 2>&1; then
  echo "[ERROR] env override did not flip build stage to fail" >&2
  exit 1
fi
grep -q "ci_stage build=fail" "$WORKDIR/override.log"

echo "== 7. unknown legacy path emits a warning but does not block =="
CONSOLE_EVIDENCE_FILE="$pass_evidence" CONSOLE_LEGACY_PORTAL_REQUIRED=unknown "$GATE" >"$WORKDIR/legacy.log" 2>&1
grep -q "legacy Portal deployment requirement is unknown" "$WORKDIR/legacy.log"

echo "== 8. preview planner emits a gated, plan-only descriptor =="
preview_out="$WORKDIR/preview.json"
CONSOLE_EVIDENCE_FILE="$pass_evidence" "$PREVIEW" \
  --ref "feature/Console_Nav-Refresh" --kind branch --output "$preview_out" >"$WORKDIR/preview.log" 2>&1
jq -e '.kind == "ConsolePreviewEnvironment"' "$preview_out" >/dev/null
jq -e '.spec.mode == "plan" and .spec.submitImmediately == false' "$preview_out" >/dev/null
ns="$(jq -r '.spec.namespace' "$preview_out")"
host="$(jq -r '.spec.hostname' "$preview_out")"
[[ "$ns" == "console-preview-feature-console-nav-refresh" ]] || {
  echo "[ERROR] unexpected preview namespace: $ns" >&2
  exit 1
}
case "$host" in
  feature-console-nav-refresh.preview.honua.dev) ;;
  *) echo "[ERROR] unexpected preview hostname: $host" >&2; exit 1 ;;
esac

echo "== 9. preview planner refuses when gate fails =="
if CONSOLE_EVIDENCE_FILE="$fail_smoke_evidence" "$PREVIEW" --ref rc-2026.05 --kind release-candidate >"$WORKDIR/preview-fail.log" 2>&1; then
  echo "[ERROR] preview planner emitted a descriptor despite a failing gate" >&2
  exit 1
fi

echo "== 10. preview planner with --skip-gate works for shape debugging =="
CONSOLE_PREVIEW_OUTPUT="$WORKDIR/preview-skip.json" "$PREVIEW" --ref RC-2026.05 --kind release-candidate --skip-gate >/dev/null 2>&1
jq -e '.spec.source.kind == "release-candidate"' "$WORKDIR/preview-skip.json" >/dev/null

echo "Console release gate + preview planner smoke check passed."
