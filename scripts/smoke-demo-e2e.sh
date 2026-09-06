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
STUB_DIR="$WORK/stub"
mkdir -p "$STUB_DIR"

cat >"$STUB_DIR/server.py" <<'PY'
import http.server, json, struct, zlib, sys, pathlib, threading, urllib.parse, re

PNG = (b'\x89PNG\r\n\x1a\n' +
       b'\x00\x00\x00\rIHDR' + struct.pack('>II', 1, 1) + b'\x08\x06\x00\x00\x00' +
       struct.pack('>I', zlib.crc32(b'IHDR' + struct.pack('>II',1,1)+b'\x08\x06\x00\x00\x00') & 0xffffffff) +
       b'\x00\x00\x00\x00IEND\xaeB`\x82')

work = pathlib.Path(sys.argv[1])
rows = {}
next_id = 1
writes = 0

def mode():
    return (work / 'mode').read_text().strip() if (work / 'mode').exists() else 'pass'

class H(http.server.BaseHTTPRequestHandler):
    def log_message(self, *a): pass
    def _send(self, code, ctype, body):
        self.send_response(code); self.send_header('Content-Type', ctype)
        self.send_header('Content-Length', str(len(body))); self.end_headers()
        self.wfile.write(body)
    def json(self, data, code=200):
        return self._send(code, 'application/json', json.dumps(data).encode())
    def do_GET(self):
        p = self.path
        if p == '/state':
            return self.json({'rows': rows, 'writes': writes})
        if self.server.write_target:
            if self.headers.get('X-API-Key') != 'smoke-admin':
                return self.json({'error': {'code': 401}})
            if '/query?' in p:
                where = urllib.parse.parse_qs(urllib.parse.urlsplit(p).query)['where'][0]
                markers = re.findall(r"'([^']*)'", where)
                found = [v for v in rows.values() if v['attributes']['name'] in markers]
                if mode() == 'bad-readback' and found:
                    found = json.loads(json.dumps(found))
                    found[0]['geometry']['x'] = 0
                return self.json({'features': found})
            return self.json({'objectIdField': 'objectid', 'fields': [
                {'name': 'objectid', 'type': 'esriFieldTypeOID'},
                {'name': 'name', 'type': 'esriFieldTypeString'}]})
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

    def do_POST(self):
        global next_id, writes
        writes += 1
        if not self.server.write_target:
            return self.json({'error': 'READ TARGET MUTATION'}, 500)
        if mode() == 'redirect':
            self.send_response(307)
            self.send_header('Location', f'http://127.0.0.1:{servers[0].server_port}/applyEdits')
            self.end_headers()
            return
        payload = json.loads(self.rfile.read(int(self.headers['Content-Length'])))
        key = self.headers.get('X-API-Key')
        if key != 'smoke-admin' and mode() != 'denial-accepted':
            code = 401 if key is None else 403
            if mode() == 'denial-leak' or (mode() == 'readonly-leak' and key is not None):
                return self.json({'error': {'code': code}, 'features': [{'secret': 'leak'}]})
            if mode() == 'denial-wrong-status':
                return self._send(404, 'application/json', b'')
            if mode() == 'denial-empty':
                return self._send(code, 'application/json', b'')
            return self.json({'error': {'code': code, 'message': 'Denied', 'details': []}})
        if 'adds' in payload:
            result = []
            for row in payload['adds']:
                oid = next_id
                next_id += 1
                row['attributes']['objectid'] = oid
                rows[oid] = row
                result.append({'objectId': oid, 'success': True})
            if mode() == 'import-error':
                return self.json({'error': {'code': 500}})
            return self.json({'addResults': result})
        if 'updates' in payload:
            assert payload['rollbackOnFailure'] is True
            assert len(payload['updates']) == 2
            first, second = payload['updates']
            assert 'objectid' not in second['attributes']
            if mode() == 'rollback-success':
                return self.json({'updateResults': [{'success': True}, {'success': True}]})
            if mode() == 'rollback-fail':
                rows[first['attributes']['objectid']]['attributes'].update(first['attributes'])
            return self.json({'updateResults': [
                {'success': False, 'error': {'code': 1008}},
                {'success': False, 'error': {'code': 1000}}]})
        if 'deletes' in payload:
            if mode() != 'cleanup-fail':
                for oid in payload['deletes']:
                    rows.pop(oid, None)
            return self.json({'deleteResults': [{'objectId': oid, 'success': True} for oid in payload['deletes']]})
        return self.json({'error': {'code': 400}})

servers = []
for write_target in (False, True):
    server = http.server.HTTPServer(('127.0.0.1', 0), H)
    server.write_target = write_target
    servers.append(server)
    threading.Thread(target=server.serve_forever, daemon=True).start()
(work / 'ports').write_text(' '.join(str(s.server_port) for s in servers))
threading.Event().wait()
PY

# Never inherit a real target, key, CLI, or output path from the caller.
for variable in ${!HONUA_DEMO_@}; do unset "$variable"; done
export HONUA_CLI=false
unset GITHUB_STEP_SUMMARY
python3 "$STUB_DIR/server.py" "$WORK" &
STUB_PID=$!
for _ in $(seq 1 30); do
  [[ -f "$WORK/ports" ]] && break
  kill -0 "$STUB_PID" 2>/dev/null || fail "stub failed to start"
  sleep 0.1
