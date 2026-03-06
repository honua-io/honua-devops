#!/usr/bin/env bash

set -euo pipefail

usage() {
  cat <<'EOF'
Usage:
  HONUA_SMOKE_BASE_URL=https://host ./scripts/smoke-contract.sh

Environment:
  HONUA_SMOKE_BASE_URL          Base URL to test (required if not passed as arg)
  HONUA_SMOKE_READINESS_PATH    Default: /healthz/ready
  HONUA_SMOKE_LIVENESS_PATH     Default: /healthz/live
  HONUA_SMOKE_VERSION_PATH      Default: /api/v1/admin/version
  HONUA_SMOKE_API_KEY           Optional API key for admin endpoint check
  HONUA_SMOKE_TIMEOUT_SECONDS   Default: 20
EOF
}

BASE_URL="${HONUA_SMOKE_BASE_URL:-${1:-}}"
if [[ -z "$BASE_URL" ]]; then
  usage
  exit 1
fi

READINESS_PATH="${HONUA_SMOKE_READINESS_PATH:-/healthz/ready}"
LIVENESS_PATH="${HONUA_SMOKE_LIVENESS_PATH:-/healthz/live}"
VERSION_PATH="${HONUA_SMOKE_VERSION_PATH:-/api/v1/admin/version}"
API_KEY="${HONUA_SMOKE_API_KEY:-}"
TIMEOUT_SECONDS="${HONUA_SMOKE_TIMEOUT_SECONDS:-20}"

probe() {
  local label="$1"
  local url="$2"
  local auth_header="${3:-}"
  local args=(
    --silent
    --show-error
    --output /tmp/honua-smoke-body.txt
    --write-out "%{http_code}"
    --max-time "$TIMEOUT_SECONDS"
  )

  if [[ -n "$auth_header" ]]; then
    args+=(--header "$auth_header")
  fi

  local status
  status="$(curl "${args[@]}" "$url" || true)"

  if [[ "$status" =~ ^2|3 ]]; then
    echo "[PASS] $label -> $status ($url)"
    return 0
  fi

  echo "[FAIL] $label -> ${status:-curl_error} ($url)" >&2
  return 1
}

failures=0

if ! probe "readiness" "${BASE_URL%/}${READINESS_PATH}"; then
  failures=$((failures + 1))
fi

if ! probe "liveness" "${BASE_URL%/}${LIVENESS_PATH}"; then
  failures=$((failures + 1))
fi

if [[ -n "$API_KEY" ]]; then
  if ! probe "admin-version" "${BASE_URL%/}${VERSION_PATH}" "X-API-Key: ${API_KEY}"; then
    failures=$((failures + 1))
  fi
else
  echo "[INFO] HONUA_SMOKE_API_KEY not set; skipping admin-version probe"
fi

if [[ "$failures" -gt 0 ]]; then
  echo "[RESULT] Smoke contract failed ($failures failures)." >&2
  exit 1
fi

echo "[RESULT] Smoke contract passed."
