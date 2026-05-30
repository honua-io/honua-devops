#!/usr/bin/env bash

# Self-test for scripts/compat-train-conformance-gate.sh (honua-devops#68).
#
# Exercises the PASS path and EVERY block path the issue calls for:
#   - all consumers green against a candidate                 -> PASS
#   - a consumer that breaks (untracked fixture/field)        -> BLOCK
#   - the honua-server#1238 JSONB-projection break, untracked -> BLOCK
#   - a break whose only failing fixtures are tracked gaps    -> PASS (known-expected, recorded)
#   - a NEW/untracked break alongside a tracked gap           -> BLOCK (gap does not mask new drift)
#   - missing candidate (no image/version)                    -> BLOCK (exit 2)
#   - missing / unpinned ('latest') fixtures version          -> BLOCK (exit 2)
#   - a missing verdict for a required consumer               -> BLOCK
#   - env-var override of a consumer status                   -> respected
#   - evidence JSON is emitted in the #41-consumable shape and
#     scripts/compat-train-release-gate.sh accepts the PASS evidence (producer+evaluator compose)
#
# Runs entirely offline (results mode) so CI needs no live cluster or gh auth.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
GATE="$REPO_ROOT/scripts/compat-train-conformance-gate.sh"
RELEASE_GATE="$REPO_ROOT/scripts/compat-train-release-gate.sh"

require_command() {
  if ! command -v "$1" >/dev/null 2>&1; then
    echo "[ERROR] required command not found: $1" >&2
    exit 1
  fi
}
require_command jq

WORKDIR="$(mktemp -d)"
trap 'rm -rf "$WORKDIR"' EXIT

CANDIDATE="ghcr.io/honua-io/honua-server:2026.06.0-rc.1"
FIXTURES="0.1.0-alpha.1"
# Focused enrolled train (qgis-plugin is unenrolled and skipped by default).
TRAIN="honua-sdk-dotnet honua-sdk-js honua-sdk-python honua-mobile"

# --- A registry pinned for the smoke (independent of the shipped one) --------
cat >"$WORKDIR/registry.json" <<'EOF'
{
  "fixtures": { "source_repo": "honua-io/geospatial-grpc" },
  "known_server_gaps": {
    "honua-server#1238": "FeatureServer/OGC JSONB-attribute projection",
    "honua-server#1166": "Temporal query support",
    "honua-server#1167": "Replica / offline sync endpoints",
    "honua-server#1237": "Analysis list/estimate endpoints"
  },
  "consumers": {
    "honua-sdk-dotnet": { "repo": "honua-io/honua-sdk-dotnet", "workflow": "conformance.yml", "candidate_input": "server_image", "fixtures_input": "fixtures_version", "enrolled": true },
    "honua-sdk-js":     { "repo": "honua-io/honua-sdk-js",     "workflow": "integration.yml", "candidate_input": "base_url", "candidate_commit_input": "server_commit", "fixtures_input": "fixtures_version", "enrolled": true },
    "honua-sdk-python": { "repo": "honua-io/honua-sdk-python", "workflow": "conformance.yml", "candidate_input": "server_image", "fixtures_input": "fixtures_version", "enrolled": true },
    "honua-mobile":     { "repo": "honua-io/honua-mobile",     "workflow": "live-server-integration.yml", "candidate_input": "honua_server_image", "fixtures_input": "fixtures_version", "enrolled": true },
    "honua-qgis-plugin":{ "repo": "honua-io/honua-qgis-plugin", "workflow": "", "candidate_input": "", "fixtures_input": "", "enrolled": false }
  }
}
EOF

run_gate() {
  COMPAT_TRAIN_CONSUMER_REGISTRY="$WORKDIR/registry.json" \
  COMPAT_TRAIN_CANDIDATE_IMAGE="$CANDIDATE" \
  COMPAT_TRAIN_FIXTURES_VERSION="$FIXTURES" \
  COMPAT_TRAIN_REPOS="$TRAIN" \
    "$GATE" "$@"
}

