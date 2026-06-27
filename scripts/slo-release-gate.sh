#!/usr/bin/env bash

set -euo pipefail

AVAILABILITY_WINDOW_DAYS="${SLO_AVAILABILITY_WINDOW_DAYS:-30}"
AVAILABILITY_MIN="${SLO_AVAILABILITY_MIN:-99.0}"
ERROR_RATE_MAX="${SLO_ERROR_RATE_MAX:-1.0}"
P95_MS_MAX="${SLO_P95_MS_MAX:-1200}"
BURN_RATE_5M_MAX="${SLO_BURN_RATE_5M_MAX:-14.4}"
BURN_RATE_30M_MAX="${SLO_BURN_RATE_30M_MAX:-6.0}"
BURN_RATE_6H_MAX="${SLO_BURN_RATE_6H_MAX:-3.0}"
ENABLE_BURN_RATE_CHECKS="${SLO_ENABLE_BURN_RATE_CHECKS:-auto}"
# GeoServices/ArcGIS REST 200+{error} budget (audit S1, #113). These errors are
# returned with an HTTP 2xx status and are invisible to the 5xx-derived error
# rate / burn rate, so the gate would otherwise green-light a release that is
# actively failing in-band.
GEOSERVICES_ERROR_RATE_MAX="${SLO_GEOSERVICES_ERROR_RATE_MAX:-1.0}"
ENABLE_GEOSERVICES_CHECK="${SLO_ENABLE_GEOSERVICES_CHECK:-auto}"
# When the in-band signal is absent, fail closed only if explicitly required;
# otherwise warn loudly so existing pipelines are not broken before they emit it.
REQUIRE_GEOSERVICES_CHECK="${SLO_REQUIRE_GEOSERVICES_CHECK:-false}"
GATE_MODE="${SLO_GATE_MODE:-pre-deploy}"

AVAILABILITY="${SLO_AVAILABILITY_PERCENT:-}"
ERROR_RATE="${SLO_ERROR_RATE_PERCENT:-}"
P95_MS="${SLO_P95_MS:-}"
BURN_RATE_5M="${SLO_BURN_RATE_5M:-}"
BURN_RATE_30M="${SLO_BURN_RATE_30M:-}"
BURN_RATE_6H="${SLO_BURN_RATE_6H:-}"
GEOSERVICES_ERROR_RATE="${SLO_GEOSERVICES_ERROR_RATE_PERCENT:-}"
JSON_URL="${HONUA_SLO_JSON_URL:-}"
MAINTENANCE_ACTIVE="${HONUA_SLO_MAINTENANCE_ACTIVE:-false}"
MAINTENANCE_FILE="${HONUA_SLO_MAINTENANCE_FILE:-}"
SUPPRESS_DURING_MAINTENANCE="${HONUA_SLO_SUPPRESS_ALERTS_DURING_MAINTENANCE:-true}"
ENFORCE_DURING_MAINTENANCE="${HONUA_SLO_ENFORCE_DURING_MAINTENANCE:-false}"
ALERT_RUNBOOK_URL="${SLO_ALERT_RUNBOOK_URL:-}"
ALERT_ROUTE_PAGERDUTY="${SLO_ALERT_ROUTE_PAGERDUTY:-}"
ALERT_ROUTE_SLACK="${SLO_ALERT_ROUTE_SLACK:-}"
ALERT_ROUTE_EMAIL="${SLO_ALERT_ROUTE_EMAIL:-}"
tmp=""

require_command() {
  if ! command -v "$1" >/dev/null 2>&1; then
    echo "[ERROR] Required command missing: $1" >&2
    exit 1
  fi
}

extract_value() {
  local file="$1"
  local expr="$2"
  jq -r "$expr // empty" "$file"
}

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

float_le() {
  awk "BEGIN {exit !($1 <= $2)}"
}

float_ge() {
  awk "BEGIN {exit !($1 >= $2)}"
}

