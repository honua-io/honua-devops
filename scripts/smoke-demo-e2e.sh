#!/usr/bin/env bash
#
# smoke-demo-e2e.sh — self-test for scripts/run-demo-e2e.sh.
#
# Runs the harness against a LOCAL stub HTTP server (no network, no real demo)
# so it is safe and deterministic in CI. Exercises BOTH the pass path and the
# fail path, and asserts:
#   * read-path PASS hops produce a "pass" evidence bundle (exit 0)
#   * a broken read target produces a FAIL and exit code 2
#   * the write==read guardrail keeps destructive hops PENDING (never writes)
#   * the evidence JSON is well-formed
#
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
HARNESS="$SCRIPT_DIR/run-demo-e2e.sh"

WORK="$(mktemp -d)"
STUB_PID=""
cleanup() {
  [[ -n "$STUB_PID" ]] && kill "$STUB_PID" 2>/dev/null || true
  rm -rf "$WORK"
}
trap cleanup EXIT

fail() { echo "[SMOKE-FAIL] $*" >&2; exit 1; }
pass() { echo "[SMOKE-PASS] $*"; }

# ---------------------------------------------------------------------------
# Stub Honua demo server: serves the minimal read-path surface the harness
# probes, with real-looking values, so the read assertions can PASS offline.
# ---------------------------------------------------------------------------
PORT="${SMOKE_STUB_PORT:-8791}"
STUB_DIR="$WORK/stub"
mkdir -p "$STUB_DIR"

cat >"$STUB_DIR/server.py" <<'PY'
import http.server, json, struct, zlib, sys

PNG = (b'\x89PNG\r\n\x1a\n' +
       b'\x00\x00\x00\rIHDR' + struct.pack('>II', 1, 1) + b'\x08\x06\x00\x00\x00' +
       struct.pack('>I', zlib.crc32(b'IHDR' + struct.pack('>II',1,1)+b'\x08\x06\x00\x00\x00') & 0xffffffff) +
       b'\x00\x00\x00\x00IEND\xaeB`\x82')

class H(http.server.BaseHTTPRequestHandler):
    def log_message(self, *a): pass
    def _send(self, code, ctype, body):
        self.send_response(code); self.send_header('Content-Type', ctype)
        self.send_header('Content-Length', str(len(body))); self.end_headers()
        self.wfile.write(body)
    def do_GET(self):
        p = self.path
        if p.startswith('/rest/info'):
            return self._send(200, 'application/json', json.dumps({"currentVersion":10.81}).encode())
        if 'returnCountOnly=true' in p:
            return self._send(200, 'application/json', json.dumps({"count":51245}).encode())
        if '/FeatureServer/' in p and '/query' in p:
            return self._send(200, 'application/json', json.dumps({"features":[{"a":1},{"a":2}],"objectIdFieldName":"id"}).encode())
        if '/MapServer/export' in p:
            return self._send(200, 'image/png', PNG)
        if p.startswith('/ogc/features/collections'):
            return self._send(200, 'application/json', json.dumps({"collections":[{"id":"1"}]}).encode())
        if p.startswith('/stac'):
            return self._send(200, 'application/json', json.dumps({"stac_version":"1.0.0","type":"Catalog"}).encode())
        if p.startswith('/odata/$metadata'):
            return self._send(200, 'application/xml', b'<?xml version="1.0"?><edmx:Edmx/>')
        if p.startswith('/ogc/tiles') and 'WebMercatorQuad' not in p:
            return self._send(200, 'application/json', json.dumps({"links":[{"rel":"self"}]}).encode())
        # everything else (WMS, WMTS tile, Geocode) is "not yet published"
        return self._send(404, 'application/json', b'{"error":"not found"}')

http.server.HTTPServer(('127.0.0.1', int(sys.argv[1])), H).serve_forever()
PY

python3 "$STUB_DIR/server.py" "$PORT" &
STUB_PID=$!
# Wait for the stub to accept connections.
for _ in $(seq 1 30); do
  if curl -s -m 2 -o /dev/null "http://127.0.0.1:$PORT/rest/info?f=json"; then break; fi
  sleep 0.3