# ----------------------------------------------------------------------------
echo "1) PASS path: every consumer green against the candidate"
cat >"$WORKDIR/results-allgreen.json" <<'EOF'
{
  "repos": {
    "honua-sdk-dotnet": { "conclusion": "success", "base_url": "ghcr.io/honua-io/honua-server:2026.06.0-rc.1", "commit": "rc1abc", "local_stack": false },
    "honua-sdk-js":     { "conclusion": "success", "base_url": "ghcr.io/honua-io/honua-server:2026.06.0-rc.1", "commit": "rc1abc", "local_stack": false },
    "honua-sdk-python": { "conclusion": "success", "base_url": "ghcr.io/honua-io/honua-server:2026.06.0-rc.1", "commit": "rc1abc", "local_stack": false },
    "honua-mobile":     { "conclusion": "success", "base_url": "ghcr.io/honua-io/honua-server:2026.06.0-rc.1", "commit": "rc1abc", "local_stack": false }
  }
}
EOF
COMPAT_TRAIN_RESULTS_FILE="$WORKDIR/results-allgreen.json" \
COMPAT_TRAIN_EVIDENCE_OUTPUT="$WORKDIR/evidence-allgreen.json" \
  run_gate
echo "[OK] all-green PASS"

echo
echo "   PASS evidence is in the #41-consumable shape and the release gate accepts it (producer + evaluator compose)"
jq -e '.repos["honua-sdk-python"].status == "pass" and (.repos["honua-sdk-python"] | has("local_stack") and has("base_url") and has("commit"))' "$WORKDIR/evidence-allgreen.json" >/dev/null \
  || { echo "[ERROR] evidence missing required #41 fields" >&2; exit 1; }
# Feed the produced evidence into the existing #41 evaluator in 'any' mode
# (results-mode targets are image refs, not live URLs, so local_stack semantics
# differ; 'any' is the CI/dev composition path).
COMPAT_TRAIN_REPOS="$TRAIN" COMPAT_TRAIN_MODE=any \
  "$RELEASE_GATE" "$WORKDIR/evidence-allgreen.json" >/dev/null \
  || { echo "[ERROR] #41 release gate rejected the produced PASS evidence" >&2; exit 1; }
echo "[OK] #41 release gate consumed the produced evidence"

# ----------------------------------------------------------------------------
echo
echo "2) BLOCK path: a consumer breaks on an untracked renamed/removed field"
cat >"$WORKDIR/results-break.json" <<'EOF'
{
  "repos": {
    "honua-sdk-dotnet": { "conclusion": "success" },
    "honua-sdk-js":     { "conclusion": "success" },
    "honua-sdk-python": { "conclusion": "success" },
    "honua-mobile": {
      "conclusion": "failure",
      "failing_fixtures": [
        { "fixture": "feature_query_response.json", "field": "features[].attributes.status", "gap_issue": null }
      ]
    }
  }
}
EOF
if COMPAT_TRAIN_RESULTS_FILE="$WORKDIR/results-break.json" run_gate; then
  echo "[ERROR] gate did not block on an untracked consumer break" >&2; exit 1
fi
echo "[OK] untracked break correctly BLOCKED"

# ----------------------------------------------------------------------------
echo
echo "3) BLOCK path: the honua-server#1238 JSONB-projection break, UNTRACKED (no gap_issue) still blocks"
cat >"$WORKDIR/results-1238-untracked.json" <<'EOF'
{
  "repos": {
    "honua-sdk-dotnet": { "conclusion": "success" },
    "honua-sdk-js":     { "conclusion": "success" },
    "honua-sdk-python": { "conclusion": "success" },
    "honua-mobile": {
      "conclusion": "failure",
      "failing_fixtures": [
        { "fixture": "feature_query_response.json", "field": "features[].attributes (JSONB projection dropped)", "gap_issue": null }
      ]
    }
  }
}
EOF
if COMPAT_TRAIN_RESULTS_FILE="$WORKDIR/results-1238-untracked.json" run_gate; then
  echo "[ERROR] gate did not block on the #1238-shape break presented as untracked drift" >&2; exit 1
fi
echo "[OK] #1238-shape untracked break correctly BLOCKED"

# ----------------------------------------------------------------------------
echo
echo "4) PASS path: a break whose ONLY failing fixtures are tracked known-server-gaps is green-with-known-gaps (recorded)"
cat >"$WORKDIR/results-gaponly.json" <<'EOF'
{
  "repos": {
    "honua-sdk-dotnet": { "conclusion": "success" },
    "honua-sdk-js":     { "conclusion": "success" },
    "honua-sdk-python": { "conclusion": "success" },
    "honua-mobile": {
      "conclusion": "failure",
      "failing_fixtures": [
        { "fixture": "feature_query_response.json", "field": "features[].attributes", "gap_issue": "honua-server#1238" },
        { "fixture": "feature_query_temporal_response.json", "field": "features[].time", "gap_issue": "honua-server#1166" }
      ]
    }
  }
}
EOF
out="$(COMPAT_TRAIN_RESULTS_FILE="$WORKDIR/results-gaponly.json" \
       COMPAT_TRAIN_EVIDENCE_OUTPUT="$WORKDIR/evidence-gaponly.json" run_gate)"
