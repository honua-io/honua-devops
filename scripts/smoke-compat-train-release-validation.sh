#!/usr/bin/env bash

# Self-test for scripts/compat-train-release-validation.sh.
#
# Exercises the success path and every blocking path the manifest-driven
# release-candidate validator must enforce:
#   - a fully-covered, all-green manifest passes and emits a "pass" bundle;
#   - a blocked release gate fails and surfaces the owning follow-up issue;
#   - an uncovered required surface (no Terraform lane) fails as a gap;
#   - an approved waiver flips a blocked item to pass, unless waivers are disabled;
#   - advisory mode reports gaps but exits 0;
#   - a failing client scoreboard blocks the train;
#   - the emitted evidence bundle has the expected machine-readable shape.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
VALIDATE="$REPO_ROOT/scripts/compat-train-release-validation.sh"

require_command() {
  if ! command -v "$1" >/dev/null 2>&1; then
    echo "[ERROR] required command not found: $1" >&2
    exit 1
  fi
}

require_command jq

WORKDIR="$(mktemp -d)"
trap 'rm -rf "$WORKDIR"' EXIT

BUNDLE="$WORKDIR/bundle.json"

# A manifest that covers all five required surfaces with green evidence.
cat >"$WORKDIR/manifest-pass.json" <<'EOF'
{
  "releaseId": "honua-test-rc",
  "channel": "preview",
  "observedAt": "2026-06-01T00:00:00Z",
  "candidate": {
    "ref": "deadbeef",
    "image": { "evidenceState": "passed", "repository": "ghcr.io/honua-io/honua-server" }
  },
  "releaseGates": [
    { "id": "server-sdk-compatibility", "owningRepo": "honua-io/honua-server",
      "requirement": "SDK compat green", "evidenceState": "passed", "blockers": [] }
  ],
  "repositoryLanes": [
    { "id": "sdk-js-trunk-ci", "owningRepo": "honua-io/honua-sdk-js",
      "requirement": "SDK CI green", "evidenceState": "passed", "blockers": [] },
    { "id": "admin-docs", "owningRepo": "honua-io/honua-console",
      "requirement": "Admin docs", "evidenceState": "passed", "blockers": [] },
    { "id": "helm-rc-metadata", "owningRepo": "honua-io/honua-helm",
      "requirement": "Chart appVersion pinned", "evidenceState": "passed", "blockers": [] },
    { "id": "terraform-modules", "owningRepo": "honua-io/honua-iac",
      "requirement": "Terraform plan validates", "evidenceState": "passed", "blockers": [] }
  ],
  "releaseLaneCriteria": []
}
EOF

cat >"$WORKDIR/scoreboard-clean.json" <<'EOF'
{ "releases": [ { "release": "2026.06.0", "summary": { "pass": 8, "pending": 0, "fail": 0 } } ] }
EOF

echo "Validating success path: all five surfaces green, clean scoreboard -> pass"
COMPAT_TRAIN_BUNDLE_OUTPUT="$BUNDLE" \
COMPAT_TRAIN_SCOREBOARD_MATRIX="$WORKDIR/scoreboard-clean.json" \
  "$VALIDATE" "$WORKDIR/manifest-pass.json" >/dev/null
[[ "$(jq -r '.verdict' "$BUNDLE")" == "pass" ]] || { echo "[ERROR] expected pass verdict" >&2; exit 1; }
[[ "$(jq -r '.summary.surfacesMissing | length' "$BUNDLE")" == "0" ]] || { echo "[ERROR] expected no missing surfaces" >&2; exit 1; }
echo "[OK] all-green manifest passed"

echo
echo "Validating bundle shape: required machine-readable fields are present"
jq -e '.verdict and (.summary|type=="object") and (.checks|type=="array") and (.followUps|type=="array") and (.surfaceCoverage|type=="array")' "$BUNDLE" >/dev/null \
  || { echo "[ERROR] evidence bundle missing required fields" >&2; exit 1; }
echo "[OK] evidence bundle shape is valid"

