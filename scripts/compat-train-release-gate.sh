#!/usr/bin/env bash

# Honua cross-repo compatibility-train release-candidate gate.
#
# A release candidate for Honua is validated as a *train*: the server plus the
# downstream client/SDK repos (honua-sdk-python, honua-sdk-js, mobile, QGIS, ...)
# that must all stay green against the same candidate before the train ships.
# This gate evaluates the per-repo release-candidate evidence and the published
# client-compatibility scoreboard, then decides whether the train is releasable.
#
# Default-safe posture (per AGENTS.md): this gate only *plans/evaluates*. It emits
# a release-notes block and a machine-readable verdict; it never deploys, submits,
# promotes, or rolls back. Any required repo that is not green -- or, in live mode,
# any repo whose evidence came from a seeded local fallback instead of the real
# external candidate target -- blocks the train with a non-zero exit code.
#
# This directly addresses the cross-repo gap reported in honua-sdk-python#53:
# a green staging run that used the seeded local fallback (HONUA_BASE_URL=
# http://localhost:5000, local_stack=true) is NOT acceptable live release-candidate
# evidence. In live mode this gate refuses to certify such runs and explains why,
# so the train cannot be declared validated on local-fallback evidence alone.
#
# Inputs may be supplied either via an evidence JSON file/URL or directly via env
# vars. Env vars take precedence over JSON fields, matching the other release
# gates in this repo (scripts/slo-release-gate.sh, scripts/console-release-gate.sh).

set -euo pipefail

# --- Configuration -----------------------------------------------------------

# Repos that make up the compatibility train. Each must report a green
# release-candidate run to certify the train. Space-separated; overridable so a
# partial train (e.g. a single-SDK hotfix) can be gated explicitly.
TRAIN_REPOS_DEFAULT="honua-server honua-sdk-python honua-sdk-js honua-mobile honua-qgis-plugin"
TRAIN_REPOS="${COMPAT_TRAIN_REPOS:-$TRAIN_REPOS_DEFAULT}"

# Validation mode:
# - live   : every repo's evidence MUST come from the real external candidate
#            target (local_stack=false). Seeded local-fallback runs are rejected.
#            This is the mode a real release-candidate certification must use.
# - any    : accept either live or seeded local-fallback evidence (CI/dev use).
TRAIN_MODE="${COMPAT_TRAIN_MODE:-live}"

# Whether a pending (not-yet-failing) client/protocol in the latest scoreboard
# release blocks the train. Strict mode treats pending as blocking.
STRICT_SCOREBOARD="${COMPAT_TRAIN_STRICT_SCOREBOARD:-false}"

GATE_MODE="${COMPAT_TRAIN_GATE_MODE:-certify}"
CANDIDATE_VERSION="${COMPAT_TRAIN_CANDIDATE_VERSION:-}"
ENVIRONMENT="${COMPAT_TRAIN_ENVIRONMENT:-staging}"

# Optional published client-compatibility scoreboard matrix. When supplied, the
# latest release in the matrix must have no failing (and, in strict mode, no
# pending) client/protocol statuses.
SCOREBOARD_MATRIX="${COMPAT_TRAIN_SCOREBOARD_MATRIX:-}"

EVIDENCE_FILE="${COMPAT_TRAIN_EVIDENCE_FILE:-${1:-}}"
EVIDENCE_URL="${COMPAT_TRAIN_EVIDENCE_URL:-}"

NOTES_OUTPUT="${COMPAT_TRAIN_RELEASE_NOTES_OUTPUT:-}"

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

# repo_key <repo> -> uppercase env-var-safe token (honua-sdk-python -> HONUA_SDK_PYTHON)
repo_key() {
  local key="${1^^}"
  printf '%s' "${key//[^A-Z0-9_]/_}"
}

