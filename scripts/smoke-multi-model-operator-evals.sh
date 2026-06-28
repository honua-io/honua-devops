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

SERVER_REPORT="$REPO_ROOT/eval/fixtures/server-eval-report.sample.json"
SCENARIO_DIR="$REPO_ROOT/eval/fixtures/scenarios"

echo "Validating contract-only multi-model eval report"
python3 "$REPO_ROOT/scripts/run-multi-model-operator-evals.py" \
  --server-report "$SERVER_REPORT" \
  --scenario-dir "$SCENARIO_DIR" \
  --output-dir "$WORKDIR/contract" \
  --hard-fail

python3 -m json.tool "$WORKDIR/contract/report.json" >/dev/null
grep -nF -- "Release gate status: \`skipped\`" "$WORKDIR/contract/report.md" >/dev/null

cat >"$WORKDIR/mock_lane.py" <<'PY'
import json
import os
from pathlib import Path

scenario_id = os.environ["HONUA_EVAL_SCENARIO_ID"]
status = "passed"
if os.environ.get("HONUA_EVAL_MOCK_SKIP") == "true" and scenario_id == "package-map-tsunami":
    status = "PassedWithSkips"

payload = {
    "status": status,
    "metrics": {
        "clarificationQuality": 1.0,
        "planValidity": 1.0,
        "executionSuccess": 1.0,
        "resultCorrectness": 1.0,
        "packageUsefulness": 1.0,
    },
    "findings": [f"mock lane accepted {scenario_id}"],
    "artifacts": [],
}
Path(os.environ["HONUA_EVAL_LANE_OUTPUT"]).write_text(json.dumps(payload) + "\n", encoding="utf-8")
PY

echo "Validating enabled release-gate lane"
HONUA_EVAL_CODEX_ENABLED=true \
HONUA_EVAL_CODEX_COMMAND="python3 $WORKDIR/mock_lane.py" \
HONUA_DEVOPS_CODEX_MODEL="codex-smoke" \
python3 "$REPO_ROOT/scripts/run-multi-model-operator-evals.py" \
  --server-report "$SERVER_REPORT" \
  --scenario-dir "$SCENARIO_DIR" \
  --output-dir "$WORKDIR/codex" \
  --run-lanes \
  --hard-fail

python3 - "$WORKDIR/codex/report.json" <<'PY'
import json
import sys
from pathlib import Path

report = json.loads(Path(sys.argv[1]).read_text(encoding="utf-8"))
codex = next(lane for lane in report["lanes"] if lane["id"] == "codex")
assert codex["status"] == "passed"
assert len(codex["scenarioResults"]) == 3
assert report["rollup"]["releaseGateStatus"] == "passed-with-skips"
PY

echo "Validating passed-with-skips release-gate lane"
HONUA_EVAL_CODEX_ENABLED=true \
HONUA_EVAL_CODEX_COMMAND="python3 $WORKDIR/mock_lane.py" \
HONUA_EVAL_MOCK_SKIP=true \
python3 "$REPO_ROOT/scripts/run-multi-model-operator-evals.py" \
  --server-report "$SERVER_REPORT" \
  --scenario-dir "$SCENARIO_DIR" \
  --output-dir "$WORKDIR/codex-with-skips" \
  --run-lanes \
  --hard-fail

python3 - "$WORKDIR/codex-with-skips/report.json" <<'PY'
import json
import sys
from pathlib import Path

report = json.loads(Path(sys.argv[1]).read_text(encoding="utf-8"))
codex = next(lane for lane in report["lanes"] if lane["id"] == "codex")
assert codex["status"] == "passed-with-skips"
assert report["rollup"]["releaseGateStatus"] == "passed-with-skips"
PY

cat >"$WORKDIR/mock_false_pass.py" <<'PY'
import json
import os
import sys
from pathlib import Path

Path(os.environ["HONUA_EVAL_LANE_OUTPUT"]).write_text(json.dumps({"status": "passed"}) + "\n", encoding="utf-8")
sys.exit(7)
PY

echo "Validating hard-fail behavior for enabled release-gate lane"
set +e
HONUA_EVAL_CODEX_ENABLED=true \
HONUA_EVAL_CODEX_COMMAND="bash -c 'exit 7'" \
python3 "$REPO_ROOT/scripts/run-multi-model-operator-evals.py" \
  --server-report "$SERVER_REPORT" \
  --scenario-dir "$SCENARIO_DIR" \
  --output-dir "$WORKDIR/failing" \
  --run-lanes \
  --hard-fail
exit_code=$?
set -e