echo
echo "Validating failure path: a blocked release gate fails and surfaces its owning follow-up"
cat >"$WORKDIR/manifest-blocked-gate.json" <<'EOF'
{
  "releaseId": "honua-test-rc",
  "channel": "preview",
  "candidate": { "ref": "deadbeef", "image": { "evidenceState": "passed" } },
  "releaseGates": [
    { "id": "server-security-nightly", "owningRepo": "honua-io/honua-server",
      "requirement": "Security nightly green", "evidenceState": "blocked",
      "blockers": [ { "repo": "honua-io/honua-server", "number": 939,
        "url": "https://github.com/honua-io/honua-server/issues/939", "reason": "Security nightly failing." } ] }
  ],
  "repositoryLanes": [
    { "id": "sdk-js-trunk-ci", "owningRepo": "honua-io/honua-sdk-js", "evidenceState": "passed", "blockers": [] },
    { "id": "admin-docs", "owningRepo": "honua-io/honua-console", "evidenceState": "passed", "blockers": [] },
    { "id": "helm-rc-metadata", "owningRepo": "honua-io/honua-helm", "evidenceState": "passed", "blockers": [] },
    { "id": "terraform-modules", "owningRepo": "honua-io/honua-iac", "evidenceState": "passed", "blockers": [] }
  ],
  "releaseLaneCriteria": []
}
EOF
if COMPAT_TRAIN_BUNDLE_OUTPUT="$BUNDLE" "$VALIDATE" "$WORKDIR/manifest-blocked-gate.json" >/dev/null; then
  echo "[ERROR] validator unexpectedly passed a manifest with a blocked gate" >&2; exit 1
fi
jq -e '.followUps[] | select(.url == "https://github.com/honua-io/honua-server/issues/939")' "$BUNDLE" >/dev/null \
  || { echo "[ERROR] blocked-gate follow-up issue not reported" >&2; exit 1; }
echo "[OK] blocked gate correctly failed and reported its follow-up issue"

echo
echo "Validating failure path: an uncovered required surface (no Terraform lane) is a gap"
cat >"$WORKDIR/manifest-missing-surface.json" <<'EOF'
{
  "releaseId": "honua-test-rc", "channel": "preview",
  "candidate": { "ref": "deadbeef", "image": { "evidenceState": "passed" } },
  "releaseGates": [
    { "id": "server-sdk-compatibility", "owningRepo": "honua-io/honua-server", "evidenceState": "passed", "blockers": [] }
  ],
  "repositoryLanes": [
    { "id": "sdk-js-trunk-ci", "owningRepo": "honua-io/honua-sdk-js", "evidenceState": "passed", "blockers": [] },
    { "id": "admin-docs", "owningRepo": "honua-io/honua-console", "evidenceState": "passed", "blockers": [] },
    { "id": "helm-rc-metadata", "owningRepo": "honua-io/honua-helm", "evidenceState": "passed", "blockers": [] }
  ],
  "releaseLaneCriteria": []
}
EOF
if COMPAT_TRAIN_BUNDLE_OUTPUT="$BUNDLE" "$VALIDATE" "$WORKDIR/manifest-missing-surface.json" >/dev/null; then
  echo "[ERROR] validator unexpectedly passed a manifest missing the terraform surface" >&2; exit 1
fi
jq -e '.summary.surfacesMissing | index("terraform")' "$BUNDLE" >/dev/null \
  || { echo "[ERROR] missing terraform surface not reported" >&2; exit 1; }
echo "[OK] uncovered required surface correctly failed"

echo
echo "Validating that a narrowed required-surface set lets a partial train pass"
COMPAT_TRAIN_BUNDLE_OUTPUT="$BUNDLE" \
COMPAT_TRAIN_REQUIRED_SURFACES="server sdk admin helm" \
  "$VALIDATE" "$WORKDIR/manifest-missing-surface.json" >/dev/null
[[ "$(jq -r '.verdict' "$BUNDLE")" == "pass" ]] || { echo "[ERROR] expected pass with narrowed surfaces" >&2; exit 1; }
echo "[OK] narrowed required-surface set passes"