json_field() {
  # json_field <jq-expr> <file> -> value or empty
  jq -r "$1 // empty" "$2" 2>/dev/null || true
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

  [[ -z "${COMPAT_TRAIN_CANDIDATE_VERSION:-}" ]] &&
    CANDIDATE_VERSION="$(json_field '.candidate.version // .version' "$EVIDENCE_FILE")"
  if [[ -z "${COMPAT_TRAIN_ENVIRONMENT:-}" ]]; then
    env_from_json="$(json_field '.environment' "$EVIDENCE_FILE")"
    [[ -n "$env_from_json" ]] && ENVIRONMENT="$env_from_json"
  fi
  if [[ -z "${COMPAT_TRAIN_REPOS:-}" ]]; then
    repos_from_json="$(json_field '.repos | keys[]' "$EVIDENCE_FILE" | tr '\n' ' ')"
    [[ -n "$(normalize "$repos_from_json")" ]] && TRAIN_REPOS="$repos_from_json"
  fi
  if [[ -z "${COMPAT_TRAIN_SCOREBOARD_MATRIX:-}" ]]; then
    sb_from_json="$(json_field '.scoreboard_matrix' "$EVIDENCE_FILE")"
    [[ -n "$sb_from_json" ]] && SCOREBOARD_MATRIX="$sb_from_json"
  fi
fi

# repo_evidence <repo> <field> -> value or empty
# Env override: COMPAT_TRAIN_REPO_<KEY>_<FIELD> (e.g. COMPAT_TRAIN_REPO_HONUA_SDK_PYTHON_STATUS).
repo_evidence() {
  local repo="$1" field="$2"
  local key
  key="$(repo_key "$repo")"
  local env_var="COMPAT_TRAIN_REPO_${key}_${field^^}"
  local value="${!env_var:-}"
  if [[ -z "$value" && -n "$EVIDENCE_FILE" ]]; then
    # Render the value as text; map a JSON null/absent field to empty. Note we
    # deliberately do NOT use `// empty` here -- that would swallow the boolean
    # false (local_stack=false), which is meaningful live-evidence signal.
    value="$(jq -r ".repos.\"$repo\".$field | if . == null then \"\" else (. | tostring) end" "$EVIDENCE_FILE" 2>/dev/null || true)"
  fi
  printf '%s' "$value"
}

# --- Evaluate per-repo release-candidate evidence ----------------------------

failures=0

echo "[INFO] gate_mode=${GATE_MODE} mode=${TRAIN_MODE} environment=${ENVIRONMENT} candidate_version=${CANDIDATE_VERSION:-unset}"
echo "[INFO] train_repos=${TRAIN_REPOS}"
echo "[INFO] strict_scoreboard=${STRICT_SCOREBOARD} scoreboard_matrix=${SCOREBOARD_MATRIX:-none}"

declare -A REPO_STATUS
declare -A REPO_LOCAL_STACK
declare -A REPO_TARGET
declare -A REPO_COMMIT

for repo in $TRAIN_REPOS; do
  status="$(normalize "$(repo_evidence "$repo" status)")"
  local_stack="$(normalize "$(repo_evidence "$repo" local_stack)")"
  # An absent local_stack signal is "unknown" -- in live mode that is not
  # certifiable evidence, so keep the distinction explicit.
  [[ -z "$local_stack" ]] && local_stack="unknown"
  target="$(repo_evidence "$repo" base_url)"
  commit="$(repo_evidence "$repo" commit)"
  REPO_STATUS["$repo"]="${status:-missing}"
  REPO_LOCAL_STACK["$repo"]="${local_stack}"
  REPO_TARGET["$repo"]="${target:-unset}"
  REPO_COMMIT["$repo"]="${commit:-unset}"

  # Normalize the run status to pass/fail/missing.
  case "$status" in
    pass | passed | success | ok | green | true) status_norm="pass" ;;
    "") status_norm="missing" ;;
    *) status_norm="fail" ;;
  esac

  if [[ "$status_norm" == "missing" ]]; then
    echo "[FAIL] repo ${repo}: no release-candidate evidence supplied"
    failures=$((failures + 1))
    continue
  fi

  if [[ "$status_norm" != "pass" ]]; then
    echo "[FAIL] repo ${repo}: release-candidate run status=${status}"
    failures=$((failures + 1))
    continue
  fi

  # Run is green. In live mode, reject seeded local-fallback evidence: that is
  # exactly the honua-sdk-python#53 situation (green run, but local_stack=true).
  if [[ "$TRAIN_MODE" == "live" ]]; then
    if is_truthy "$local_stack"; then
      echo "[FAIL] repo ${repo}: run is green but used the seeded local fallback (local_stack=true, target=${REPO_TARGET[$repo]}); not valid live release-candidate evidence"
      failures=$((failures + 1))
      continue
    fi
    if [[ "$local_stack" == "unknown" ]]; then
      echo "[FAIL] repo ${repo}: run is green but local_stack is unknown; cannot certify live evidence (provide local_stack=false + the external candidate target)"
      failures=$((failures + 1))
      continue
    fi
    echo "[PASS] repo ${repo}: green against live candidate target ${REPO_TARGET[$repo]} (commit ${REPO_COMMIT[$repo]})"
  else
    echo "[PASS] repo ${repo}: green (local_stack=${REPO_LOCAL_STACK[$repo]}, target=${REPO_TARGET[$repo]})"
  fi
done

# --- Evaluate the published client-compatibility scoreboard -------------------

SCOREBOARD_PASS=""
SCOREBOARD_PENDING=""
SCOREBOARD_FAIL=""
SCOREBOARD_RELEASE=""

