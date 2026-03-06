#!/usr/bin/env bash

set -euo pipefail

AVAILABILITY_MIN="${SLO_AVAILABILITY_MIN:-99.0}"
ERROR_RATE_MAX="${SLO_ERROR_RATE_MAX:-1.0}"
P95_MS_MAX="${SLO_P95_MS_MAX:-1200}"

AVAILABILITY="${SLO_AVAILABILITY_PERCENT:-}"
ERROR_RATE="${SLO_ERROR_RATE_PERCENT:-}"
P95_MS="${SLO_P95_MS:-}"
JSON_URL="${HONUA_SLO_JSON_URL:-}"

require_command() {
  if ! command -v "$1" >/dev/null 2>&1; then
    echo "[ERROR] Required command missing: $1" >&2
    exit 1
  fi
}

extract_value() {
  local file="$1"
  local expr="$2"
  jq -r "$expr // empty" "$file"
}

float_le() {
  awk "BEGIN {exit !($1 <= $2)}"
}

float_ge() {
  awk "BEGIN {exit !($1 >= $2)}"
}

if [[ -n "$JSON_URL" ]]; then
  require_command curl
  require_command jq
  tmp="$(mktemp)"
  trap 'rm -f "$tmp"' EXIT
  curl --silent --show-error --fail "$JSON_URL" > "$tmp"

  AVAILABILITY="${AVAILABILITY:-$(extract_value "$tmp" '.availability_percent // .availability // .slo.availability')}"
  ERROR_RATE="${ERROR_RATE:-$(extract_value "$tmp" '.error_rate_percent // .error_rate // .slo.error_rate')}"
  P95_MS="${P95_MS:-$(extract_value "$tmp" '.p95_latency_ms // .latency_p95_ms // .slo.p95_latency_ms')}"
fi

for key in AVAILABILITY ERROR_RATE P95_MS; do
  if [[ -z "${!key:-}" ]]; then
    echo "[ERROR] Missing metric value: $key" >&2
    echo "Provide SLO_* env vars or HONUA_SLO_JSON_URL." >&2
    exit 1
  fi
done

failures=0

if float_ge "$AVAILABILITY" "$AVAILABILITY_MIN"; then
  echo "[PASS] availability ${AVAILABILITY}% >= ${AVAILABILITY_MIN}%"
else
  echo "[FAIL] availability ${AVAILABILITY}% < ${AVAILABILITY_MIN}%"
  failures=$((failures + 1))
fi

if float_le "$ERROR_RATE" "$ERROR_RATE_MAX"; then
  echo "[PASS] error_rate ${ERROR_RATE}% <= ${ERROR_RATE_MAX}%"
else
  echo "[FAIL] error_rate ${ERROR_RATE}% > ${ERROR_RATE_MAX}%"
  failures=$((failures + 1))
fi

if float_le "$P95_MS" "$P95_MS_MAX"; then
  echo "[PASS] p95_latency ${P95_MS}ms <= ${P95_MS_MAX}ms"
else
  echo "[FAIL] p95_latency ${P95_MS}ms > ${P95_MS_MAX}ms"
  failures=$((failures + 1))
fi

if [[ "$failures" -gt 0 ]]; then
  echo "[RESULT] SLO gate failed ($failures checks)."
  exit 1
fi

echo "[RESULT] SLO gate passed."
