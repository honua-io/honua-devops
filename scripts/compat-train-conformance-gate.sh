#!/usr/bin/env bash

# Honua cross-repo compatibility-train CONFORMANCE gate (honua-devops#68,
# epic honua-io/geospatial-grpc#18).
#
# This is the *producer* half of the compatibility train. Given a CANDIDATE
# under test -- a server image/version (e.g. honua-server:<rc-tag>) or an SDK
# version -- it actively runs EVERY consumer SDK's shared-fixture conformance
# against that candidate, collects the per-repo green/break verdict, and BLOCKS
# the bump (non-zero exit) if any consumer breaks. It emits the verdict in the
# evidence shape that scripts/compat-train-release-gate.sh (#41) already consumes
# (repos.<repo>.{status,local_stack,base_url,commit}), so the two compose:
#
#     conformance-gate (this) ---evidence.json---> release-gate (#41)
#         (produces verdict)                          (evaluates verdict)
#
# It is the mechanism that would have stopped honua-server#1238 (a server data-
# projection change that broke mobile FeatureServer/OGC reads in the field,
# caught by users, not CI) from reaching consumers: that JSONB-projection shape
# is one of the canonical fixtures every consumer round-trips here.
#
# The shared geospatial-grpc conformance fixtures (geospatial-grpc#3) are the
# single source of truth. They are PINNED by version (never copied); the gate
# refuses to run on an unpinned ('latest'/empty) fixtures version.
#
# KNOWN-EXPECTED-FAILING server gaps: the consumer conformance jobs xfail a set
# of already-tracked nightly honua-server gaps (honua-server#1238/#1166/#1167/
# #1237) with explicit issue references, so those jobs are green while the
# harness is in place. This gate mirrors that: a consumer whose ONLY failing
# fixtures map to a tracked gap is reported as green-with-known-gaps (recorded,
# never silently swallowed). Any NEW/untracked fixture break still FAILS. When a
# server fix lands, drop the issue from the registry's known_server_gaps and the
# xflip becomes required. The gate NEVER blanket-applies continue-on-error and
# NEVER fakes green.
#
# Default-safe posture (per AGENTS.md): this gate only PLANS/EVALUATES. It
# dispatches read-only conformance runs and evaluates their verdicts; it never
# deploys, submits, promotes, or rolls back the candidate. A break blocks the
# promotion path -- it does not roll anything back.
#
# Modes:
#   - dispatch (default when COMPAT_TRAIN_DISPATCH=true and gh is available):
#       dispatch each enrolled consumer's conformance workflow against the
#       candidate, wait for completion, and read the conclusion.
#   - results  (default for CI/dev/smoke): consume a pre-collected results JSON
#       (per-repo conclusion + optional failing-fixture detail) and/or env
#       overrides. This is how the smoke self-test and offline CI exercise every
#       pass/break path without a live cluster.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

# --- Configuration -----------------------------------------------------------

# Consumer-SDK conformance registry (workflow + dispatch interface per repo,
# plus the tracked known_server_gaps). Single source of truth for the train set.
REGISTRY="${COMPAT_TRAIN_CONSUMER_REGISTRY:-${REPO_ROOT}/compatibility/consumers.conformance.json}"

# The candidate under test. One of these MUST be supplied:
#   - a server image/tag (e.g. ghcr.io/honua-io/honua-server:2026.06.0-rc.1), or
#   - an SDK version label.
# The gate refuses to run with no candidate (you cannot validate "nothing").
CANDIDATE_IMAGE="${COMPAT_TRAIN_CANDIDATE_IMAGE:-}"
CANDIDATE_VERSION="${COMPAT_TRAIN_CANDIDATE_VERSION:-}"
CANDIDATE_COMMIT="${COMPAT_TRAIN_CANDIDATE_COMMIT:-}"

# Pinned shared-fixtures version (geospatial-grpc#3). MUST be an explicit pin --
# 'latest' or empty is rejected so CI is deterministic and the candidate is
# validated against a known canonical contract revision.
FIXTURES_VERSION="${COMPAT_TRAIN_FIXTURES_VERSION:-}"

# Train set. Defaults to every enrolled consumer in the registry; overridable
# to gate a partial train (e.g. a single-SDK hotfix). Space-separated.
TRAIN_REPOS="${COMPAT_TRAIN_REPOS:-}"

