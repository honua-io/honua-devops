#!/usr/bin/env bash

# Self-test for scripts/compat-train-live-probe.sh.
#
# Exercises the live-probe layer fully OFFLINE (no GitHub auth, no live target,
# no cloud creds), proving the correctness rule for honua-devops#41: a probe that
# cannot truly run reports BLOCKED with its missing dependency and NEVER emits a
# green it did not verify. It also proves the probes that *can* run offline (Helm
# Chart.yaml inspection) actually pass/fail correctly, and that live mode never
# fails the run on blocked-only probes.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
PROBE="$REPO_ROOT/scripts/compat-train-live-probe.sh"

require_command() {
  if ! command -v "$1" >/dev/null 2>&1; then
    echo "[ERROR] required command not found: $1" >&2
    exit 1
  fi
}
require_command jq

WORKDIR="$(mktemp -d)"
trap 'rm -rf "$WORKDIR"' EXIT
BUNDLE="$WORKDIR/probe-bundle.json"

# A manifest that cites run URLs (so github-run probes engage) and a candidate
# image with no tag/digest (so the image probe is BLOCKED, matching reality).
cat >"$WORKDIR/manifest.json" <<'EOF'
{
  "releaseId": "honua-test-rc",
  "channel": "preview",
  "candidate": {
    "ref": "deadbeefdeadbeefdeadbeefdeadbeefdeadbeef",
    "refSource": "feature/test",
    "image": { "repository": "ghcr.io/honua-io/honua-server", "tag": null, "digest": null }
  },
  "releaseGates": [
    { "id": "server-sdk-compatibility", "owningRepo": "honua-io/honua-server",
      "evidenceState": "passed",
      "latestEvidence": { "runUrl": "https://github.com/honua-io/honua-server/actions/runs/25525446417" } }
  ],
  "repositoryLanes": [
    { "id": "sdk-js-trunk-ci", "owningRepo": "honua-io/honua-sdk-js",
      "evidenceState": "passed",
      "latestEvidence": { "runUrl": "https://github.com/honua-io/honua-sdk-js/actions/runs/25487920652" } }
  ]
}
EOF

PASS=0
FAIL=0
check() {
  # check <description> <jq-filter-on-bundle> <expected>
  local desc="$1" filter="$2" expected="$3" actual
  actual="$(jq -r "$filter" "$BUNDLE")"
  if [[ "$actual" == "$expected" ]]; then
    echo "  ok: $desc"
    PASS=$((PASS + 1))
  else
    echo "  FAIL: $desc (expected '$expected', got '$actual')"
    FAIL=$((FAIL + 1))
  fi
}

# --- Case 1: fully offline (no gh auth, no base URL, no helm, no creds) -------
# Force the no-auth path even if the test host happens to have gh/GITHUB_TOKEN,
# so the BLOCKED-not-faked contract is deterministically exercised.
echo "[case] fully offline — every external probe must be BLOCKED, never green"
env -u GITHUB_TOKEN PATH="$(dirname "$(command -v jq)"):/usr/bin:/bin" \
  HONUA_PROBE_BUNDLE_OUTPUT="$BUNDLE" \
  HONUA_PROBE_MODE=advisory \
  bash "$PROBE" "$WORKDIR/manifest.json" >/dev/null

check "bundle kind" '.kind' 'compat-train-live-probe'
check "no probe is falsely passed offline" '[.probes[] | select(.state=="passed")] | length' '0'
check "github-run is blocked without auth" \
  '[.probes[] | select(.id|startswith("github-run")) | select(.state=="blocked")] | length > 0' 'true'
check "candidate-image blocked" '.probes[] | select(.id=="candidate-image") | .state' 'blocked'
check "server-health blocked (no base url)" '.probes[] | select(.id=="server-health") | .state' 'blocked'
check "helm-metadata blocked (no chart)" '.probes[] | select(.id=="helm-metadata") | .state' 'blocked'
check "terraform-plan blocked (no creds)" '.probes[] | select(.id=="terraform-plan") | .state' 'blocked'
check "every blocked probe documents a missing dependency" \
  '[.probes[] | select(.state=="blocked") | select(.missing==null)] | length' '0'
check "terraform missing names honua-iac#30" \
  '.probes[] | select(.id=="terraform-plan") | (.missing | contains("honua-iac#30"))' 'true'
check "server surface rolls up to blocked (no live pass)" \
  '.surfaceProbeCoverage[] | select(.surface=="server") | .status' 'blocked'

# --- Case 2: a real Helm chart with a real appVersion -> helm probe PASSES ----
echo "[case] real chart appVersion -> helm-metadata passes"
cat >"$WORKDIR/Chart.yaml" <<'EOF'
apiVersion: v2
name: honua-server
version: 0.2.0
appVersion: "2026.05.0-rc.1"
EOF
env -u GITHUB_TOKEN PATH="$(dirname "$(command -v jq)"):/usr/bin:/bin" \
  HONUA_PROBE_BUNDLE_OUTPUT="$BUNDLE" \
  HONUA_PROBE_HELM_CHART="$WORKDIR/Chart.yaml" \
  bash "$PROBE" "$WORKDIR/manifest.json" >/dev/null