echo
echo "Validating waiver handling: an approved waiver flips a blocked lane to pass"
cat >"$WORKDIR/manifest-waiver.json" <<'EOF'
{
  "releaseId": "honua-test-rc", "channel": "preview",
  "candidate": { "ref": "deadbeef", "image": { "evidenceState": "passed" } },
  "releaseGates": [
    { "id": "server-sdk-compatibility", "owningRepo": "honua-io/honua-server", "evidenceState": "passed", "blockers": [] }
  ],
  "repositoryLanes": [
    { "id": "sdk-js-trunk-ci", "owningRepo": "honua-io/honua-sdk-js", "evidenceState": "passed", "blockers": [] },
    { "id": "admin-docs", "owningRepo": "honua-io/honua-console", "evidenceState": "passed", "blockers": [] },
    { "id": "helm-rc-metadata", "owningRepo": "honua-io/honua-helm", "evidenceState": "blocked",
      "waiver": { "approvedBy": "release-captain", "reason": "Chart pin deferred to RC2." },
      "blockers": [ { "url": "https://github.com/honua-io/honua-helm/issues/1", "reason": "chart pin open" } ] },
    { "id": "terraform-modules", "owningRepo": "honua-io/honua-iac", "evidenceState": "passed", "blockers": [] }
  ],
  "releaseLaneCriteria": []
}
EOF
COMPAT_TRAIN_BUNDLE_OUTPUT="$BUNDLE" "$VALIDATE" "$WORKDIR/manifest-waiver.json" >/dev/null
[[ "$(jq -r '.verdict' "$BUNDLE")" == "pass" ]] || { echo "[ERROR] approved waiver did not flip the lane to pass" >&2; exit 1; }
[[ "$(jq -r '.summary.waived' "$BUNDLE")" == "1" ]] || { echo "[ERROR] expected one waived check" >&2; exit 1; }
echo "[OK] approved waiver accepted"

echo
echo "Validating that disabling waivers re-blocks the waived lane"
if COMPAT_TRAIN_BUNDLE_OUTPUT="$BUNDLE" COMPAT_TRAIN_ALLOW_WAIVERS=false \
  "$VALIDATE" "$WORKDIR/manifest-waiver.json" >/dev/null; then
  echo "[ERROR] validator passed a waived lane with waivers disabled" >&2; exit 1
fi
echo "[OK] waivers-disabled correctly re-blocks"

echo
echo "Validating advisory mode reports gaps but exits 0"
COMPAT_TRAIN_BUNDLE_OUTPUT="$BUNDLE" COMPAT_TRAIN_MODE=advisory \
  "$VALIDATE" "$WORKDIR/manifest-blocked-gate.json" >/dev/null
[[ "$(jq -r '.verdict' "$BUNDLE")" == "fail" ]] || { echo "[ERROR] advisory bundle should still record a fail verdict" >&2; exit 1; }
echo "[OK] advisory mode exits 0 while recording the fail verdict"

echo
echo "Validating a failing client scoreboard blocks an otherwise-green train"
cat >"$WORKDIR/scoreboard-fail.json" <<'EOF'
{ "releases": [ { "release": "2026.06.0", "summary": { "pass": 5, "pending": 1, "fail": 2 } } ] }
EOF
if COMPAT_TRAIN_BUNDLE_OUTPUT="$BUNDLE" \
  COMPAT_TRAIN_SCOREBOARD_MATRIX="$WORKDIR/scoreboard-fail.json" \
  "$VALIDATE" "$WORKDIR/manifest-pass.json" >/dev/null; then
  echo "[ERROR] validator passed despite a failing scoreboard release" >&2; exit 1
fi
echo "[OK] failing scoreboard release correctly blocked"

echo
echo "Validating release-notes output is written"
COMPAT_TRAIN_BUNDLE_OUTPUT="$BUNDLE" \
COMPAT_TRAIN_RELEASE_NOTES_OUTPUT="$WORKDIR/notes.md" \
  "$VALIDATE" "$WORKDIR/manifest-pass.json" >/dev/null
grep -q "compatibility-train release-candidate validation" "$WORKDIR/notes.md" \
  || { echo "[ERROR] release notes were not written as expected" >&2; exit 1; }
echo "[OK] release notes written"

echo
echo "Validating: an honua-esri-compat gate maps to the server surface"
cat >"$WORKDIR/manifest-esri.json" <<'EOF'
{
  "releaseId": "honua-test-rc", "channel": "preview",
  "candidate": { "ref": "deadbeef", "image": { "evidenceState": "passed" } },
  "releaseGates": [
    { "id": "server-esri-sdk-certification", "owningRepo": "honua-io/honua-esri-compat",
      "requirement": "Esri SDKs certify.", "evidenceState": "passed", "blockers": [] }
  ],
  "repositoryLanes": [], "releaseLaneCriteria": []
}
EOF
COMPAT_TRAIN_BUNDLE_OUTPUT="$BUNDLE" COMPAT_TRAIN_MODE=advisory \
  "$VALIDATE" "$WORKDIR/manifest-esri.json" >/dev/null
jq -e '.checks[] | select(.id=="server-esri-sdk-certification") | .surface=="server"' "$BUNDLE" >/dev/null \
  || { echo "[ERROR] honua-esri-compat gate did not map to the server surface" >&2; exit 1; }
echo "[OK] esri-compat maps to server surface"

echo
echo "Compatibility-train release-candidate validation smoke check passed."
