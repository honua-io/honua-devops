#!/usr/bin/env bash

# Self-test for scripts/compat-train-rc-aggregate.sh.
#
# Exercises the success path and every blocking path the RC aggregator must
# enforce, entirely offline (it consumes synthetic layer bundles):
#   - all required layers green -> "releasable", exit 0;
#   - a failing conformance consumer -> blocked + follow-up names the consumer;
#   - a failed release-gate (local-fallback evidence) -> blocked;
#   - a failed manifest validation -> blocked + its follow-ups are folded in;
#   - a missing required layer -> blocked (a not-run layer is never a silent pass);
#   - live-probe blocked-only is NOT releasable-blocking by default, but IS when
#     COMPAT_TRAIN_RC_REQUIRE_PROBE=true;
#   - advisory mode reports the blocked verdict but exits 0;
#   - the emitted bundle has the expected machine-readable shape.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
AGG="$REPO_ROOT/scripts/compat-train-rc-aggregate.sh"

require_command() {
  if ! command -v "$1" >/dev/null 2>&1; then
    echo "[ERROR] required command not found: $1" >&2
    exit 1
  fi
}

require_command jq

WORKDIR="$(mktemp -d)"
trap 'rm -rf "$WORKDIR"' EXIT

BUNDLE="$WORKDIR/rc-bundle.json"

# --- Synthetic layer bundles -------------------------------------------------

# conformance evidence (producer output shape: repos.<repo>.{status,...})
cat >"$WORKDIR/conformance-green.json" <<'EOF'
{
  "candidate": { "version": "2026.06.0-rc.1", "image": "ghcr.io/honua-io/honua-server:2026.06.0-rc.1" },
  "environment": "staging",
  "fixtures_version": "0.1.0-alpha.1",
  "repos": {
    "honua-sdk-python": { "status": "pass", "local_stack": false, "base_url": "https://staging.honua.example", "commit": "7c39a0d" },
    "honua-sdk-js":     { "status": "pass", "local_stack": false, "base_url": "https://staging.honua.example", "commit": "abc123" }
  }
}
EOF

cat >"$WORKDIR/conformance-broken.json" <<'EOF'
{
  "candidate": { "version": "2026.06.0-rc.1" },
  "environment": "staging",
  "repos": {
    "honua-sdk-python": { "status": "pass", "local_stack": false },
    "honua-mobile":     { "status": "fail", "local_stack": false, "detail": "FeatureServer read break" }
  }
}
EOF

# manifest validation bundle (validator output shape: verdict + followUps + summary)
cat >"$WORKDIR/validation-pass.json" <<'EOF'
{
  "kind": "compat-train-release-validation",
  "releaseId": "honua-2026-05-preview", "channel": "preview", "environment": "preview",
  "candidate": { "ref": "deadbeef" },
  "verdict": "pass",
  "summary": { "surfacesMissing": [] },
  "followUps": []
}
EOF

cat >"$WORKDIR/validation-fail.json" <<'EOF'
{
  "kind": "compat-train-release-validation",
  "releaseId": "honua-2026-05-preview", "channel": "preview", "environment": "preview",
  "candidate": { "ref": "deadbeef" },
  "verdict": "fail",
  "summary": { "surfacesMissing": ["terraform"] },
  "followUps": [
    { "url": "https://github.com/honua-io/honua-server/issues/939", "repo": "honua-io/honua-server", "number": 939, "reason": "Security nightly failing." }
  ]
}
EOF

# live-probe bundle (probe output shape: summary.{passed,failed,blocked})
cat >"$WORKDIR/probe-blocked-only.json" <<'EOF'
{ "kind": "compat-train-live-probe", "releaseId": "honua-2026-05-preview", "candidateRef": "deadbeef",
  "summary": { "probes": 5, "passed": 0, "failed": 0, "blocked": 5 } }
EOF

cat >"$WORKDIR/probe-green.json" <<'EOF'
{ "kind": "compat-train-live-probe", "releaseId": "honua-2026-05-preview", "candidateRef": "deadbeef",
  "summary": { "probes": 5, "passed": 3, "failed": 0, "blocked": 2 } }
EOF

# --- Tests -------------------------------------------------------------------