clamped_ratio() {
  awk -v numerator="$1" -v denominator="$2" '
    BEGIN {
      if (denominator <= 0) {
        print "1.0000";
        exit 0;
      }

      ratio = numerator / denominator;
      if (ratio < 0) {
        ratio = 0;
      }

      if (ratio > 1) {
        ratio = 1;
      }

      printf "%.4f", ratio;
    }'
}

minimum_value() {
  awk -v first="$1" -v second="$2" '
    BEGIN {
      if (first < second) {
        printf "%.4f", first;
      } else {
        printf "%.4f", second;
      }
    }'
}

print_alert_routes() {
  if [[ -n "$ALERT_ROUTE_PAGERDUTY" || -n "$ALERT_ROUTE_SLACK" || -n "$ALERT_ROUTE_EMAIL" ]]; then
    echo "[INFO] alert_routes pagerduty=${ALERT_ROUTE_PAGERDUTY:-unset} slack=${ALERT_ROUTE_SLACK:-unset} email=${ALERT_ROUTE_EMAIL:-unset}"
  fi

  if [[ -n "$ALERT_RUNBOOK_URL" ]]; then
    echo "[INFO] runbook=${ALERT_RUNBOOK_URL}"
  fi
}

trap '[[ -n "$tmp" ]] && rm -f "$tmp"' EXIT

if [[ -n "$MAINTENANCE_FILE" && -f "$MAINTENANCE_FILE" ]]; then
  MAINTENANCE_ACTIVE=true
fi

if [[ -n "$JSON_URL" ]]; then
  require_command curl
  require_command jq
  tmp="$(mktemp)"
  curl --silent --show-error --fail "$JSON_URL" > "$tmp"

  AVAILABILITY="${AVAILABILITY:-$(extract_value "$tmp" '.availability_percent // .availability // .slo.availability')}"
  ERROR_RATE="${ERROR_RATE:-$(extract_value "$tmp" '.error_rate_percent // .error_rate // .slo.error_rate')}"
  P95_MS="${P95_MS:-$(extract_value "$tmp" '.p95_latency_ms // .latency_p95_ms // .slo.p95_latency_ms')}"
  BURN_RATE_5M="${BURN_RATE_5M:-$(extract_value "$tmp" '.burn_rate_5m // .burn_rates["5m"] // .slo.burn_rate_5m')}"
  BURN_RATE_30M="${BURN_RATE_30M:-$(extract_value "$tmp" '.burn_rate_30m // .burn_rates["30m"] // .slo.burn_rate_30m')}"
  BURN_RATE_6H="${BURN_RATE_6H:-$(extract_value "$tmp" '.burn_rate_6h // .burn_rates["6h"] // .slo.burn_rate_6h')}"
  GEOSERVICES_ERROR_RATE="${GEOSERVICES_ERROR_RATE:-$(extract_value "$tmp" '.geoservices_error_rate_percent // .geoservices_error_rate // .slo.geoservices_error_rate // .inband_error_rate_percent // .inband_error_rate')}"
fi

if [[ "$ENABLE_BURN_RATE_CHECKS" == "auto" ]]; then
  if [[ -n "$BURN_RATE_5M$BURN_RATE_30M$BURN_RATE_6H" ]]; then
    ENABLE_BURN_RATE_CHECKS=true
  else
    ENABLE_BURN_RATE_CHECKS=false
  fi
fi

if [[ "$ENABLE_GEOSERVICES_CHECK" == "auto" ]]; then
  if [[ -n "$GEOSERVICES_ERROR_RATE" ]]; then
    ENABLE_GEOSERVICES_CHECK=true
  else
    ENABLE_GEOSERVICES_CHECK=false
  fi
fi

for key in AVAILABILITY ERROR_RATE P95_MS; do
  if [[ -z "${!key:-}" ]]; then
    echo "[ERROR] Missing metric value: $key" >&2
    echo "Provide SLO_* env vars or HONUA_SLO_JSON_URL." >&2
    exit 1
  fi
