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

require_command dotnet
require_command python3

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

CUSTOMER_ROOT="$WORKDIR/customer-repo"
TERRAFORM_ROOT="$WORKDIR/honua-iac"
mkdir -p "$TERRAFORM_ROOT"
PORT_FILE="$WORKDIR/mock-server.port"

python3 - "$PORT_FILE" >"$WORKDIR/mock-server.log" 2>&1 <<'PY' &
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
import sys

port_file = sys.argv[1]

class Handler(BaseHTTPRequestHandler):
    def do_GET(self):
        self._respond()

    def do_POST(self):
        _ = self.rfile.read(int(self.headers.get("Content-Length", "0")))
        self._respond()

    def log_message(self, format, *args):
        return

    def _respond(self):
        payload = json.dumps({"status": "ok", "path": self.path}).encode("utf-8")
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(payload)))
        self.end_headers()
        self.wfile.write(payload)

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
  echo "[ERROR] mock backend server did not start" >&2
  exit 1
fi

MOCK_PORT="$(cat "$PORT_FILE")"
MOCK_BASE_URL="http://127.0.0.1:${MOCK_PORT}"

bash "$REPO_ROOT/scripts/bootstrap-customer-repo.sh" \
  --customer-root "$CUSTOMER_ROOT" \
  --service parcels-api \
  --runtime-target aks \
  --provider codex \
  --revision release/2026.04 \
  --terraform-local-path "$TERRAFORM_ROOT" \
  --honua-api-base-url "$MOCK_BASE_URL" \
  --otel-base-url "$MOCK_BASE_URL" \
  --run-preflight \
  --force

test -f "$CUSTOMER_ROOT/.env.local"
test -f "$CUSTOMER_ROOT/bootstrap/parcels-api-bootstrap-checklist.md"
test -f "$CUSTOMER_ROOT/bootstrap/configure-honua-operator-ci.sh"
test -f "$CUSTOMER_ROOT/desired-state/bundles/parcels-api/prod.servicebundle.yaml"
test -f "$CUSTOMER_ROOT/.github/workflows/honua-operator-validation.yml"
test -f "$CUSTOMER_ROOT/.github/workflows/honua-operator-preflight.yml"
bash -n "$CUSTOMER_ROOT/bootstrap/configure-honua-operator-ci.sh"
grep -nF -- "workflow_dispatch:" "$CUSTOMER_ROOT/.github/workflows/honua-operator-preflight.yml" >/dev/null
grep -nF -- "HONUA_DEVOPS_HONUA_API_BASE_URL" "$CUSTOMER_ROOT/bootstrap/configure-honua-operator-ci.sh" >/dev/null
grep -nF -- "load_dotenv_file" "$CUSTOMER_ROOT/bootstrap/configure-honua-operator-ci.sh" >/dev/null
if grep -nF -- "source \"\$REPO_ROOT/.env" "$CUSTOMER_ROOT/bootstrap/configure-honua-operator-ci.sh" >/dev/null; then
  echo "[ERROR] generated CI helper must not source dotenv files as shell." >&2
  exit 1
fi

FAKE_BIN="$WORKDIR/fake-bin"
mkdir -p "$FAKE_BIN"
cat >"$FAKE_BIN/gh" <<EOF
#!/usr/bin/env bash
printf '%s\n' "\$*" >> "$WORKDIR/gh-calls.log"
while [[ \$# -gt 0 ]]; do
  if [[ "\$1" == "--body" ]]; then
    shift
    printf 'BODY=%s\n' "\${1:-}" >> "$WORKDIR/gh-calls.log"
  elif [[ "\$1" == "--body-file" ]]; then
    shift
    if [[ "\${1:-}" == "-" ]]; then
      printf 'BODY=%s\n' "\$(cat)" >> "$WORKDIR/gh-calls.log"
    fi
  fi
  shift || true
done
EOF
chmod +x "$FAKE_BIN/gh"

printf '\nHONUA_DEVOPS_HONUA_API_KEY="$(touch %s/dotenv-executed)"\n' "$WORKDIR" >> "$CUSTOMER_ROOT/.env.local"
PATH="$FAKE_BIN:$PATH" bash "$CUSTOMER_ROOT/bootstrap/configure-honua-operator-ci.sh" --repo honua-io/customer-smoke >/dev/null
if [[ -e "$WORKDIR/dotenv-executed" ]]; then
  echo "[ERROR] generated CI helper executed dotenv command substitution." >&2
  exit 1
fi
grep -nF -- 'BODY=$(touch ' "$WORKDIR/gh-calls.log" >/dev/null

echo "Customer bootstrap smoke check passed."