# Require even unenrolled consumers (e.g. honua-qgis-plugin) to report a verdict.
# Off by default: an unenrolled consumer (no conformance workflow yet) is skipped
# with an explicit note, never silently treated as green.
REQUIRE_ALL="${COMPAT_TRAIN_REQUIRE_ALL:-false}"

# Execution mode. dispatch => fire each consumer workflow via gh and read the
# conclusion; results => consume a pre-collected results JSON / env (CI/dev/smoke).
DISPATCH="${COMPAT_TRAIN_DISPATCH:-false}"

# Pre-collected per-repo results (results mode). Shape:
#   { "repos": { "<repo>": {
#       "conclusion": "success|failure",
#       "base_url": "...", "commit": "...", "local_stack": true|false,
#       "failing_fixtures": [ { "fixture": "...", "field": "...", "gap_issue": "honua-server#1238"|null } ]
#   } } }
RESULTS_FILE="${COMPAT_TRAIN_RESULTS_FILE:-}"

# Outputs.
EVIDENCE_OUTPUT="${COMPAT_TRAIN_EVIDENCE_OUTPUT:-}"   # evidence JSON consumed by #41
REPORT_OUTPUT="${COMPAT_TRAIN_REPORT_OUTPUT:-}"        # human-readable report

# Dispatch tuning.
DISPATCH_REF="${COMPAT_TRAIN_DISPATCH_REF:-trunk}"
DISPATCH_TIMEOUT="${COMPAT_TRAIN_DISPATCH_TIMEOUT:-2400}"  # seconds to wait per run

ENVIRONMENT="${COMPAT_TRAIN_ENVIRONMENT:-candidate}"

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
  local value="${1:-}"
  value="${value,,}"
  value="${value#"${value%%[![:space:]]*}"}"
  value="${value%"${value##*[![:space:]]}"}"
  printf '%s' "$value"
}

# repo_key honua-sdk-python -> HONUA_SDK_PYTHON
repo_key() {
  local key="${1^^}"
  printf '%s' "${key//[^A-Z0-9_]/_}"
}

require_command jq

# --- Validate inputs (block paths: missing candidate, unpinned fixtures) ------

if [[ ! -f "$REGISTRY" ]]; then
  echo "[ERROR] consumer registry not found: $REGISTRY" >&2
  exit 1
fi
if ! jq -e . "$REGISTRY" >/dev/null 2>&1; then
  echo "[ERROR] consumer registry is not valid JSON: $REGISTRY" >&2
  exit 1
fi

CANDIDATE_LABEL="${CANDIDATE_IMAGE:-$CANDIDATE_VERSION}"
if [[ -z "$CANDIDATE_LABEL" ]]; then
  echo "[BLOCK] no candidate under test: set COMPAT_TRAIN_CANDIDATE_IMAGE (server image/tag) or COMPAT_TRAIN_CANDIDATE_VERSION (SDK version). The gate refuses to validate an empty candidate." >&2
  exit 2
fi

fixtures_norm="$(normalize "$FIXTURES_VERSION")"
if [[ -z "$fixtures_norm" || "$fixtures_norm" == "latest" ]]; then
  echo "[BLOCK] shared-fixtures version is missing or unpinned (got '${FIXTURES_VERSION:-<empty>}'). Set COMPAT_TRAIN_FIXTURES_VERSION to an explicit geospatial-grpc fixtures pin (e.g. 0.1.0-alpha.1); 'latest' is rejected so the candidate is validated against a known canonical contract revision." >&2
  exit 2
fi

# Resolve the train set from the registry when not explicitly supplied.
if [[ -z "$(normalize "$TRAIN_REPOS")" ]]; then
  TRAIN_REPOS="$(jq -r '.consumers | keys[]' "$REGISTRY" | tr '\n' ' ')"
fi

# --- Per-repo verdict collection ---------------------------------------------

failures=0
gap_only_repos=0

declare -A REPO_STATUS        # pass | fail | skip
declare -A REPO_LOCAL_STACK
declare -A REPO_TARGET
declare -A REPO_COMMIT
declare -A REPO_DETAIL        # human-readable break/skip detail
declare -A REPO_GAPS          # known gap issues hit (space-separated)

