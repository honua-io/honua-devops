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

python3 -m json.tool "$REPO_ROOT/observability/grafana/honua-slo-dashboard.json" >/dev/null
grep -nE -- "honua:slo:availability:error_budget_remaining_ratio|honua:slo:error_rate:error_budget_remaining_ratio|honua:slo:burn_rate:5m|honua:slo:burn_rate:30m|honua:slo:burn_rate:6h" \
  "$REPO_ROOT/observability/prometheus/honua-slo-recording-rules.yml" >/dev/null
# Audit S1 (#113): the in-band / GeoServices 200+{error} recording rules must exist.
grep -nE -- "honua:slo:geoservices_error_rate:ratio_5m|honua:slo:inband_error_rate:ratio_5m|honua:slo:effective_error_rate:ratio_5m|honua:slo:effective_burn_rate:5m" \
  "$REPO_ROOT/observability/prometheus/honua-slo-recording-rules.yml" >/dev/null
grep -nE -- "HonuaSloFastBurn|HonuaSloMediumBurn|HonuaSloSlowBurn|HonuaAvailabilitySloViolation|HonuaBackupDurabilityRisk" \
  "$REPO_ROOT/observability/prometheus/honua-slo-alert-rules.yml" >/dev/null
# Audit S1 (#113): alerts that fire on the in-band / GeoServices error signal.
grep -nE -- "HonuaGeoServicesInBandErrorRate|HonuaEffectiveSloFastBurn|HonuaEffectiveSloMediumBurn" \
  "$REPO_ROOT/observability/prometheus/honua-slo-alert-rules.yml" >/dev/null
grep -nE -- "pagerduty-critical|slack-warning|email-info|maintenance-mute" \
  "$REPO_ROOT/observability/alertmanager/honua-slo-routes.template.yml" >/dev/null

echo "SLO observability assets validation passed."