done

if [[ "$ENABLE_BURN_RATE_CHECKS" == "true" ]]; then
  for key in BURN_RATE_5M BURN_RATE_30M BURN_RATE_6H; do
    if [[ -z "${!key:-}" ]]; then
      echo "[ERROR] Missing burn-rate metric value: $key" >&2
      echo "Provide SLO_BURN_RATE_* env vars or include burn-rate keys in HONUA_SLO_JSON_URL." >&2
      exit 1
    fi
  done
fi

failures=0

if float_ge "$AVAILABILITY" "$AVAILABILITY_MIN"; then
  echo "[PASS] availability ${AVAILABILITY}% >= ${AVAILABILITY_MIN}%"
else
  echo "[FAIL] availability ${AVAILABILITY}% < ${AVAILABILITY_MIN}%"
  failures=$((failures + 1))
fi

if float_le "$ERROR_RATE" "$ERROR_RATE_MAX"; then
  echo "[PASS] error_rate ${ERROR_RATE}% <= ${ERROR_RATE_MAX}%"
else
  echo "[FAIL] error_rate ${ERROR_RATE}% > ${ERROR_RATE_MAX}%"
  failures=$((failures + 1))
fi

if float_le "$P95_MS" "$P95_MS_MAX"; then
  echo "[PASS] p95_latency ${P95_MS}ms <= ${P95_MS_MAX}ms"
else
  echo "[FAIL] p95_latency ${P95_MS}ms > ${P95_MS_MAX}ms"
  failures=$((failures + 1))
fi

if [[ "$ENABLE_BURN_RATE_CHECKS" == "true" ]]; then
  if float_le "$BURN_RATE_5M" "$BURN_RATE_5M_MAX"; then
    echo "[PASS] burn_rate_5m ${BURN_RATE_5M} <= ${BURN_RATE_5M_MAX}"
  else
    echo "[FAIL] burn_rate_5m ${BURN_RATE_5M} > ${BURN_RATE_5M_MAX}"
    failures=$((failures + 1))
  fi

  if float_le "$BURN_RATE_30M" "$BURN_RATE_30M_MAX"; then
    echo "[PASS] burn_rate_30m ${BURN_RATE_30M} <= ${BURN_RATE_30M_MAX}"
  else
    echo "[FAIL] burn_rate_30m ${BURN_RATE_30M} > ${BURN_RATE_30M_MAX}"
    failures=$((failures + 1))
  fi

  if float_le "$BURN_RATE_6H" "$BURN_RATE_6H_MAX"; then
    echo "[PASS] burn_rate_6h ${BURN_RATE_6H} <= ${BURN_RATE_6H_MAX}"
  else
    echo "[FAIL] burn_rate_6h ${BURN_RATE_6H} > ${BURN_RATE_6H_MAX}"
    failures=$((failures + 1))
  fi
else
  echo "[INFO] burn-rate checks disabled; provide SLO_BURN_RATE_* or burn_rates JSON keys to enable them."
fi

if [[ "$ENABLE_GEOSERVICES_CHECK" == "true" ]]; then
  if float_le "$GEOSERVICES_ERROR_RATE" "$GEOSERVICES_ERROR_RATE_MAX"; then
    echo "[PASS] geoservices_inband_error_rate ${GEOSERVICES_ERROR_RATE}% <= ${GEOSERVICES_ERROR_RATE_MAX}%"
  else
    echo "[FAIL] geoservices_inband_error_rate ${GEOSERVICES_ERROR_RATE}% > ${GEOSERVICES_ERROR_RATE_MAX}% (200+{error} envelopes invisible to 5xx metrics)"
    failures=$((failures + 1))
  fi
