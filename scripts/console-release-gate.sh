#!/usr/bin/env bash

# Honua Console release-promotion gate.
#
# Evaluates the CI evidence produced for a honua-console build (install, lint,
# typecheck, unit tests, browser smoke, production build) plus the unified-runtime
# surface-parity status (Console, Studio, Catalog, Share, Operate) and decides
# whether the single deployable Console artifact may be promoted.
#
# Default-safe posture (per AGENTS.md): this gate only *plans/evaluates*. It emits
# a release-notes block and a machine-readable verdict; it never deploys, submits,
# or rolls back. A failing browser smoke (or any failing required CI stage, or a
# regressed/unknown surface) blocks promotion with a non-zero exit code.
#
# Inputs may be supplied either via an evidence JSON file/URL or directly via env
# vars. Env vars take precedence over JSON fields, matching scripts/slo-release-gate.sh.

set -euo pipefail

# --- Configuration -----------------------------------------------------------

# Required CI stages for honua-console. Each must report "pass" to promote.
REQUIRED_STAGES_DEFAULT="install lint typecheck unit browser_smoke build"
REQUIRED_STAGES="${CONSOLE_REQUIRED_STAGES:-$REQUIRED_STAGES_DEFAULT}"

# Unified-runtime surfaces that must reach parity before the single artifact ships.
SURFACES_DEFAULT="console studio catalog share operate"
SURFACES="${CONSOLE_SURFACES:-$SURFACES_DEFAULT}"

# Acceptable per-surface parity states. Anything else is treated as blocking.
# - ready   : surface is at parity in the unified runtime; safe to promote.
# - preview : surface ships behind a preview flag; allowed unless strict mode.
ALLOWED_SURFACE_STATES="ready preview"

# Strict mode also blocks on any surface that is only in "preview" parity.
STRICT_SURFACE_PARITY="${CONSOLE_STRICT_SURFACE_PARITY:-false}"

GATE_MODE="${CONSOLE_GATE_MODE:-promote}"
ARTIFACT_VERSION="${CONSOLE_ARTIFACT_VERSION:-}"
ARTIFACT_KIND="${CONSOLE_ARTIFACT_KIND:-unified-runtime-image}"
ENVIRONMENT="${CONSOLE_ENVIRONMENT:-staging}"

# Whether the legacy Portal/Admin deployment paths are still required. Reported in
# the release notes so promotion reviewers know if the old paths can be retired.
# Left empty here so an unset env var can fall back to evidence JSON; defaults to
# "unknown" only after both env and JSON have been consulted.
LEGACY_PORTAL_REQUIRED="${CONSOLE_LEGACY_PORTAL_REQUIRED:-}"
LEGACY_ADMIN_REQUIRED="${CONSOLE_LEGACY_ADMIN_REQUIRED:-}"

EVIDENCE_FILE="${CONSOLE_EVIDENCE_FILE:-${1:-}}"
EVIDENCE_URL="${CONSOLE_EVIDENCE_URL:-}"

NOTES_OUTPUT="${CONSOLE_RELEASE_NOTES_OUTPUT:-}"

tmp=""
trap '[[ -n "$tmp" ]] && rm -f "$tmp"' EXIT

# --- Helpers -----------------------------------------------------------------

require_command() {
  if ! command -v "$1" >/dev/null 2>&1; then
    echo "[ERROR] Required command missing: $1" >&2
    exit 1
  fi
}

is_truthy() {
  case "${1,,}" in
    1 | true | yes | on) return 0 ;;
    *) return 1 ;;
  esac
}