check "helm-metadata passes with a real appVersion" \
  '.probes[] | select(.id=="helm-metadata") | .state' 'passed'
check "helm surface verified" \
  '.surfaceProbeCoverage[] | select(.surface=="helm") | .status' 'verified'

# --- Case 3: a placeholder appVersion -> helm probe FAILS ---------------------
echo "[case] placeholder chart appVersion -> helm-metadata fails"
cat >"$WORKDIR/Chart-bad.yaml" <<'EOF'
apiVersion: v2
name: honua-server
version: 0.0.0
appVersion: "latest"
EOF
env -u GITHUB_TOKEN PATH="$(dirname "$(command -v jq)"):/usr/bin:/bin" \
  HONUA_PROBE_BUNDLE_OUTPUT="$BUNDLE" \
  HONUA_PROBE_HELM_CHART="$WORKDIR/Chart-bad.yaml" \
  HONUA_PROBE_MODE=advisory \
  bash "$PROBE" "$WORKDIR/manifest.json" >/dev/null
check "helm-metadata fails on placeholder appVersion" \
  '.probes[] | select(.id=="helm-metadata") | .state' 'failed'

# --- Case 4: live mode with a real failure fails the run ---------------------
echo "[case] live mode fails the run on a real (non-blocked) failure"
set +e
env -u GITHUB_TOKEN PATH="$(dirname "$(command -v jq)"):/usr/bin:/bin" \
  HONUA_PROBE_BUNDLE_OUTPUT="$BUNDLE" \
  HONUA_PROBE_HELM_CHART="$WORKDIR/Chart-bad.yaml" \
  HONUA_PROBE_MODE=live \
  bash "$PROBE" "$WORKDIR/manifest.json" >/dev/null
rc=$?
set -e
if [[ "$rc" -ne 0 ]]; then
  echo "  ok: live mode exits non-zero on a real failed probe (rc=$rc)"; PASS=$((PASS + 1))
else
  echo "  FAIL: live mode should exit non-zero when a probe actually failed"; FAIL=$((FAIL + 1))
fi

# --- Case 5: live mode with ONLY blocked probes does NOT fail the run --------
echo "[case] live mode does not fail on blocked-only (missing deps are gaps, not regressions)"
set +e
env -u GITHUB_TOKEN PATH="$(dirname "$(command -v jq)"):/usr/bin:/bin" \
  HONUA_PROBE_BUNDLE_OUTPUT="$BUNDLE" \
  HONUA_PROBE_MODE=live \
  bash "$PROBE" "$WORKDIR/manifest.json" >/dev/null
rc=$?
set -e
if [[ "$rc" -eq 0 ]]; then
  echo "  ok: live mode exits 0 when every reachable probe is blocked"; PASS=$((PASS + 1))
else
  echo "  FAIL: live mode must not fail on blocked-only probes (rc=$rc)"; FAIL=$((FAIL + 1))
fi

# --- Case 6: only passed evidenceState signals are re-verified ----------------
# A signal the manifest recorded as blocked/waived/pending must NOT be re-fetched
# against GitHub: doing so could both falsely fail a release the manifest already
# accounted for and manufacture "verified" coverage the manifest never asserted.
# With every cited runUrl on a non-passed signal, the github-run probe has nothing
# to re-verify and reports the "no run URLs" blocked sentinel rather than a
# per-surface state derived from those non-passed runs.
echo "[case] non-passed evidenceState signals are excluded from re-verification"
cat >"$WORKDIR/manifest-nonpassed.json" <<'EOF'
{
  "releaseId": "honua-test-rc",
  "channel": "preview",
  "candidate": {
    "ref": "deadbeefdeadbeefdeadbeefdeadbeefdeadbeef",
    "refSource": "feature/test",
    "image": { "repository": "ghcr.io/honua-io/honua-server", "tag": null, "digest": null }
  },
  "releaseGates": [
    { "id": "server-sdk-compatibility", "owningRepo": "honua-io/honua-server",
      "evidenceState": "blocked",
      "latestEvidence": { "runUrl": "https://github.com/honua-io/honua-server/actions/runs/25525446417" } }
  ],
  "repositoryLanes": [
    { "id": "sdk-js-trunk-ci", "owningRepo": "honua-io/honua-sdk-js",
      "evidenceState": "pending",
      "latestEvidence": { "runUrl": "https://github.com/honua-io/honua-sdk-js/actions/runs/25487920652" } }
  ]
}
EOF
env -u GITHUB_TOKEN PATH="$(dirname "$(command -v jq)"):/usr/bin:/bin" \
  HONUA_PROBE_BUNDLE_OUTPUT="$BUNDLE" \
  HONUA_PROBE_MODE=advisory \
  bash "$PROBE" "$WORKDIR/manifest-nonpassed.json" >/dev/null
check "github-run reports the no-runs-to-re-verify sentinel for non-passed signals" \
  '.probes[] | select(.id=="github-run") | .state' 'blocked'
check "no per-surface github-run probe is emitted for non-passed signals" \
  '[.probes[] | select(.id|startswith("github-run:"))] | length' '0'

echo
echo "[smoke] $PASS passed, $FAIL failed"
[[ "$FAIL" -eq 0 ]]
