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
require_command curl

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

python3 - "$PORT_FILE" >"$WORKDIR/mock-server.log" 2>&1 <<'PY' &
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
import sys

port_file = sys.argv[1]
expected_key = "test-admin-key"

class Handler(BaseHTTPRequestHandler):
    def do_GET(self):
        if self.path in ("/healthz/ready", "/healthz/live"):
            self._send(200, {"status": "ok", "path": self.path})
            return

        if self.path == "/api/v1/admin/version":
            if self.headers.get("X-API-Key") == expected_key:
                self._send(200, {"version": "2026.03", "status": "ok"})
            else:
                self._send(401, {"error": "unauthorized"})
            return

        self._send(404, {"error": "not-found", "path": self.path})

    def log_message(self, format, *args):
        return

    def _send(self, status, payload):
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
  echo "[ERROR] smoke-contract mock server did not start" >&2
  exit 1
fi

BASE_URL="http://127.0.0.1:$(cat "$PORT_FILE")"

echo "Validating smoke contract without admin API key"
HONUA_SMOKE_BASE_URL="$BASE_URL" \
  "$REPO_ROOT/scripts/smoke-contract.sh"

echo "Validating smoke contract with admin API key"
HONUA_SMOKE_BASE_URL="$BASE_URL" \
HONUA_SMOKE_API_KEY="test-admin-key" \
  "$REPO_ROOT/scripts/smoke-contract.sh"

echo "Validating smoke contract failure on wrong admin API key"
if HONUA_SMOKE_BASE_URL="$BASE_URL" \
  HONUA_SMOKE_API_KEY="wrong-key" \
  "$REPO_ROOT/scripts/smoke-contract.sh"; then
  echo "[ERROR] smoke contract unexpectedly passed with the wrong admin API key" >&2
  exit 1
fi

echo "Smoke contract smoke check passed."