normalize() {
  # lowercase + trim
  local value="${1:-}"
  value="${value,,}"
  value="${value#"${value%%[![:space:]]*}"}"
  value="${value%"${value##*[![:space:]]}"}"
  printf '%s' "$value"
}

json_field() {
  # json_field <jq-path> <file> -> value or empty.
  # Uses an explicit null check (not `// empty`) so that boolean `false` and the
  # string "0" are preserved rather than swallowed by jq's alternative operator.
  jq -r "$1 | if . == null then empty else . end" "$2" 2>/dev/null || true
}

contains_word() {
  # contains_word <needle> <space-separated-haystack>
  local needle="$1"
  shift
  local word
  for word in $1; do
    [[ "$word" == "$needle" ]] && return 0
  done
  return 1
}

# --- Load evidence -----------------------------------------------------------

if [[ -n "$EVIDENCE_URL" ]]; then
  require_command curl
  require_command jq
  tmp="$(mktemp)"
  curl --silent --show-error --fail "$EVIDENCE_URL" >"$tmp"
  EVIDENCE_FILE="$tmp"
fi

if [[ -n "$EVIDENCE_FILE" ]]; then
  require_command jq
  if [[ ! -f "$EVIDENCE_FILE" ]]; then
    echo "[ERROR] Evidence file not found: $EVIDENCE_FILE" >&2
    exit 1
  fi
  if ! jq -e . "$EVIDENCE_FILE" >/dev/null 2>&1; then
    echo "[ERROR] Evidence file is not valid JSON: $EVIDENCE_FILE" >&2
    exit 1
  fi

  ARTIFACT_VERSION="${CONSOLE_ARTIFACT_VERSION:-$(json_field '.artifact.version // .version' "$EVIDENCE_FILE")}"
  if [[ -z "${CONSOLE_ARTIFACT_KIND:-}" ]]; then
    kind_from_json="$(json_field '.artifact.kind' "$EVIDENCE_FILE")"
    [[ -n "$kind_from_json" ]] && ARTIFACT_KIND="$kind_from_json"
  fi
  if [[ -z "${CONSOLE_ENVIRONMENT:-}" ]]; then
    env_from_json="$(json_field '.environment' "$EVIDENCE_FILE")"
    [[ -n "$env_from_json" ]] && ENVIRONMENT="$env_from_json"
  fi
  if [[ -z "$LEGACY_PORTAL_REQUIRED" ]]; then
    LEGACY_PORTAL_REQUIRED="$(json_field '.legacy.portal_required' "$EVIDENCE_FILE")"
  fi
  if [[ -z "$LEGACY_ADMIN_REQUIRED" ]]; then
    LEGACY_ADMIN_REQUIRED="$(json_field '.legacy.admin_required' "$EVIDENCE_FILE")"
  fi
fi

# Default to "unknown" only after both env and evidence JSON were consulted.
LEGACY_PORTAL_REQUIRED="${LEGACY_PORTAL_REQUIRED:-unknown}"
LEGACY_ADMIN_REQUIRED="${LEGACY_ADMIN_REQUIRED:-unknown}"

stage_status() {
  # stage_status <stage> -> pass|fail|missing
  local stage="$1"
  local env_var="CONSOLE_STAGE_${stage^^}"
  env_var="${env_var//[^A-Z0-9_]/_}"
  local value="${!env_var:-}"
  if [[ -z "$value" && -n "$EVIDENCE_FILE" ]]; then
    value="$(json_field ".stages.\"$stage\"" "$EVIDENCE_FILE")"
  fi
  value="$(normalize "$value")"
  case "$value" in
    pass | passed | success | ok | green | true) printf 'pass' ;;
    "") printf 'missing' ;;
    *) printf 'fail' ;;
  esac
}

surface_status() {
  # surface_status <surface> -> normalized state or "missing"
  local surface="$1"
  local env_var="CONSOLE_SURFACE_${surface^^}"
  env_var="${env_var//[^A-Z0-9_]/_}"
  local value="${!env_var:-}"
  if [[ -z "$value" && -n "$EVIDENCE_FILE" ]]; then
    value="$(json_field ".surfaces.\"$surface\"" "$EVIDENCE_FILE")"
  fi
  value="$(normalize "$value")"
  [[ -z "$value" ]] && value="missing"
  printf '%s' "$value"
}

# --- Evaluate ----------------------------------------------------------------

failures=0

echo "[INFO] gate_mode=${GATE_MODE} environment=${ENVIRONMENT} artifact_kind=${ARTIFACT_KIND} artifact_version=${ARTIFACT_VERSION:-unset}"
echo "[INFO] required_stages=${REQUIRED_STAGES}"
echo "[INFO] surfaces=${SURFACES} strict_surface_parity=${STRICT_SURFACE_PARITY}"