# results_field <repo> <jq-path-suffix> -> value or empty
results_field() {
  local repo="$1" path="$2"
  [[ -z "$RESULTS_FILE" || ! -f "$RESULTS_FILE" ]] && { printf ''; return; }
  jq -r ".repos.\"$repo\".$path | if . == null then \"\" else (. | tostring) end" "$RESULTS_FILE" 2>/dev/null || true
}

# is_known_gap <issue> -> 0 if the issue is a tracked known_server_gap
is_known_gap() {
  local issue="$1"
  [[ -z "$issue" ]] && return 1
  jq -e --arg i "$issue" '.known_server_gaps | has($i)' "$REGISTRY" >/dev/null 2>&1
}

# dispatch_and_collect <repo> <workflow> <candidate_input> <fixtures_input> <commit_input>
# Dispatches the consumer conformance workflow against the candidate, waits, and
# echoes "conclusion<TAB>run_url". Requires gh. Read-only validation (no deploy).
dispatch_and_collect() {
  local repo="$1" workflow="$2" cand_input="$3" fix_input="$4" commit_input="$5"
  local gh_repo
  gh_repo="$(jq -r ".consumers.\"$repo\".repo" "$REGISTRY")"

  local -a args=(workflow run "$workflow" --repo "$gh_repo" --ref "$DISPATCH_REF")
  if [[ -n "$cand_input" ]]; then
    # mobile/dotnet/python take an image; js takes a base_url. We pass the image
    # tag/version for image inputs and the candidate label for base_url inputs;
    # the candidate label is the server target either way.
    args+=(-f "${cand_input}=${CANDIDATE_LABEL}")
  fi
  if [[ -n "$fix_input" ]]; then
    args+=(-f "${fix_input}=${FIXTURES_VERSION}")
  fi
  if [[ -n "$commit_input" && -n "$CANDIDATE_COMMIT" ]]; then
    args+=(-f "${commit_input}=${CANDIDATE_COMMIT}")
  fi

  # Record the newest EXISTING run id before dispatch so we can correlate the run we trigger.
  # GitHub run databaseIds increase monotonically, so the run we dispatch will have an id
  # strictly greater than this baseline. Without this we would `gh run list --limit 1` after a
  # fixed sleep and could certify on an OLDER still-SUCCESSFUL run instead of the one we just
  # triggered.
  local pre_id
  pre_id="$(gh run list --repo "$gh_repo" --workflow "$workflow" --branch "$DISPATCH_REF" --limit 1 --json databaseId -q '.[0].databaseId' 2>/dev/null || true)"
  [[ "$pre_id" =~ ^[0-9]+$ ]] || pre_id=0

  gh "${args[@]}" >&2 || { echo "dispatch-error"; return; }

  # Poll until a NEW run (databaseId strictly greater than the pre-dispatch baseline) appears,
  # rather than blindly taking the latest run after a fixed sleep.
  local run_id="" run_url candidate_id waited=0
  local poll_interval=5 appear_timeout="${DISPATCH_RUN_APPEAR_TIMEOUT:-120}"
  while (( waited < appear_timeout )); do
    sleep "$poll_interval"
    waited=$(( waited + poll_interval ))
    candidate_id="$(gh run list --repo "$gh_repo" --workflow "$workflow" --branch "$DISPATCH_REF" --limit 1 --json databaseId -q '.[0].databaseId' 2>/dev/null || true)"
    if [[ "$candidate_id" =~ ^[0-9]+$ && "$candidate_id" -gt "$pre_id" ]]; then
      run_id="$candidate_id"
      break
    fi
  done
  if [[ -z "$run_id" ]]; then
    # No NEW run materialized within the bound; do not fall back to a stale prior run.
    echo "dispatch-not-found"
    return
  fi
  run_url="$(gh run view "$run_id" --repo "$gh_repo" --json url -q .url 2>/dev/null || true)"
  if ! timeout "$DISPATCH_TIMEOUT" gh run watch "$run_id" --repo "$gh_repo" --exit-status >&2 2>&1; then
    printf 'failure\t%s\n' "$run_url"
    return
  fi
  printf 'success\t%s\n' "$run_url"
}

