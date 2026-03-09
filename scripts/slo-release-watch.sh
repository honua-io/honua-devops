#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

WATCH_INTERVAL_SECONDS="${SLO_WATCH_INTERVAL_SECONDS:-60}"
WATCH_MAX_SAMPLES="${SLO_WATCH_MAX_SAMPLES:-5}"
WATCH_CONSECUTIVE_FAILURES="${SLO_WATCH_CONSECUTIVE_FAILURES:-2}"
WATCH_AUTO_ROLLBACK="${SLO_WATCH_AUTO_ROLLBACK:-false}"
WATCH_ROLLBACK_COMMAND="${SLO_WATCH_ROLLBACK_COMMAND:-}"

is_truthy() {
  local value="${1:-}"
  case "${value,,}" in
    1|true|yes|on)
      return 0
      ;;
    *)
      return 1
      ;;
  esac
}

consecutive_failures=0
total_failures=0

for sample in $(seq 1 "$WATCH_MAX_SAMPLES"); do
  echo "[INFO] SLO watch sample ${sample}/${WATCH_MAX_SAMPLES}"

  if output="$(SLO_GATE_MODE="post-deploy-watch" "$REPO_ROOT/scripts/slo-release-gate.sh" 2>&1)"; then
    printf "%s\n" "$output"
    consecutive_failures=0
  else
    gate_exit=$?
    printf "%s\n" "$output"
    total_failures=$((total_failures + 1))
    consecutive_failures=$((consecutive_failures + 1))

    if [[ "$consecutive_failures" -ge "$WATCH_CONSECUTIVE_FAILURES" ]]; then
      echo "[FAIL] SLO watch exceeded failure threshold after ${sample} samples."

      if is_truthy "$WATCH_AUTO_ROLLBACK"; then
        if [[ -z "$WATCH_ROLLBACK_COMMAND" ]]; then
          echo "[ERROR] SLO watch auto-rollback requested but SLO_WATCH_ROLLBACK_COMMAND is empty." >&2
          exit 1
        fi

        echo "[ACTION] Executing rollback command: $WATCH_ROLLBACK_COMMAND"
        bash -lc "$WATCH_ROLLBACK_COMMAND"
        echo "[RESULT] SLO watch triggered rollback."
        exit 2
      fi

      echo "[RESULT] SLO watch failed without automatic rollback."
      exit "$gate_exit"
    fi
  fi

  if [[ "$sample" -lt "$WATCH_MAX_SAMPLES" ]]; then
    sleep "$WATCH_INTERVAL_SECONDS"
  fi
done

echo "[RESULT] SLO watch passed after ${WATCH_MAX_SAMPLES} samples with ${total_failures} failure(s)."
