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
require_command rg

python3 -m json.tool "$REPO_ROOT/observability/grafana/honua-slo-dashboard.json" >/dev/null
rg -n "honua:slo:availability:error_budget_remaining_ratio|honua:slo:error_rate:error_budget_remaining_ratio|honua:slo:burn_rate:5m|honua:slo:burn_rate:30m|honua:slo:burn_rate:6h" \
  "$REPO_ROOT/observability/prometheus/honua-slo-recording-rules.yml" >/dev/null
rg -n "HonuaSloFastBurn|HonuaSloMediumBurn|HonuaSloSlowBurn|HonuaAvailabilitySloViolation|HonuaBackupDurabilityRisk" \
  "$REPO_ROOT/observability/prometheus/honua-slo-alert-rules.yml" >/dev/null
rg -n "pagerduty-critical|slack-warning|email-info|maintenance-mute" \
  "$REPO_ROOT/observability/alertmanager/honua-slo-routes.template.yml" >/dev/null

echo "SLO observability assets validation passed."