echo "[INFO] candidate=${CANDIDATE_LABEL} fixtures_version=${FIXTURES_VERSION} mode=$( is_truthy "$DISPATCH" && echo dispatch || echo results ) environment=${ENVIRONMENT}"
echo "[INFO] train_repos=${TRAIN_REPOS}"

for repo in $TRAIN_REPOS; do
  enrolled="$(jq -r ".consumers.\"$repo\".enrolled // false" "$REGISTRY")"
  workflow="$(jq -r ".consumers.\"$repo\".workflow // \"\"" "$REGISTRY")"
  cand_input="$(jq -r ".consumers.\"$repo\".candidate_input // \"\"" "$REGISTRY")"
  fix_input="$(jq -r ".consumers.\"$repo\".fixtures_input // \"\"" "$REGISTRY")"
  commit_input="$(jq -r ".consumers.\"$repo\".candidate_commit_input // \"\"" "$REGISTRY")"

  REPO_GAPS["$repo"]=""
  REPO_LOCAL_STACK["$repo"]="false"
  REPO_TARGET["$repo"]="${CANDIDATE_LABEL}"
  REPO_COMMIT["$repo"]="${CANDIDATE_COMMIT:-unset}"

  # Unenrolled consumer (no conformance workflow yet, e.g. qgis-plugin).
  if [[ "$enrolled" != "true" || -z "$workflow" ]]; then
    if is_truthy "$REQUIRE_ALL"; then
      echo "[FAIL] ${repo}: not enrolled in the shared-fixture conformance lane, but COMPAT_TRAIN_REQUIRE_ALL=true requires a verdict"
      REPO_STATUS["$repo"]="fail"
      REPO_DETAIL["$repo"]="not enrolled in shared-fixture conformance lane (no conformance workflow)"
      failures=$((failures + 1))
    else
      echo "[SKIP] ${repo}: not enrolled in the shared-fixture conformance lane (no conformance workflow); not required"
      REPO_STATUS["$repo"]="skip"
      REPO_DETAIL["$repo"]="not enrolled (no conformance workflow); not required"
    fi
    continue
  fi

  # Collect the conclusion + break detail for this consumer.
  conclusion=""
  if is_truthy "$DISPATCH"; then
    require_command gh
    out="$(dispatch_and_collect "$repo" "$workflow" "$cand_input" "$fix_input" "$commit_input")"
    conclusion="${out%%$'\t'*}"
    run_url="${out#*$'\t'}"
    [[ "$run_url" != "$out" ]] && REPO_TARGET["$repo"]="${run_url}"
    case "$conclusion" in
      success) conclusion="success" ;;
      *) conclusion="failure" ;;
    esac
  else
    raw="$(normalize "$(results_field "$repo" conclusion)")"
    case "$raw" in
      success | pass | passed | ok | green | true) conclusion="success" ;;
      "") conclusion="missing" ;;
      *) conclusion="failure" ;;
    esac
    bu="$(results_field "$repo" base_url)"; [[ -n "$bu" ]] && REPO_TARGET["$repo"]="$bu"
    co="$(results_field "$repo" commit)"; [[ -n "$co" ]] && REPO_COMMIT["$repo"]="$co"
    ls="$(normalize "$(results_field "$repo" local_stack)")"; [[ -n "$ls" ]] && REPO_LOCAL_STACK["$repo"]="$ls"
  fi

  # Env override of conclusion (highest precedence; matches the #41 gate style).
  key="$(repo_key "$repo")"
  env_status_var="COMPAT_TRAIN_REPO_${key}_STATUS"
  if [[ -n "${!env_status_var:-}" ]]; then
    case "$(normalize "${!env_status_var}")" in
      pass | passed | success | ok | green | true) conclusion="success" ;;
      *) conclusion="failure" ;;
    esac
  fi

  if [[ "$conclusion" == "missing" ]]; then
    echo "[FAIL] ${repo}: no conformance verdict collected for candidate ${CANDIDATE_LABEL}"
    REPO_STATUS["$repo"]="fail"
    REPO_DETAIL["$repo"]="no conformance verdict collected"
    failures=$((failures + 1))
    continue
  fi

  if [[ "$conclusion" == "success" ]]; then
    echo "[PASS] ${repo}: shared-fixture conformance green against candidate ${CANDIDATE_LABEL} (target=${REPO_TARGET[$repo]})"
    REPO_STATUS["$repo"]="pass"
    continue
  fi

  # conclusion == failure. Classify the failing fixtures: a break whose failing
  # fixtures ALL map to a tracked known_server_gap is KNOWN-EXPECTED-FAILING;
  # any new/untracked failing fixture blocks. We can only classify when the
  # results file carries per-fixture detail. A dispatched failure with no detail
  # is treated as a hard, untracked break (never silently passed).
  total_fail=0
  untracked_fail=0
  gaps_hit=""
  detail_msgs=""
  if [[ -n "$RESULTS_FILE" && -f "$RESULTS_FILE" ]]; then
    n="$(jq -r ".repos.\"$repo\".failing_fixtures | length // 0" "$RESULTS_FILE" 2>/dev/null || echo 0)"
    [[ "$n" =~ ^[0-9]+$ ]] || n=0
    for ((i = 0; i < n; i++)); do
      total_fail=$((total_fail + 1))
      fx="$(jq -r ".repos.\"$repo\".failing_fixtures[$i].fixture // \"?\"" "$RESULTS_FILE")"
      fld="$(jq -r ".repos.\"$repo\".failing_fixtures[$i].field // \"\"" "$RESULTS_FILE")"
      gap="$(jq -r ".repos.\"$repo\".failing_fixtures[$i].gap_issue // \"\"" "$RESULTS_FILE")"
      if is_known_gap "$gap"; then
        gaps_hit="${gaps_hit} ${gap}"
        detail_msgs="${detail_msgs}\n    - KNOWN-EXPECTED (${gap}): fixture ${fx}${fld:+ field ${fld}}"
      else
        untracked_fail=$((untracked_fail + 1))
        detail_msgs="${detail_msgs}\n    - UNTRACKED BREAK: fixture ${fx}${fld:+ field ${fld}}${gap:+ (claimed gap ${gap} is NOT in known_server_gaps)}"
      fi
    done
  fi

  if [[ "$total_fail" -gt 0 && "$untracked_fail" -eq 0 ]]; then
    # Every failing fixture is a tracked known gap: green-with-known-gaps. The
    # consumer's own job xfails these, so its conclusion should normally already
    # be success; we still record it so the gate is explicit and auditable.
    gaps_hit="$(echo "$gaps_hit" | tr ' ' '\n' | sort -u | tr '\n' ' ' | sed 's/^ *//;s/ *$//')"
    echo "[PASS] ${repo}: green against candidate ${CANDIDATE_LABEL} with KNOWN-EXPECTED gaps only (${gaps_hit})"
    echo -e "${detail_msgs}"
    REPO_STATUS["$repo"]="pass"
    REPO_GAPS["$repo"]="$gaps_hit"
    REPO_DETAIL["$repo"]="green; known-expected gaps: ${gaps_hit}"
    gap_only_repos=$((gap_only_repos + 1))
    continue
  fi

  # Real, blocking break.
  if [[ "$total_fail" -eq 0 ]]; then
    detail_msgs="\n    - conformance run failed against candidate ${CANDIDATE_LABEL} (no per-fixture detail available; treated as an untracked break)"
  fi
  gaps_hit="$(echo "$gaps_hit" | tr ' ' '\n' | sort -u | tr '\n' ' ' | sed 's/^ *//;s/ *$//')"
  echo "[BLOCK] ${repo}: BREAKS against candidate ${CANDIDATE_LABEL} (${untracked_fail:-?} untracked failing fixture(s))"
  echo -e "${detail_msgs}"
  REPO_STATUS["$repo"]="fail"
  REPO_GAPS["$repo"]="$gaps_hit"
  REPO_DETAIL["$repo"]="$(echo -e "${detail_msgs}" | tr '\n' ';' | sed 's/  */ /g')"
  failures=$((failures + 1))