if [[ "$exit_code" -ne 2 ]]; then
  echo "[ERROR] Expected hard-fail exit code 2, got ${exit_code}." >&2
  exit 1
fi

echo "Validating non-zero lane exit overrides passing payload"
set +e
HONUA_EVAL_CODEX_ENABLED=true \
HONUA_EVAL_CODEX_COMMAND="python3 $WORKDIR/mock_false_pass.py" \
python3 "$REPO_ROOT/scripts/run-multi-model-operator-evals.py" \
  --server-report "$SERVER_REPORT" \
  --scenario-dir "$SCENARIO_DIR" \
  --output-dir "$WORKDIR/false-pass" \
  --run-lanes \
  --hard-fail
exit_code=$?
set -e

if [[ "$exit_code" -ne 2 ]]; then
  echo "[ERROR] Expected hard-fail exit code 2 for non-zero lane command, got ${exit_code}." >&2
  exit 1
fi

echo "Validating exit-0 with no result file is NOT credited as passed"
set +e
HONUA_EVAL_CODEX_ENABLED=true \
HONUA_EVAL_CODEX_COMMAND="bash -c 'exit 0'" \
python3 "$REPO_ROOT/scripts/run-multi-model-operator-evals.py" \
  --server-report "$SERVER_REPORT" \
  --scenario-dir "$SCENARIO_DIR" \
  --output-dir "$WORKDIR/empty-result" \
  --run-lanes \
  --hard-fail
exit_code=$?
set -e

if [[ "$exit_code" -ne 2 ]]; then
  echo "[ERROR] Expected hard-fail exit code 2 when a release-gate lane exits 0 without a result file, got ${exit_code}." >&2
  exit 1
fi

echo "Validating per-lane timeout kills the whole process tree (no orphaned grandchildren)"
# The lane command shells out and that shell backgrounds a long-lived grandchild — exactly the
# shape of a real model-CLI invocation. The grandchild records its PID and sleeps far longer
# than the lane budget. After the (timed-out) run returns, the grandchild must be dead: if only
# the /bin/sh wrapper were killed, the reparented grandchild would survive and keep burning the
# budget the harness claims to bound.
GRANDCHILD_PID_FILE="$WORKDIR/grandchild.pid"
cat >"$WORKDIR/hang_lane.sh" <<HANG
#!/usr/bin/env bash
# Background a grandchild that outlives the lane budget, then block on it.
( echo \$\$ >"$GRANDCHILD_PID_FILE"; exec sleep 600 ) &
child=\$!
wait "\$child"
HANG
chmod +x "$WORKDIR/hang_lane.sh"

set +e
HONUA_EVAL_CODEX_ENABLED=true \
HONUA_EVAL_CODEX_COMMAND="bash $WORKDIR/hang_lane.sh" \
HONUA_EVAL_LANE_TIMEOUT_SECONDS=2 \
python3 "$REPO_ROOT/scripts/run-multi-model-operator-evals.py" \
  --server-report "$SERVER_REPORT" \
  --scenario-dir "$SCENARIO_DIR" \
  --output-dir "$WORKDIR/timeout" \
  --run-lanes \
  --hard-fail
exit_code=$?
set -e

if [[ "$exit_code" -ne 2 ]]; then
  echo "[ERROR] Expected hard-fail exit code 2 for a timed-out release-gate lane, got ${exit_code}." >&2
  exit 1
fi

python3 - "$WORKDIR/timeout/report.json" <<'PY'
import json
import sys
from pathlib import Path

report = json.loads(Path(sys.argv[1]).read_text(encoding="utf-8"))
codex = next(lane for lane in report["lanes"] if lane["id"] == "codex")
assert codex["status"] == "failed", codex["status"]
assert any(result.get("timedOut") for result in codex["scenarioResults"]), "expected a timedOut scenario result"
PY

if [[ ! -f "$GRANDCHILD_PID_FILE" ]]; then
  echo "[ERROR] grandchild never recorded its PID; smoke setup is broken." >&2
  exit 1
fi
grandchild_pid="$(cat "$GRANDCHILD_PID_FILE")"
# Give the OS a brief moment to finish reaping the killed group.
for _ in 1 2 3 4 5 6 7 8 9 10; do
  if ! kill -0 "$grandchild_pid" 2>/dev/null; then
    break
  fi
  sleep 0.2
done
if kill -0 "$grandchild_pid" 2>/dev/null; then
  echo "[ERROR] grandchild PID ${grandchild_pid} survived the lane timeout — process tree was NOT killed." >&2
  kill -9 "$grandchild_pid" 2>/dev/null || true
  exit 1
fi

echo "Multi-model operator eval smoke check passed."
