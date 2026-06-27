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
FIXTURES_ROOT="$WORKDIR/releases"
mkdir -p "$FIXTURES_ROOT/2026.03.0/demo-service/evidence" "$FIXTURES_ROOT/2026.03.1/demo-service/evidence"

cat >"$FIXTURES_ROOT/2026.03.0/demo-service/evidence/session.json" <<'EOF'
{
  "service_id": "demo-service",
  "service_title": "Demo Service",
  "clients": [
    {
      "name": "QGIS",
      "version": "3.38",
      "status": "pass"
    },
    {
      "name": "OpenLayers",
      "version": "10.0",
      "status": "pass"
    }
  ]
}
EOF

cat >"$FIXTURES_ROOT/2026.03.0/demo-service/compatibility-results.json" <<'EOF'
{
  "release": "2026.03.0",
  "release_date": "Mon, 02 Mar 2026 00:00:00 GMT",
  "service_id": "demo-service",
  "service_title": "Demo Service",
  "source_pack": "compatibility/releases/2026.03.0/demo-service",
  "clients": [
    {
      "name": "QGIS",
      "status": "pending",
      "protocols": [
        { "name": "WMTS", "status": "pending" }
      ]
    },
    {
      "name": "OpenLayers",
      "status": "pending",
      "protocols": [
        { "name": "WMTS", "status": "pending" }
      ]
    }
  ]
}
EOF

cat >"$FIXTURES_ROOT/2026.03.1/demo-service/evidence/session.json" <<'EOF'
{
  "service_id": "demo-service",
  "service_title": "Demo Service",
  "clients": [
    {
      "name": "QGIS",
      "version": "3.38",
      "status": "pass"
    },
    {
      "name": "OpenLayers",
      "version": "10.0",
      "status": "pass"
    },
    {
      "name": "Power BI",
      "version": "2.141",
      "status": "pass"
    }
  ]
}
EOF

cat >"$FIXTURES_ROOT/2026.03.1/demo-service/compatibility-results.json" <<'EOF'
{
  "release": "2026.03.1",
  "release_date": "Mon, 09 Mar 2026 00:00:00 GMT",
  "service_id": "demo-service",
  "service_title": "Demo Service",
  "source_pack": "compatibility/releases/2026.03.1/demo-service",
  "clients": [
    {
      "name": "QGIS",
      "status": "pass",
      "protocols": [
        { "name": "WMTS", "status": "pass" }
      ]
    },
    {
      "name": "OpenLayers",
      "status": "pass",
      "protocols": [
        { "name": "WMTS", "status": "pass" }
      ]
    },
    {
      "name": "Power BI",
      "status": "pass",
      "protocols": [
        { "name": "OData v4", "status": "pass" }
      ]
    }
  ]
}
EOF

echo "Generating compatibility scoreboard from smoke fixtures"
python3 "$REPO_ROOT/scripts/generate-client-compat-scoreboard.py" \
  --packs-root "$FIXTURES_ROOT" \
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
cp -R "$FIXTURES_ROOT" "$WORKDIR/failing-releases"
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

echo "Validating degraded (warn) release blocking path"
cp -R "$FIXTURES_ROOT" "$WORKDIR/warn-releases"
python3 - "$WORKDIR/warn-releases/2026.03.1/demo-service/compatibility-results.json" <<'PY'
import json
import sys
from pathlib import Path

path = Path(sys.argv[1])
payload = json.loads(path.read_text(encoding="utf-8"))
for client in payload["clients"]:
    if client["name"] == "Power BI":
        client["status"] = "warn"
        client["protocols"][0]["status"] = "warn"
        client["notes"] = "Injected degradation for smoke coverage."
        break
path.write_text(json.dumps(payload, indent=2) + "\n", encoding="utf-8")
PY

set +e
python3 "$REPO_ROOT/scripts/generate-client-compat-scoreboard.py" \
  --packs-root "$WORKDIR/warn-releases" \
  --catalog "$REPO_ROOT/compatibility/clients.catalog.json" \
  --output-dir "$WORKDIR/warn-out" \
  --hard-fail
warn_exit=$?
set -e

if [[ "$warn_exit" -ne 2 ]]; then
  echo "[ERROR] Expected a degraded (warn) release to hard-fail with exit 2, got ${warn_exit}." >&2
  exit 1
fi
# The warn client must be counted in the summary and the badge must NOT be green.
grep -nF -- '"warn": 1' "$WORKDIR/warn-out/compatibility-matrix.json" >/dev/null
if grep -nF -- '"color": "green"' "$WORKDIR/warn-out/badge.json" >/dev/null; then
  echo "[ERROR] Badge stayed green despite a degraded (warn) client." >&2
  exit 1
fi

echo "Validating out-of-vocabulary status fails closed"
cp -R "$FIXTURES_ROOT" "$WORKDIR/unknown-releases"
python3 - "$WORKDIR/unknown-releases/2026.03.1/demo-service/compatibility-results.json" <<'PY'
import json
import sys
from pathlib import Path

path = Path(sys.argv[1])
payload = json.loads(path.read_text(encoding="utf-8"))
payload["clients"][0]["status"] = "degraded"  # not in the closed vocabulary
payload["clients"][0]["protocols"][0]["status"] = "degraded"
path.write_text(json.dumps(payload, indent=2) + "\n", encoding="utf-8")
PY

set +e
python3 "$REPO_ROOT/scripts/generate-client-compat-scoreboard.py" \
  --packs-root "$WORKDIR/unknown-releases" \
  --catalog "$REPO_ROOT/compatibility/clients.catalog.json" \
  --output-dir "$WORKDIR/unknown-out" >/dev/null 2>&1
unknown_exit=$?
set -e

if [[ "$unknown_exit" -eq 0 ]]; then
  echo "[ERROR] Expected an out-of-vocabulary status to fail closed (non-zero exit), got 0." >&2
  exit 1
fi

echo "Client compatibility scoreboard smoke check passed."