echo "$out" | grep -q "KNOWN-EXPECTED gaps only" || { echo "[ERROR] gap-only run not reported as known-expected" >&2; exit 1; }
jq -e '.repos["honua-mobile"].status == "pass" and (.repos["honua-mobile"].known_gaps | index("honua-server#1238"))' "$WORKDIR/evidence-gaponly.json" >/dev/null \
  || { echo "[ERROR] known gaps not recorded in evidence" >&2; exit 1; }
echo "[OK] gap-only run PASSES and records the known gaps (not silently swallowed)"

# ----------------------------------------------------------------------------
echo
echo "5) BLOCK path: a NEW/untracked break alongside a tracked gap still blocks (gap must not mask new drift)"
cat >"$WORKDIR/results-mixed.json" <<'EOF'
{
  "repos": {
    "honua-sdk-dotnet": { "conclusion": "success" },
    "honua-sdk-js":     { "conclusion": "success" },
    "honua-sdk-python": { "conclusion": "success" },
    "honua-mobile": {
      "conclusion": "failure",
      "failing_fixtures": [
        { "fixture": "feature_query_response.json", "field": "features[].attributes", "gap_issue": "honua-server#1238" },
        { "fixture": "workspace_create_response.json", "field": "workspace.id (renamed)", "gap_issue": null }
      ]
    }
  }
}
EOF
if COMPAT_TRAIN_RESULTS_FILE="$WORKDIR/results-mixed.json" run_gate; then
  echo "[ERROR] a tracked gap masked a new untracked break" >&2; exit 1
fi
echo "[OK] new drift alongside a tracked gap correctly BLOCKED"

# ----------------------------------------------------------------------------
echo
echo "6) BLOCK path: missing candidate (no image/version) -> exit 2"
set +e
COMPAT_TRAIN_CONSUMER_REGISTRY="$WORKDIR/registry.json" \
COMPAT_TRAIN_FIXTURES_VERSION="$FIXTURES" \
COMPAT_TRAIN_REPOS="$TRAIN" \
COMPAT_TRAIN_RESULTS_FILE="$WORKDIR/results-allgreen.json" \
  "$GATE"
rc=$?
set -e
[[ "$rc" -eq 2 ]] || { echo "[ERROR] missing candidate did not exit 2 (got $rc)" >&2; exit 1; }
echo "[OK] missing candidate correctly BLOCKED (exit 2)"

# ----------------------------------------------------------------------------
echo
echo "7) BLOCK path: missing fixtures version -> exit 2"
set +e
COMPAT_TRAIN_CONSUMER_REGISTRY="$WORKDIR/registry.json" \
COMPAT_TRAIN_CANDIDATE_IMAGE="$CANDIDATE" \
COMPAT_TRAIN_REPOS="$TRAIN" \
COMPAT_TRAIN_RESULTS_FILE="$WORKDIR/results-allgreen.json" \
  "$GATE"
rc=$?
set -e
[[ "$rc" -eq 2 ]] || { echo "[ERROR] missing fixtures version did not exit 2 (got $rc)" >&2; exit 1; }
echo "[OK] missing fixtures version correctly BLOCKED (exit 2)"

echo
echo "   ... and an explicitly 'latest' (unpinned) fixtures version is rejected too"
set +e
COMPAT_TRAIN_CONSUMER_REGISTRY="$WORKDIR/registry.json" \
COMPAT_TRAIN_CANDIDATE_IMAGE="$CANDIDATE" \
COMPAT_TRAIN_FIXTURES_VERSION="latest" \
COMPAT_TRAIN_REPOS="$TRAIN" \
COMPAT_TRAIN_RESULTS_FILE="$WORKDIR/results-allgreen.json" \
  "$GATE"
