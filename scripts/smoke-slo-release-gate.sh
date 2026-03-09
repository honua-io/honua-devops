#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

require_command() {
  local command_name="$1"
  if ! command -v "$command_name" >/dev/null 2>&1; then
    echo "[ERROR] required command not found: $command_name" >&2
    exit 1
  fi
}

require_command python3
require_command jq

WORKDIR="$(mktemp -d)"
SERVER_PID=""

cleanup() {
  if [[ -n "$SERVER_PID" ]]; then
    kill "$SERVER_PID" >/dev/null 2>&1 || true
    wait "$SERVER_PID" >/dev/null 2>&1 || true
  fi

  rm -rf "$WORKDIR"
}

trap cleanup EXIT

echo "Validating SLO release gate success path from direct env values"
SLO_AVAILABILITY_PERCENT=99.4 \
SLO_ERROR_RATE_PERCENT=0.2 \
SLO_P95_MS=810 \
SLO_BURN_RATE_5M=1.4 \
SLO_BURN_RATE_30M=0.9 \
SLO_BURN_RATE_6H=0.5 \
SLO_ENABLE_BURN_RATE_CHECKS=true \
  "$REPO_ROOT/scripts/slo-release-gate.sh"

echo "Validating SLO release gate failure path from direct env values"
if SLO_AVAILABILITY_PERCENT=97.8 \
  SLO_ERROR_RATE_PERCENT=1.4 \
  SLO_P95_MS=1400 \
  SLO_BURN_RATE_5M=18.0 \
  SLO_BURN_RATE_30M=7.1 \
  SLO_BURN_RATE_6H=3.8 \
  SLO_ENABLE_BURN_RATE_CHECKS=true \
  "$REPO_ROOT/scripts/slo-release-gate.sh"; then
  echo "[ERROR] SLO release gate unexpectedly passed failing metrics" >&2
  exit 1
fi

echo "Validating maintenance suppression path"
SLO_AVAILABILITY_PERCENT=97.8 \
SLO_ERROR_RATE_PERCENT=1.4 \
SLO_P95_MS=1400 \
SLO_BURN_RATE_5M=18.0 \
SLO_BURN_RATE_30M=7.1 \
SLO_BURN_RATE_6H=3.8 \
SLO_ENABLE_BURN_RATE_CHECKS=true \
HONUA_SLO_MAINTENANCE_ACTIVE=true \
  "$REPO_ROOT/scripts/slo-release-gate.sh"

PORT_FILE="$WORKDIR/mock-server.port"

python3 - "$WORKDIR" "$PORT_FILE" >"$WORKDIR/mock-server.log" 2>&1 <<'PY' &
from http.server import SimpleHTTPRequestHandler, ThreadingHTTPServer
import os
import sys

root = sys.argv[1]
port_file = sys.argv[2]

os.chdir(root)
server = ThreadingHTTPServer(("127.0.0.1", 0), SimpleHTTPRequestHandler)
with open(port_file, "w", encoding="utf-8") as handle:
    handle.write(str(server.server_port))
    handle.flush()

server.serve_forever()
PY
SERVER_PID="$!"

cat >"$WORKDIR/slo.json" <<'EOF'
{
  "availability_percent": 99.7,
  "error_rate_percent": 0.1,
  "p95_latency_ms": 640,
  "burn_rates": {
    "5m": 1.1,
    "30m": 0.7,
    "6h": 0.4
  }
}
EOF

for _ in $(seq 1 50); do
  if [[ -s "$PORT_FILE" ]]; then
    break
  fi
  sleep 0.1
done

if [[ ! -s "$PORT_FILE" ]]; then
  echo "[ERROR] SLO JSON mock server did not start" >&2
  exit 1
fi

echo "Validating SLO release gate JSON input path"
HONUA_SLO_JSON_URL="http://127.0.0.1:$(cat "$PORT_FILE")/slo.json" \
SLO_ENABLE_BURN_RATE_CHECKS=true \
  "$REPO_ROOT/scripts/slo-release-gate.sh"

echo "SLO release gate smoke check passed."