done

# --- Emit evidence JSON (consumed by #41) ------------------------------------

# Build repos.<repo>.{status,local_stack,base_url,commit} (+ gate-specific
# detail/known_gaps fields, additive -- #41 ignores unknown fields).
build_evidence() {
  local repos_json="{}"
  for repo in $TRAIN_REPOS; do
    local st="${REPO_STATUS[$repo]:-missing}"
    # Map skip -> not included as a hard fail; #41 reads status pass/fail.
    local evidence_status="$st"
    [[ "$st" == "skip" ]] && evidence_status="skipped"
    repos_json="$(jq \
      --arg repo "$repo" \
      --arg status "$evidence_status" \
      --argjson local_stack "$( is_truthy "${REPO_LOCAL_STACK[$repo]:-false}" && echo true || echo false )" \
      --arg base_url "${REPO_TARGET[$repo]:-unset}" \
      --arg commit "${REPO_COMMIT[$repo]:-unset}" \
      --arg detail "${REPO_DETAIL[$repo]:-}" \
      --arg gaps "${REPO_GAPS[$repo]:-}" \
      '. + { ($repo): {
          status: $status,
          local_stack: $local_stack,
          base_url: $base_url,
          commit: $commit,
          detail: $detail,
          known_gaps: ($gaps | if . == "" then [] else (split(" ") | map(select(length > 0))) end)
        } }' <<<"$repos_json")"
  done

  jq -n \
    --arg version "${CANDIDATE_VERSION:-$CANDIDATE_IMAGE}" \
    --arg image "${CANDIDATE_IMAGE}" \
    --arg env "${ENVIRONMENT}" \
    --arg fixtures "${FIXTURES_VERSION}" \
    --argjson repos "$repos_json" \
    '{
      candidate: { version: $version, image: $image },
      environment: $env,
      fixtures_version: $fixtures,
      repos: $repos
    }'
}