elif is_truthy "$REQUIRE_GEOSERVICES_CHECK"; then
  echo "[FAIL] GeoServices in-band error rate required but not provided. Set SLO_GEOSERVICES_ERROR_RATE_PERCENT or include geoservices_error_rate in HONUA_SLO_JSON_URL."
  failures=$((failures + 1))
else
  echo "[WARN] GeoServices in-band error rate not provided; the gate is blind to 200+{error} failures. Provide SLO_GEOSERVICES_ERROR_RATE_PERCENT (or geoservices_error_rate JSON key), or set SLO_REQUIRE_GEOSERVICES_CHECK=true to fail closed."
fi

availability_budget_remaining_ratio="$(awk -v actual="$AVAILABILITY" -v target="$AVAILABILITY_MIN" '
  BEGIN {
    budget = 100 - target;
    spent = 100 - actual;
    if (budget <= 0) {
      print "1.0000";
      exit 0;
    }

    remaining = 1 - (spent / budget);
    if (remaining < 0) {
      remaining = 0;
    }

    if (remaining > 1) {
      remaining = 1;
    }

    printf "%.4f", remaining;
  }')"
error_budget_remaining_ratio="$(awk -v actual="$ERROR_RATE" -v threshold="$ERROR_RATE_MAX" '
  BEGIN {
    if (threshold <= 0) {
      print "1.0000";
      exit 0;
    }

    remaining = 1 - (actual / threshold);
    if (remaining < 0) {
      remaining = 0;
    }

    if (remaining > 1) {
      remaining = 1;
    }

    printf "%.4f", remaining;
  }')"
latency_headroom_ratio="$(awk -v actual="$P95_MS" -v threshold="$P95_MS_MAX" '
  BEGIN {
    if (threshold <= 0) {
      print "1.0000";
      exit 0;
    }

    remaining = 1 - (actual / threshold);
    if (remaining < 0) {
      remaining = 0;
    }

    if (remaining > 1) {
      remaining = 1;
    }

    printf "%.4f", remaining;
  }')"
overall_error_budget_remaining_ratio="$(minimum_value "$availability_budget_remaining_ratio" "$error_budget_remaining_ratio")"

echo "[INFO] gate_mode=${GATE_MODE} window=${AVAILABILITY_WINDOW_DAYS}d maintenance_active=${MAINTENANCE_ACTIVE}"
echo "[INFO] availability_error_budget_remaining_ratio=${availability_budget_remaining_ratio}"
echo "[INFO] error_rate_budget_remaining_ratio=${error_budget_remaining_ratio}"
echo "[INFO] latency_headroom_ratio=${latency_headroom_ratio}"
echo "[INFO] overall_error_budget_remaining_ratio=${overall_error_budget_remaining_ratio}"
if [[ "$ENABLE_GEOSERVICES_CHECK" == "true" ]]; then
  geoservices_budget_remaining_ratio="$(awk -v actual="$GEOSERVICES_ERROR_RATE" -v threshold="$GEOSERVICES_ERROR_RATE_MAX" '
    BEGIN {
      if (threshold <= 0) { print "1.0000"; exit 0; }
      remaining = 1 - (actual / threshold);
      if (remaining < 0) { remaining = 0; }
      if (remaining > 1) { remaining = 1; }
      printf "%.4f", remaining;
    }')"
  echo "[INFO] geoservices_inband_error_budget_remaining_ratio=${geoservices_budget_remaining_ratio}"
fi

if [[ "$failures" -gt 0 ]]; then
  print_alert_routes

  if is_truthy "$MAINTENANCE_ACTIVE" &&
    is_truthy "$SUPPRESS_DURING_MAINTENANCE" &&
    ! is_truthy "$ENFORCE_DURING_MAINTENANCE"; then
    echo "[SUPPRESSED] SLO gate violations suppressed during maintenance window."
    exit 0
  fi

  echo "[RESULT] SLO gate failed ($failures checks)."
  exit 1
fi

echo "[RESULT] SLO gate passed."
