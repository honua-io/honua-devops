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

WORKDIR="$(mktemp -d)"
trap 'rm -rf "$WORKDIR"' EXIT

echo "Generating compatibility scoreboard from repo fixtures"
python3 "$REPO_ROOT/scripts/generate-client-compat-scoreboard.py" \
  --packs-root "$REPO_ROOT/compatibility/releases" \
  --catalog "$REPO_ROOT/compatibility/clients.catalog.json" \
  --output-dir "$WORKDIR/out"

test -f "$WORKDIR/out/compatibility-matrix.json"
test -f "$WORKDIR/out/compatibility-matrix.md"
test -f "$WORKDIR/out/index.html"
test -f "$WORKDIR/out/compatibility-changes.xml"
test -f "$WORKDIR/out/badge.json"
python3 -m json.tool "$WORKDIR/out/compatibility-matrix.json" >/dev/null
python3 -m json.tool "$WORKDIR/out/badge.json" >/dev/null
grep -nF -- "Release 2026.03.1" "$WORKDIR/out/compatibility-matrix.md" >/dev/null
grep -nF -- "OpenLayers / WMTS: pending -> pass" "$WORKDIR/out/compatibility-matrix.md" >/dev/null

echo "Validating hard-fail release blocking path"
cp -R "$REPO_ROOT/compatibility/releases" "$WORKDIR/failing-releases"
python3 - "$WORKDIR/failing-releases/2026.03.1/demo-service/compatibility-results.json" <<'PY'
import json
import sys
from pathlib import Path

path = Path(sys.argv[1])
payload = json.loads(path.read_text(encoding="utf-8"))
for client in payload["clients"]:
    if client["name"] == "Power BI":
        client["status"] = "fail"
        client["protocols"][0]["status"] = "fail"
        client["notes"] = "Injected regression for smoke coverage."
        break
path.write_text(json.dumps(payload, indent=2) + "\n", encoding="utf-8")
PY

set +e
python3 "$REPO_ROOT/scripts/generate-client-compat-scoreboard.py" \
  --packs-root "$WORKDIR/failing-releases" \
  --catalog "$REPO_ROOT/compatibility/clients.catalog.json" \
  --output-dir "$WORKDIR/failing-out" \
  --hard-fail
exit_code=$?
set -e

if [[ "$exit_code" -ne 2 ]]; then
  echo "[ERROR] Expected hard-fail scoreboard exit code 2, got ${exit_code}." >&2
  exit 1
fi

echo "Client compatibility scoreboard smoke check passed."
