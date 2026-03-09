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

PORT_FILE="$WORKDIR/mock-server.port"
COUNTER_FILE="$WORKDIR/flaky.counter"
printf "0" >"$COUNTER_FILE"

python3 - "$PORT_FILE" "$COUNTER_FILE" >"$WORKDIR/mock-server.log" 2>&1 <<'PY' &
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
import sys

port_file = sys.argv[1]
counter_file = sys.argv[2]

good_payload = {
    "availability_percent": 99.8,
    "error_rate_percent": 0.1,
    "p95_latency_ms": 640,
    "burn_rates": {
        "5m": 1.2,
        "30m": 0.8,
        "6h": 0.4,
    },
}

bad_payload = {
    "availability_percent": 97.9,
    "error_rate_percent": 1.6,
    "p95_latency_ms": 1490,
    "burn_rates": {
        "5m": 18.0,
        "30m": 7.1,
        "6h": 3.8,
    },
}

class Handler(BaseHTTPRequestHandler):
    def do_GET(self):
        if self.path == "/good.json":
            self._send(good_payload)
            return

        if self.path == "/flaky.json":
            with open(counter_file, "r+", encoding="utf-8") as handle:
                count = int(handle.read().strip() or "0")
                handle.seek(0)
                handle.write(str(count + 1))
                handle.truncate()

            self._send(good_payload if count == 0 else bad_payload)
            return

        self._send({"error": "not-found", "path": self.path}, status=404)

    def log_message(self, format, *args):
        return

    def _send(self, payload, status=200):
        body = json.dumps(payload).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
with open(port_file, "w", encoding="utf-8") as handle:
    handle.write(str(server.server_port))
    handle.flush()

server.serve_forever()
PY
SERVER_PID="$!"

for _ in $(seq 1 50); do
  if [[ -s "$PORT_FILE" ]]; then
    break
  fi
  sleep 0.1
done

if [[ ! -s "$PORT_FILE" ]]; then
  echo "[ERROR] SLO watch mock server did not start" >&2
  exit 1
fi

BASE_URL="http://127.0.0.1:$(cat "$PORT_FILE")"
ROLLBACK_MARKER="$WORKDIR/rollback-marker.txt"

echo "Validating SLO watch pass path"
HONUA_SLO_JSON_URL="$BASE_URL/good.json" \
SLO_ENABLE_BURN_RATE_CHECKS=true \
SLO_WATCH_INTERVAL_SECONDS=0 \
SLO_WATCH_MAX_SAMPLES=2 \
  "$REPO_ROOT/scripts/slo-release-watch.sh"

echo "Validating SLO watch rollback path"
set +e
HONUA_SLO_JSON_URL="$BASE_URL/flaky.json" \
SLO_ENABLE_BURN_RATE_CHECKS=true \
SLO_WATCH_INTERVAL_SECONDS=0 \
SLO_WATCH_MAX_SAMPLES=3 \
SLO_WATCH_CONSECUTIVE_FAILURES=2 \
SLO_WATCH_AUTO_ROLLBACK=true \
SLO_WATCH_ROLLBACK_COMMAND="printf rollback-triggered > '$ROLLBACK_MARKER'" \
  "$REPO_ROOT/scripts/slo-release-watch.sh"
exit_code=$?
set -e

if [[ "$exit_code" -ne 2 ]]; then
  echo "[ERROR] Expected SLO watch rollback exit code 2, got ${exit_code}." >&2
  exit 1
fi

test -f "$ROLLBACK_MARKER"
grep -nF -- "rollback-triggered" "$ROLLBACK_MARKER" >/dev/null

echo "SLO watch smoke check passed."