done
[[ -f "$WORK/ports" ]] || fail "stub did not start"
read -r READ_PORT WRITE_PORT <"$WORK/ports" || true
BASE="http://127.0.0.1:$READ_PORT"
WRITE_BASE="http://127.0.0.1:$WRITE_PORT"

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
  "$HARNESS" --env smoke-guardrail --write-base-url "$BASE/" --admin-key smoke-admin --pro-ai-live \
  --output-dir "$OUT3" >"$WORK/case3.log" 2>&1
RC3=$?
set -e
[[ "$RC3" -eq 0 ]] || { cat "$WORK/case3.log"; fail "case3 expected exit 0, got $RC3"; }
grep -q 'REFUSED: write base URL equals' "$WORK/case3.log" || fail "case3 guardrail did not refuse write==read"
grep -q 'demoB.rollback.*PENDING' "$WORK/case3.log" || fail "case3 demoB.rollback should be PENDING under guardrail"
curl -fsS "$BASE/state" | python3 -c 'import json,sys; assert json.load(sys.stdin)["writes"] == 0'
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

# Stateful write cases validate persisted rows as well as response status.
run_write_case() {
  local mode="$1" expected="$2" failed_hop="${3:-}"
  echo "$mode" >"$WORK/mode"
  local out="$WORK/write $mode"
  local rc=0
  HONUA_DEMO_WRITE_BASE_URL="$WRITE_BASE" HONUA_DEMO_ADMIN_KEY=smoke-admin \
    HONUA_DEMO_READ_ONLY_KEY=smoke-reader HONUA_DEMO_API_KEY=smoke-read-target \
    "$HARNESS" --env smoke-write --base-url "$BASE" --output-dir "$out" \
    --resource-prefix repeatable --json-summary "$WORK/receipt-$mode/summary.json" \
    >"$WORK/write-$mode.log" 2>&1 || rc=$?
  [[ "$rc" -eq "$expected" ]] || { cat "$WORK/write-$mode.log"; fail "$mode: expected $expected, got $rc"; }
  python3 - "$out/demo-e2e-evidence.json" "$WORK/receipt-$mode/summary.json" "$failed_hop" <<'PY_ASSERT'
import json, sys
from pathlib import Path
receipt = json.loads(Path(sys.argv[1]).read_text())
assert receipt == json.loads(Path(sys.argv[2]).read_text())
assert receipt['schema_version'] == 1
assert 'smoke-admin' not in json.dumps(receipt) and 'smoke-reader' not in json.dumps(receipt)
hops = {h['id']: h for h in receipt['hops']}
if sys.argv[3]:
    assert hops[sys.argv[3]]['status'] == 'FAIL', hops
else:
    for name in ('demoA.import', 'demoB.rollback', 'demoA.cleanup', 'auth.no_key', 'auth.read_only'):
        assert hops[name]['status'] == 'PASS', hops[name]
    assert hops['demoA.import']['asserted_values']['records'] == 1
    assert hops['demoA.import']['asserted_values']['x'] == -156.5
    assert hops['demoB.rollback']['asserted_values']['data_equal'] is True
    for name in ('auth.no_key', 'auth.read_only', 'demoA.cleanup'):
        assert hops[name]['asserted_values']['features'] == []
PY_ASSERT
  if [[ "$mode" != cleanup-fail ]]; then
    curl -fsS "$BASE/state" | python3 -c 'import json,sys; assert json.load(sys.stdin)["rows"] == {}'
  fi
  pass "$mode: exit $rc, receipt asserted, cleanup checked"
}
run_write_case pass 0
run_write_case pass 0 # repeated prefix must leave no rows
run_write_case denial-empty 0
run_write_case bad-readback 2 demoA.import
run_write_case import-error 2 demoA.import
run_write_case rollback-fail 2 demoB.rollback
run_write_case denial-wrong-status 2 auth.no_key
run_write_case denial-leak 2 auth.no_key
run_write_case readonly-leak 2 auth.read_only
run_write_case redirect 2 auth.no_key
run_write_case rollback-success 2 demoB.rollback
run_write_case denial-accepted 2 auth.no_key
# Missing admin key cannot start writes, even when a distinct target exists.
echo pass >"$WORK/mode"
before="$(curl -fsS "$BASE/state")"
HONUA_DEMO_WRITE_BASE_URL="$WRITE_BASE" "$HARNESS" --env smoke-no-admin \
  --base-url "$BASE" --output-dir "$WORK/no-admin" >"$WORK/no-admin.log" 2>&1
[[ "$(curl -fsS "$BASE/state")" == "$before" ]] || fail "missing admin key sent a write"
grep -q 'demoA.import.*PENDING' "$WORK/no-admin.log" || fail "missing admin did not gate import"
pass "missing admin key: writes PENDING, zero mutations"

# Another repo can invoke the script from its cwd and reuse the evidence path.
(cd "$WORK" && HONUA_DEMO_WRITE_BASE_URL="$WRITE_BASE" HONUA_DEMO_ADMIN_KEY=smoke-admin \
  "$HARNESS" --env smoke-external --base-url "$BASE" --output-dir 'external output' \
  --json-summary 'external output/demo-e2e-evidence.json' >"$WORK/external.log" 2>&1)
python3 - "$WORK/external output/demo-e2e-evidence.json" <<'PY_OPTIONAL'
import json, sys
hops = {h['id']: h for h in json.load(open(sys.argv[1]))['hops']}
assert hops['auth.read_only']['status'] == 'PENDING'
assert hops['demoA.cleanup']['status'] == 'PASS'
PY_OPTIONAL
pass "external cwd, spaced paths, same summary path, optional read-only key"

run_write_case cleanup-fail 2 demoA.cleanup

echo "[SMOKE-OK] all demo-e2e harness self-tests passed"