echo "Validating success path: conformance green + gate pass + validation pass -> releasable"
COMPAT_TRAIN_CONFORMANCE_EVIDENCE="$WORKDIR/conformance-green.json" \
COMPAT_TRAIN_RELEASE_GATE_RESULT="pass" \
COMPAT_TRAIN_VALIDATION_BUNDLE="$WORKDIR/validation-pass.json" \
COMPAT_TRAIN_PROBE_BUNDLE="$WORKDIR/probe-green.json" \
COMPAT_TRAIN_RC_BUNDLE_OUTPUT="$BUNDLE" \
  "$AGG" >/dev/null
[[ "$(jq -r '.verdict' "$BUNDLE")" == "releasable" ]] || { echo "[ERROR] expected releasable verdict" >&2; exit 1; }
echo "[OK] all-green train is releasable"

echo
echo "Validating bundle shape: required machine-readable fields are present"
jq -e '.verdict and (.layers|type=="array") and (.summary|type=="object") and (.followUps|type=="array") and (.requiredLayers|type=="array")' "$BUNDLE" >/dev/null \
  || { echo "[ERROR] RC bundle missing required fields" >&2; exit 1; }
echo "[OK] RC evidence bundle shape is valid"

echo
echo "Validating failure path: a failing conformance consumer blocks and is named in a follow-up"
if COMPAT_TRAIN_CONFORMANCE_EVIDENCE="$WORKDIR/conformance-broken.json" \
   COMPAT_TRAIN_RELEASE_GATE_RESULT="pass" \
   COMPAT_TRAIN_VALIDATION_BUNDLE="$WORKDIR/validation-pass.json" \
   COMPAT_TRAIN_RC_BUNDLE_OUTPUT="$BUNDLE" \
   "$AGG" >/dev/null; then
  echo "[ERROR] aggregator unexpectedly passed with a broken conformance consumer" >&2; exit 1
fi
[[ "$(jq -r '.verdict' "$BUNDLE")" == "blocked" ]] || { echo "[ERROR] expected blocked verdict" >&2; exit 1; }
jq -e '.followUps[] | select(.reason | test("honua-mobile"))' "$BUNDLE" >/dev/null \
  || { echo "[ERROR] broken consumer not surfaced as a follow-up" >&2; exit 1; }
echo "[OK] broken conformance consumer correctly blocked and surfaced"

echo
echo "Validating failure path: a failed release-gate (local-fallback evidence) blocks the train"
if COMPAT_TRAIN_CONFORMANCE_EVIDENCE="$WORKDIR/conformance-green.json" \
   COMPAT_TRAIN_RELEASE_GATE_RESULT="fail" \
   COMPAT_TRAIN_VALIDATION_BUNDLE="$WORKDIR/validation-pass.json" \
   COMPAT_TRAIN_RC_BUNDLE_OUTPUT="$BUNDLE" \
   "$AGG" >/dev/null; then
  echo "[ERROR] aggregator unexpectedly passed with a failed release-gate" >&2; exit 1
fi
jq -e '.summary.blockingLayers | index("release-gate")' "$BUNDLE" >/dev/null \
  || { echo "[ERROR] release-gate not reported as a blocking layer" >&2; exit 1; }
echo "[OK] failed release-gate correctly blocked"

echo
echo "Validating failure path: a failed manifest validation blocks and folds in its follow-ups"
if COMPAT_TRAIN_CONFORMANCE_EVIDENCE="$WORKDIR/conformance-green.json" \
   COMPAT_TRAIN_RELEASE_GATE_RESULT="pass" \
   COMPAT_TRAIN_VALIDATION_BUNDLE="$WORKDIR/validation-fail.json" \
   COMPAT_TRAIN_RC_BUNDLE_OUTPUT="$BUNDLE" \
   "$AGG" >/dev/null; then
  echo "[ERROR] aggregator unexpectedly passed with a failed manifest validation" >&2; exit 1
fi
jq -e '.followUps[] | select(.url == "https://github.com/honua-io/honua-server/issues/939")' "$BUNDLE" >/dev/null \
  || { echo "[ERROR] validation follow-up not folded into the RC bundle" >&2; exit 1; }
echo "[OK] failed manifest validation correctly blocked and its follow-up folded in"