rc=$?
set -e
[[ "$rc" -eq 2 ]] || { echo "[ERROR] 'latest' fixtures version was not rejected (got $rc)" >&2; exit 1; }
echo "[OK] unpinned 'latest' fixtures version correctly BLOCKED (exit 2)"

# ----------------------------------------------------------------------------
echo
echo "8) BLOCK path: a missing verdict for a required consumer blocks"
cat >"$WORKDIR/results-missingrepo.json" <<'EOF'
{
  "repos": {
    "honua-sdk-dotnet": { "conclusion": "success" },
    "honua-sdk-js":     { "conclusion": "success" },
    "honua-sdk-python": { "conclusion": "success" }
  }
}
EOF
if COMPAT_TRAIN_RESULTS_FILE="$WORKDIR/results-missingrepo.json" run_gate; then
  echo "[ERROR] a missing required-consumer verdict did not block" >&2; exit 1
fi
echo "[OK] missing required-consumer verdict correctly BLOCKED"

# ----------------------------------------------------------------------------
echo
echo "9) env-var override of a consumer status is respected (flip a green results entry to fail)"
if COMPAT_TRAIN_RESULTS_FILE="$WORKDIR/results-allgreen.json" \
   COMPAT_TRAIN_REPO_HONUA_SDK_PYTHON_STATUS=fail \
   run_gate; then
  echo "[ERROR] env override to fail did not block" >&2; exit 1
fi
echo "[OK] env-var status override respected"

# ----------------------------------------------------------------------------
echo
echo "10) unenrolled consumer (qgis-plugin) is skipped by default, required under COMPAT_TRAIN_REQUIRE_ALL"
skip_out="$(COMPAT_TRAIN_CONSUMER_REGISTRY="$WORKDIR/registry.json" \
  COMPAT_TRAIN_CANDIDATE_IMAGE="$CANDIDATE" \
  COMPAT_TRAIN_FIXTURES_VERSION="$FIXTURES" \
  COMPAT_TRAIN_REPOS="honua-sdk-python honua-qgis-plugin" \
  COMPAT_TRAIN_RESULTS_FILE="$WORKDIR/results-allgreen.json" \
  "$GATE")"
echo "$skip_out" | grep -q "honua-qgis-plugin: not enrolled" \
  || { echo "[ERROR] unenrolled consumer not skipped with explanation" >&2; exit 1; }
echo "[OK] unenrolled consumer skipped by default"
set +e
COMPAT_TRAIN_CONSUMER_REGISTRY="$WORKDIR/registry.json" \
COMPAT_TRAIN_CANDIDATE_IMAGE="$CANDIDATE" \
COMPAT_TRAIN_FIXTURES_VERSION="$FIXTURES" \
COMPAT_TRAIN_REPOS="honua-sdk-python honua-qgis-plugin" \
COMPAT_TRAIN_REQUIRE_ALL=true \
COMPAT_TRAIN_RESULTS_FILE="$WORKDIR/results-allgreen.json" \
  "$GATE" >/dev/null
rc=$?
set -e
[[ "$rc" -eq 1 ]] || { echo "[ERROR] COMPAT_TRAIN_REQUIRE_ALL did not make an unenrolled consumer block (got $rc)" >&2; exit 1; }
echo "[OK] COMPAT_TRAIN_REQUIRE_ALL makes an unenrolled consumer block"

# ----------------------------------------------------------------------------
echo
echo "11) the shipped registry is valid and lists the issue's consumer set"
SHIPPED="$REPO_ROOT/compatibility/consumers.conformance.json"
jq -e . "$SHIPPED" >/dev/null || { echo "[ERROR] shipped registry is not valid JSON" >&2; exit 1; }
for c in honua-sdk-dotnet honua-sdk-js honua-sdk-python honua-mobile honua-qgis-plugin; do
  jq -e --arg c "$c" '.consumers | has($c)' "$SHIPPED" >/dev/null \
    || { echo "[ERROR] shipped registry missing consumer $c" >&2; exit 1; }
done
for g in "honua-server#1238" "honua-server#1166" "honua-server#1167" "honua-server#1237"; do
  jq -e --arg g "$g" '.known_server_gaps | has($g)' "$SHIPPED" >/dev/null \
    || { echo "[ERROR] shipped registry missing known gap $g" >&2; exit 1; }
done
echo "[OK] shipped registry covers the consumer set and the tracked known-server gaps"

echo
echo "Compatibility-train conformance gate smoke check passed."