EVIDENCE_JSON="$(build_evidence)"

if [[ -n "$EVIDENCE_OUTPUT" ]]; then
  printf '%s\n' "$EVIDENCE_JSON" >"$EVIDENCE_OUTPUT"
  echo "[INFO] evidence written to ${EVIDENCE_OUTPUT} (shape consumed by scripts/compat-train-release-gate.sh)"
fi

# --- Human-readable report ----------------------------------------------------

report="$(
  cat <<EOF
# Honua compatibility-train conformance gate

- candidate: ${CANDIDATE_LABEL}
- fixtures_version: ${FIXTURES_VERSION} (geospatial-grpc shared fixtures, pinned)
- environment: ${ENVIRONMENT}
- mode: $( is_truthy "$DISPATCH" && echo "dispatch (live consumer runs)" || echo "results (collected verdicts)" )

## Per-consumer conformance verdict
$(for repo in $TRAIN_REPOS; do
    st="${REPO_STATUS[$repo]:-missing}"
    line="- ${repo}: ${st}"
    [[ -n "${REPO_GAPS[$repo]:-}" ]] && line="${line} (known-expected gaps: ${REPO_GAPS[$repo]})"
    [[ "$st" == "fail" || "$st" == "skip" ]] && [[ -n "${REPO_DETAIL[$repo]:-}" ]] && line="${line} -- ${REPO_DETAIL[$repo]}"
    echo "$line"
  done)
EOF
)"

echo "----- conformance-report -----"
echo "$report"
echo "------------------------------"

if [[ -n "$REPORT_OUTPUT" ]]; then
  printf '%s\n' "$report" >"$REPORT_OUTPUT"
  echo "[INFO] report written to ${REPORT_OUTPUT}"
fi

# --- Verdict ------------------------------------------------------------------

if [[ "$failures" -gt 0 ]]; then
  echo "[RESULT] Compatibility-train conformance gate BLOCKED: ${failures} consumer(s) break against candidate ${CANDIDATE_LABEL}. The bump is NOT promotable. See the report above for the offending consumer(s)/fixture(s)/field(s)."
  exit 1
fi

if [[ "$gap_only_repos" -gt 0 ]]; then
  echo "[RESULT] Compatibility-train conformance gate PASSED for candidate ${CANDIDATE_LABEL} (${gap_only_repos} consumer(s) green with KNOWN-EXPECTED server gaps only; recorded, not silently swallowed). Candidate is promotable."
else
  echo "[RESULT] Compatibility-train conformance gate PASSED for candidate ${CANDIDATE_LABEL}; every consumer round-trips the canonical fixtures. Candidate is promotable."
fi