declare -A STAGE_RESULTS
for stage in $REQUIRED_STAGES; do
  result="$(stage_status "$stage")"
  STAGE_RESULTS["$stage"]="$result"
  case "$result" in
    pass)
      echo "[PASS] ci_stage ${stage}=pass"
      ;;
    missing)
      echo "[FAIL] ci_stage ${stage}=missing (no evidence supplied)"
      failures=$((failures + 1))
      ;;
    *)
      echo "[FAIL] ci_stage ${stage}=fail"
      failures=$((failures + 1))
      ;;
  esac
done

# Browser smoke is the explicit promotion blocker called out in the acceptance
# criteria; surface its verdict prominently.
smoke_result="${STAGE_RESULTS[browser_smoke]:-}"
if [[ -n "$smoke_result" && "$smoke_result" != "pass" ]]; then
  echo "[BLOCK] Console browser smoke did not pass (${smoke_result}); single artifact promotion is blocked."
fi

declare -A SURFACE_RESULTS
for surface in $SURFACES; do
  state="$(surface_status "$surface")"
  SURFACE_RESULTS["$surface"]="$state"
  if contains_word "$state" "$ALLOWED_SURFACE_STATES"; then
    if [[ "$state" == "preview" ]] && is_truthy "$STRICT_SURFACE_PARITY"; then
      echo "[FAIL] surface_parity ${surface}=preview (blocked by strict mode)"
      failures=$((failures + 1))
    else
      echo "[PASS] surface_parity ${surface}=${state}"
    fi
  else
    echo "[FAIL] surface_parity ${surface}=${state} (not in: ${ALLOWED_SURFACE_STATES})"
    failures=$((failures + 1))
  fi
done

# --- Release notes -----------------------------------------------------------

legacy_portal_norm="$(normalize "$LEGACY_PORTAL_REQUIRED")"
legacy_admin_norm="$(normalize "$LEGACY_ADMIN_REQUIRED")"

legacy_is_known() {
  case "$1" in
    true | yes | required | 1 | false | no | retired | 0) return 0 ;;
    *) return 1 ;;
  esac
}

render_legacy() {
  case "$1" in
    true | yes | required | 1) printf 'STILL REQUIRED' ;;
    false | no | retired | 0) printf 'retired (safe to remove)' ;;
    *) printf 'UNKNOWN (confirm before promotion)' ;;
  esac
}

notes="$(
  cat <<EOF
# Honua Console release notes

- environment: ${ENVIRONMENT}
- artifact: ${ARTIFACT_KIND} ${ARTIFACT_VERSION:-(version unset)}
- gate_mode: ${GATE_MODE}

## CI status
$(for stage in $REQUIRED_STAGES; do echo "- ${stage}: ${STAGE_RESULTS[$stage]}"; done)

## Unified-runtime surface parity
$(for surface in $SURFACES; do echo "- ${surface}: ${SURFACE_RESULTS[$surface]}"; done)

## Legacy deployment paths
- Portal: $(render_legacy "$legacy_portal_norm")
- Admin: $(render_legacy "$legacy_admin_norm")
EOF
)"

echo "----- release-notes -----"
echo "$notes"
echo "-------------------------"

if [[ -n "$NOTES_OUTPUT" ]]; then
  printf '%s\n' "$notes" >"$NOTES_OUTPUT"
  echo "[INFO] release notes written to ${NOTES_OUTPUT}"
fi

# Surface legacy-path uncertainty as a non-blocking warning so reviewers act on it.
if ! legacy_is_known "$legacy_portal_norm"; then
  echo "[WARN] legacy Portal deployment requirement is unknown; release notes flag it for confirmation."
fi
if ! legacy_is_known "$legacy_admin_norm"; then
  echo "[WARN] legacy Admin deployment requirement is unknown; release notes flag it for confirmation."
fi

# --- Verdict -----------------------------------------------------------------

if [[ "$failures" -gt 0 ]]; then
  echo "[RESULT] Console release gate FAILED (${failures} blocking checks); promotion of the single Console artifact is blocked."
  exit 1
fi

echo "[RESULT] Console release gate PASSED; single Console artifact is eligible for ${GATE_MODE} to ${ENVIRONMENT}."
