#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
GATE="$REPO_ROOT/scripts/compat-train-release-gate.sh"

require_command() {
  if ! command -v "$1" >/dev/null 2>&1; then
    echo "[ERROR] required command not found: $1" >&2
    exit 1
  fi
}

require_command jq

WORKDIR="$(mktemp -d)"
trap 'rm -rf "$WORKDIR"' EXIT

# A two-repo train keeps the smoke focused and fast.
TRAIN="honua-server honua-sdk-python"

echo "Validating success path: all repos green against live targets, scoreboard clean"
cat >"$WORKDIR/evidence-live.json" <<'EOF'
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
  }
}
EOF
COMPAT_TRAIN_REPOS="$TRAIN" \
COMPAT_TRAIN_SCOREBOARD_MATRIX="$REPO_ROOT/compatibility/scoreboard/compatibility-matrix.json" \
  "$GATE" "$WORKDIR/evidence-live.json"

echo
echo "Validating failure path: a green run that used the seeded local fallback is rejected in live mode"
cat >"$WORKDIR/evidence-localfallback.json" <<'EOF'
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
      "local_stack": true,
      "base_url": "http://localhost:5000",
      "commit": "7c39a0d"
    }
  }
}
EOF
if COMPAT_TRAIN_REPOS="$TRAIN" "$GATE" "$WORKDIR/evidence-localfallback.json"; then
  echo "[ERROR] gate unexpectedly certified a local-fallback run in live mode" >&2
  exit 1
fi
echo "[OK] local-fallback run correctly blocked in live mode"

echo
echo "Validating that the same local-fallback run is accepted in 'any' mode (CI/dev use)"
COMPAT_TRAIN_REPOS="$TRAIN" COMPAT_TRAIN_MODE=any \
  "$GATE" "$WORKDIR/evidence-localfallback.json"

echo
echo "Validating failure path: a green run with an absent local_stack signal is not certifiable in live mode"
cat >"$WORKDIR/evidence-unknownstack.json" <<'EOF'
{
  "candidate": { "version": "2026.06.0-rc.1" },
  "repos": {
    "honua-server": { "status": "pass", "local_stack": false, "base_url": "https://s/api" },
    "honua-sdk-python": { "status": "pass", "base_url": "https://s/api" }
  }
}
EOF
if COMPAT_TRAIN_REPOS="$TRAIN" "$GATE" "$WORKDIR/evidence-unknownstack.json"; then
  echo "[ERROR] gate unexpectedly certified a run with unknown local_stack in live mode" >&2
  exit 1
fi
echo "[OK] unknown local_stack correctly blocked in live mode"

echo
echo "Validating failure path: a failing repo run blocks the train"
cat >"$WORKDIR/evidence-failrun.json" <<'EOF'
{
  "candidate": { "version": "2026.06.0-rc.2" },
  "repos": {
    "honua-server": { "status": "pass", "local_stack": false, "base_url": "https://s/api" },
    "honua-sdk-python": { "status": "fail", "local_stack": false, "base_url": "https://s/api" }
  }
}
EOF
if COMPAT_TRAIN_REPOS="$TRAIN" "$GATE" "$WORKDIR/evidence-failrun.json"; then
  echo "[ERROR] gate unexpectedly certified a train with a failing repo run" >&2
  exit 1
fi
echo "[OK] failing repo run correctly blocked"

echo
echo "Validating failure path: missing evidence for a required repo blocks the train"
cat >"$WORKDIR/evidence-missing.json" <<'EOF'
{
  "candidate": { "version": "2026.06.0-rc.3" },
  "repos": {
    "honua-server": { "status": "pass", "local_stack": false, "base_url": "https://s/api" }
  }
}
EOF
if COMPAT_TRAIN_REPOS="$TRAIN" "$GATE" "$WORKDIR/evidence-missing.json"; then
  echo "[ERROR] gate unexpectedly certified a train with a missing required repo" >&2
  exit 1
fi
echo "[OK] missing required repo correctly blocked"

echo
echo "Validating non-blocking path: a 'skipped' repo (unenrolled consumer) does not block the train"
# conformance-gate emits status=skipped for unenrolled consumers and rc-aggregate
# treats it as non-failing; the release-gate must agree rather than route it to fail.
cat >"$WORKDIR/evidence-skipped.json" <<'EOF'
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
      "status": "skipped",
      "base_url": "unset"
    }
  }
}
EOF
if ! COMPAT_TRAIN_REPOS="$TRAIN" "$GATE" "$WORKDIR/evidence-skipped.json"; then
  echo "[ERROR] gate unexpectedly blocked a train with a skipped (non-blocking) repo" >&2
  exit 1
fi
echo "[OK] skipped repo correctly treated as non-blocking"

echo
echo "Validating env-var override precedence over evidence file"
# Evidence says honua-sdk-python used the local fallback; env override flips it to a live target.
COMPAT_TRAIN_REPOS="$TRAIN" \
COMPAT_TRAIN_REPO_HONUA_SDK_PYTHON_LOCAL_STACK=false \
COMPAT_TRAIN_REPO_HONUA_SDK_PYTHON_BASE_URL="https://staging.honua.example/api" \
  "$GATE" "$WORKDIR/evidence-localfallback.json"

echo
echo "Validating strict scoreboard mode blocks on a pending client"
cat >"$WORKDIR/scoreboard-pending.json" <<'EOF'
{
  "releases": [
    {
      "release": "2026.06.0",
      "summary": { "pass": 6, "pending": 2, "fail": 0 }
    }
  ]
}
EOF
if COMPAT_TRAIN_REPOS="$TRAIN" \
  COMPAT_TRAIN_STRICT_SCOREBOARD=true \
  COMPAT_TRAIN_SCOREBOARD_MATRIX="$WORKDIR/scoreboard-pending.json" \
  "$GATE" "$WORKDIR/evidence-live.json"; then
  echo "[ERROR] strict scoreboard mode unexpectedly passed with pending clients" >&2
  exit 1
fi
echo "[OK] strict scoreboard mode correctly blocked on pending clients"

echo
echo "Validating non-strict mode tolerates pending clients"
COMPAT_TRAIN_REPOS="$TRAIN" \
COMPAT_TRAIN_SCOREBOARD_MATRIX="$WORKDIR/scoreboard-pending.json" \
  "$GATE" "$WORKDIR/evidence-live.json"

echo
echo "Validating a failing scoreboard release blocks the train even with green repos"
cat >"$WORKDIR/scoreboard-fail.json" <<'EOF'
{
  "releases": [
    { "release": "2026.06.0", "summary": { "pass": 5, "pending": 1, "fail": 2 } }
  ]
}
EOF
if COMPAT_TRAIN_REPOS="$TRAIN" \
  COMPAT_TRAIN_SCOREBOARD_MATRIX="$WORKDIR/scoreboard-fail.json" \
  "$GATE" "$WORKDIR/evidence-live.json"; then
  echo "[ERROR] gate unexpectedly certified a train with a failing scoreboard release" >&2
  exit 1
fi
echo "[OK] failing scoreboard release correctly blocked"

echo
echo "Validating release-notes output is written"
COMPAT_TRAIN_REPOS="$TRAIN" \
COMPAT_TRAIN_RELEASE_NOTES_OUTPUT="$WORKDIR/notes.md" \
  "$GATE" "$WORKDIR/evidence-live.json" >/dev/null
if ! grep -q "compatibility-train release-candidate gate" "$WORKDIR/notes.md"; then
  echo "[ERROR] release notes were not written as expected" >&2
  exit 1
fi
echo "[OK] release notes written"

echo
echo "Compatibility-train release-candidate gate smoke check passed."