echo
echo "Validating failure path: a MISSING required layer blocks (a not-run layer is never a silent pass)"
if COMPAT_TRAIN_CONFORMANCE_EVIDENCE="$WORKDIR/conformance-green.json" \
   COMPAT_TRAIN_RELEASE_GATE_RESULT="pass" \
   COMPAT_TRAIN_RC_BUNDLE_OUTPUT="$BUNDLE" \
   "$AGG" >/dev/null; then
  echo "[ERROR] aggregator unexpectedly passed with a missing validation layer" >&2; exit 1
fi
jq -e '.layers[] | select(.name=="release-validation") | .status=="missing"' "$BUNDLE" >/dev/null \
  || { echo "[ERROR] absent validation bundle not reported as missing" >&2; exit 1; }
echo "[OK] missing required layer correctly blocked"

echo
echo "Validating live-probe is advisory by default: blocked-only probe does NOT block the train"
COMPAT_TRAIN_CONFORMANCE_EVIDENCE="$WORKDIR/conformance-green.json" \
COMPAT_TRAIN_RELEASE_GATE_RESULT="pass" \
COMPAT_TRAIN_VALIDATION_BUNDLE="$WORKDIR/validation-pass.json" \
COMPAT_TRAIN_PROBE_BUNDLE="$WORKDIR/probe-blocked-only.json" \
COMPAT_TRAIN_RC_BUNDLE_OUTPUT="$BUNDLE" \
  "$AGG" >/dev/null
[[ "$(jq -r '.verdict' "$BUNDLE")" == "releasable" ]] || { echo "[ERROR] blocked-only probe should not block by default" >&2; exit 1; }
[[ "$(jq -r '.layers[] | select(.name=="live-probe") | .status' "$BUNDLE")" == "blocked" ]] \
  || { echo "[ERROR] blocked-only probe layer should report status=blocked" >&2; exit 1; }
echo "[OK] live-probe advisory-by-default honored"

echo
echo "Validating COMPAT_TRAIN_RC_REQUIRE_PROBE=true makes a blocked-only probe block the train"
if COMPAT_TRAIN_CONFORMANCE_EVIDENCE="$WORKDIR/conformance-green.json" \
   COMPAT_TRAIN_RELEASE_GATE_RESULT="pass" \
   COMPAT_TRAIN_VALIDATION_BUNDLE="$WORKDIR/validation-pass.json" \
   COMPAT_TRAIN_PROBE_BUNDLE="$WORKDIR/probe-blocked-only.json" \
   COMPAT_TRAIN_RC_REQUIRE_PROBE=true \
   COMPAT_TRAIN_RC_BUNDLE_OUTPUT="$BUNDLE" \
   "$AGG" >/dev/null; then
  echo "[ERROR] required blocked-only probe should have blocked the train" >&2; exit 1
fi
echo "[OK] required-probe mode blocks a blocked-only probe"

echo
echo "Validating advisory mode reports the blocked verdict but exits 0"
COMPAT_TRAIN_CONFORMANCE_EVIDENCE="$WORKDIR/conformance-green.json" \
COMPAT_TRAIN_RELEASE_GATE_RESULT="fail" \
COMPAT_TRAIN_VALIDATION_BUNDLE="$WORKDIR/validation-pass.json" \
COMPAT_TRAIN_RC_MODE=advisory \
COMPAT_TRAIN_RC_BUNDLE_OUTPUT="$BUNDLE" \
  "$AGG" >/dev/null
[[ "$(jq -r '.verdict' "$BUNDLE")" == "blocked" ]] || { echo "[ERROR] advisory bundle should still record a blocked verdict" >&2; exit 1; }
echo "[OK] advisory mode exits 0 while recording the blocked verdict"

echo
echo "Validating release-notes output is written"
COMPAT_TRAIN_CONFORMANCE_EVIDENCE="$WORKDIR/conformance-green.json" \
COMPAT_TRAIN_RELEASE_GATE_RESULT="pass" \
COMPAT_TRAIN_VALIDATION_BUNDLE="$WORKDIR/validation-pass.json" \
COMPAT_TRAIN_RC_BUNDLE_OUTPUT="$BUNDLE" \
COMPAT_TRAIN_RC_NOTES_OUTPUT="$WORKDIR/notes.md" \
  "$AGG" >/dev/null
grep -q "compatibility-train release-candidate validation" "$WORKDIR/notes.md" \
  || { echo "[ERROR] release notes were not written as expected" >&2; exit 1; }
echo "[OK] release notes written"

echo
echo "Compatibility-train RC aggregator smoke check passed."