done
BASE="http://127.0.0.1:$PORT"

# ---------------------------------------------------------------------------
# CASE 1 — happy read path against the stub: expect exit 0 and a "pass" bundle.
# ---------------------------------------------------------------------------
OUT1="$WORK/out1"
set +e
HONUA_DEMO_BASE_URL="$BASE" HONUA_DEMO_TIMEOUT_SECONDS=5 \
  "$HARNESS" --env smoke --output-dir "$OUT1" >"$WORK/case1.log" 2>&1
RC1=$?
set -e
[[ "$RC1" -eq 0 ]] || { cat "$WORK/case1.log"; fail "case1 expected exit 0, got $RC1"; }
[[ -f "$OUT1/demo-e2e-evidence.json" ]] || fail "case1 missing evidence json"
python3 -m json.tool "$OUT1/demo-e2e-evidence.json" >/dev/null || fail "case1 evidence json malformed"
grep -q '"status": "pass"' "$OUT1/demo-e2e-evidence.json" || fail "case1 evidence not status=pass"
grep -q 'demoA.query.*PASS' "$WORK/case1.log" || fail "case1 missing PASS for demoA.query"
grep -q '51245' "$WORK/case1.log" || fail "case1 missing asserted count 51245"
grep -q 'demoA.import.*PENDING' "$WORK/case1.log" || fail "case1 write hop should be PENDING (no write target)"
pass "case1 happy read path: exit 0, status=pass, real count asserted, writes PENDING"

# ---------------------------------------------------------------------------
# CASE 2 — broken read target: expect a FAIL and exit code 2.
# ---------------------------------------------------------------------------
OUT2="$WORK/out2"
set +e
HONUA_DEMO_BASE_URL="http://127.0.0.1:1" HONUA_DEMO_TIMEOUT_SECONDS=3 \
  "$HARNESS" --env smoke-broken --output-dir "$OUT2" >"$WORK/case2.log" 2>&1
RC2=$?
set -e
[[ "$RC2" -eq 2 ]] || { cat "$WORK/case2.log"; fail "case2 expected exit 2 on failure, got $RC2"; }
grep -q '"status": "fail"' "$OUT2/demo-e2e-evidence.json" || fail "case2 evidence not status=fail"
grep -qE 'FAIL=' "$WORK/case2.log" || fail "case2 missing FAIL summary"
pass "case2 broken read target: exit 2, status=fail"

# ---------------------------------------------------------------------------
# CASE 3 — write==read guardrail: destructive hops MUST stay PENDING even with
# --pro-ai-live (never write to the customer-facing alias).
# ---------------------------------------------------------------------------
OUT3="$WORK/out3"
set +e
HONUA_DEMO_BASE_URL="$BASE" HONUA_DEMO_TIMEOUT_SECONDS=5 \
  "$HARNESS" --env smoke-guardrail --write-base-url "$BASE" --pro-ai-live \
  --output-dir "$OUT3" >"$WORK/case3.log" 2>&1
RC3=$?
set -e
[[ "$RC3" -eq 0 ]] || { cat "$WORK/case3.log"; fail "case3 expected exit 0, got $RC3"; }
grep -q 'REFUSED: write base URL equals' "$WORK/case3.log" || fail "case3 guardrail did not refuse write==read"
grep -q 'demoB.rollback.*PENDING' "$WORK/case3.log" || fail "case3 demoB.rollback should be PENDING under guardrail"
pass "case3 write==read guardrail: destructive hops PENDING, never written"

# ---------------------------------------------------------------------------
# CASE 4 — missing required input: expect a config error (exit 1).
# ---------------------------------------------------------------------------
set +e
HONUA_DEMO_BASE_URL="" "$HARNESS" --env nope >"$WORK/case4.log" 2>&1
RC4=$?
set -e
[[ "$RC4" -eq 1 ]] || fail "case4 expected exit 1 on missing base-url, got $RC4"
pass "case4 missing base URL: exit 1 config error"

echo "[SMOKE-OK] all demo-e2e harness self-tests passed"