if [[ -n "$SCOREBOARD_MATRIX" ]]; then
  require_command jq
  if [[ ! -f "$SCOREBOARD_MATRIX" ]]; then
    echo "[FAIL] scoreboard matrix not found: ${SCOREBOARD_MATRIX}"
    failures=$((failures + 1))
  elif ! jq -e . "$SCOREBOARD_MATRIX" >/dev/null 2>&1; then
    echo "[FAIL] scoreboard matrix is not valid JSON: ${SCOREBOARD_MATRIX}"
    failures=$((failures + 1))
  else
    # The generator orders releases newest-first; take the first as the latest.
    SCOREBOARD_RELEASE="$(json_field '.releases[0].release' "$SCOREBOARD_MATRIX")"
    SCOREBOARD_PASS="$(json_field '.releases[0].summary.pass' "$SCOREBOARD_MATRIX")"
    SCOREBOARD_PENDING="$(json_field '.releases[0].summary.pending' "$SCOREBOARD_MATRIX")"
    SCOREBOARD_WARN="$(json_field '.releases[0].summary.warn' "$SCOREBOARD_MATRIX")"
    SCOREBOARD_FAIL="$(json_field '.releases[0].summary.fail' "$SCOREBOARD_MATRIX")"
    SCOREBOARD_PASS="${SCOREBOARD_PASS:-0}"
    SCOREBOARD_PENDING="${SCOREBOARD_PENDING:-0}"
    SCOREBOARD_WARN="${SCOREBOARD_WARN:-0}"
    SCOREBOARD_FAIL="${SCOREBOARD_FAIL:-0}"

    if [[ "$SCOREBOARD_FAIL" -gt 0 ]]; then
      echo "[FAIL] scoreboard release ${SCOREBOARD_RELEASE}: ${SCOREBOARD_FAIL} failing client(s)/protocol(s)"
      failures=$((failures + 1))
    elif [[ "$SCOREBOARD_WARN" -gt 0 ]]; then
      echo "[FAIL] scoreboard release ${SCOREBOARD_RELEASE}: ${SCOREBOARD_WARN} degraded (warn) client(s)/protocol(s)"
      failures=$((failures + 1))
    elif [[ "$SCOREBOARD_PENDING" -gt 0 ]] && is_truthy "$STRICT_SCOREBOARD"; then
      echo "[FAIL] scoreboard release ${SCOREBOARD_RELEASE}: ${SCOREBOARD_PENDING} pending client(s) (blocked by strict mode)"
      failures=$((failures + 1))
    else
      echo "[PASS] scoreboard release ${SCOREBOARD_RELEASE}: pass=${SCOREBOARD_PASS} pending=${SCOREBOARD_PENDING} warn=${SCOREBOARD_WARN} fail=${SCOREBOARD_FAIL}"
    fi
  fi
else
  echo "[WARN] no scoreboard matrix supplied; train certified on per-repo RC evidence only"
fi

# --- Release notes -----------------------------------------------------------

notes="$(
  cat <<EOF
# Honua compatibility-train release-candidate gate

- environment: ${ENVIRONMENT}
- candidate: ${CANDIDATE_VERSION:-(version unset)}
- gate_mode: ${GATE_MODE}
- validation_mode: ${TRAIN_MODE}

## Per-repo release-candidate evidence
$(for repo in $TRAIN_REPOS; do
    echo "- ${repo}: status=${REPO_STATUS[$repo]} local_stack=${REPO_LOCAL_STACK[$repo]} target=${REPO_TARGET[$repo]} commit=${REPO_COMMIT[$repo]}"
  done)

## Client-compatibility scoreboard
$(if [[ -n "$SCOREBOARD_MATRIX" && -n "$SCOREBOARD_RELEASE" ]]; then
    echo "- latest release: ${SCOREBOARD_RELEASE} (pass=${SCOREBOARD_PASS} pending=${SCOREBOARD_PENDING} warn=${SCOREBOARD_WARN} fail=${SCOREBOARD_FAIL})"
  else
    echo "- not supplied (per-repo RC evidence only)"
  fi)
EOF
)"

echo "----- release-notes -----"
echo "$notes"
echo "-------------------------"

if [[ -n "$NOTES_OUTPUT" ]]; then
  printf '%s\n' "$notes" >"$NOTES_OUTPUT"
  echo "[INFO] release notes written to ${NOTES_OUTPUT}"
fi

# --- Verdict -----------------------------------------------------------------

if [[ "$failures" -gt 0 ]]; then
  echo "[RESULT] Compatibility-train release-candidate gate FAILED (${failures} blocking checks); the train is NOT releasable to ${ENVIRONMENT}."
  exit 1
fi

echo "[RESULT] Compatibility-train release-candidate gate PASSED; candidate ${CANDIDATE_VERSION:-(unset)} is eligible for ${GATE_MODE} to ${ENVIRONMENT}."
