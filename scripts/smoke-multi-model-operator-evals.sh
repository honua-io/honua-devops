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

echo "Multi-model operator eval smoke check passed."
