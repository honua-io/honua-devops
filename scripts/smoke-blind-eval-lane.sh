#!/usr/bin/env bash
#
# Fixture floor for the blind fault-injection evaluation lane (honua-devops#155).
#
# Runs the same `--eval-blind` CLI mode the credentialed lane runs, against local
# fixture adapters, and asserts BOTH directions:
#
#   - the golden fixture produces a PASSING scorecard and exit 0
#   - the known-bad fixture produces a FAILING scorecard and exit 1 (test-of-the-test)
#
# A lane that can only report green proves nothing, so the known-bad assertion is the
# load-bearing half of this script.

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

CONFIGURATION="${HONUA_DEVOPS_BUILD_CONFIGURATION:-Release}"
WORKDIR="$(mktemp -d)"
trap 'rm -rf "$WORKDIR"' EXIT

COMMIT_SHA="$(git -C "$REPO_ROOT" rev-parse HEAD 2>/dev/null || echo "unknown")"

run_lane() {
  local fixture="$1"
  local output="$2"
  local expected_exit="$3"
  local expected_result="$4"
  local exit_code=0

  set +e
  dotnet run --project "$REPO_ROOT/src/Honua.DevOps.Agent" \
    --configuration "$CONFIGURATION" \
    -- \
    --eval-blind \
    --eval-fixture "$fixture" \
    --eval-output "$output" \
    --eval-commit "$COMMIT_SHA"
  exit_code=$?
  set -e

  if [[ "$exit_code" -ne "$expected_exit" ]]; then
    echo "[ERROR] $(basename "$fixture"): expected exit ${expected_exit}, got ${exit_code}." >&2
    exit 1
  fi

  python3 "$REPO_ROOT/scripts/verify-blind-eval-scorecard.py" "$output" \
    --expect-lane fixture \
    --expect-commit "$COMMIT_SHA" \
    --expect-result "$expected_result"
}

echo "Validating the golden fixture adapter scores as PASS"
run_lane "$REPO_ROOT/eval/fixtures/blind-eval/golden-answers.json" "$WORKDIR/golden.json" 0 pass

echo "Validating the known-bad fixture adapter scores as FAIL (test-of-the-test)"
run_lane "$REPO_ROOT/eval/fixtures/blind-eval/known-bad-answers.json" "$WORKDIR/known-bad.json" 1 fail

echo "Validating the known-bad scorecard records real failure modes"
python3 - "$WORKDIR/known-bad.json" <<'PY'
import json
import sys
from pathlib import Path

scorecard = json.loads(Path(sys.argv[1]).read_text(encoding="utf-8"))

assert scorecard["aggregate"]["scenariosPassed"] == 0, scorecard["aggregate"]
assert scorecard["aggregate"]["result"] == "fail", scorecard["aggregate"]

for scenario in scorecard["scenarios"]:
    assert scenario["result"] == "fail", scenario
    assert scenario["diagnosisCorrect"] is False, scenario
    assert "wrong-root-cause" in scenario["failureModes"], scenario
PY

echo "Validating an unknown fault-set selector fails the lane instead of reporting empty-green"
set +e
dotnet run --project "$REPO_ROOT/src/Honua.DevOps.Agent" \
  --configuration "$CONFIGURATION" \
  -- \
  --eval-blind \
  --eval-fixture "$REPO_ROOT/eval/fixtures/blind-eval/golden-answers.json" \
  --eval-fault-set "FAULT-000-does-not-exist" \
  --eval-output "$WORKDIR/unknown.json" >/dev/null 2>&1
exit_code=$?
set -e

if [[ "$exit_code" -eq 0 ]]; then
  echo "[ERROR] An unknown fault set exited 0; the lane would report green having run nothing." >&2
  exit 1
fi

if [[ -f "$WORKDIR/unknown.json" ]]; then
  echo "[ERROR] An unknown fault set wrote a scorecard; no artifact should be published." >&2
  exit 1
fi

echo "Blind-eval lane smoke check passed."
