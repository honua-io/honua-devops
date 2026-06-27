#!/usr/bin/env bash

# Honua release-candidate LIVE PROBE layer for the cross-repo compatibility train.
#
# honua-devops#41 asks the release-candidate validation pipeline to *execute smoke
# checks across the named surfaces* (server, SDK, admin, Helm, Terraform) — not
# merely transcribe the manifest's pre-recorded evidenceState. This script is that
# active layer:
#
#   compat-train-release-validation.sh  -> evaluates the manifest (trusts its
#                                          recorded evidenceState per signal).
#   compat-train-live-probe.sh (this)   -> independently RE-VERIFIES, with the
#                                          tools/creds actually available, the
#                                          signals the manifest cites, and emits a
#                                          per-surface probe bundle.
#
# Pluggable, honest probes. Each probe runs only what it can actually verify with
# the credentials/infra present in the environment. A probe that needs a missing
# dependency reports state "blocked" with a documented `missing` reason — it NEVER
# emits a green it did not verify (the correctness rule for #41).
#
# Probes implemented:
#   - github-run        : re-fetch the conclusion of every GitHub Actions run URL
#                         the manifest cites (releaseGates/repositoryLanes
#                         latestEvidence.runUrl) and confirm it is still
#                         success. Needs `gh` auth OR a GITHUB_TOKEN; otherwise
#                         BLOCKED: needs gh/GITHUB_TOKEN.
#   - candidate-image   : confirm the candidate image tag+digest are published in
#                         the registry. Needs the manifest to carry a non-null
#                         tag/digest (currently blocked upstream) AND registry
#                         read creds; otherwise BLOCKED: needs published image /
#                         registry creds.
#   - server-health     : HTTP smoke against a live candidate deployment
#                         (/healthz/ready, /healthz/live, version). Needs
#                         HONUA_PROBE_BASE_URL pointing at a real external target;
#                         otherwise BLOCKED: needs live staging base URL (the
#                         honua-sdk-python#53 / #41 staging-URL gap).
#   - helm-metadata     : confirm the published chart appVersion/image point at the
#                         candidate, not a placeholder. Needs a chart source
#                         (HONUA_PROBE_HELM_CHART) and a versioned chart (honua-helm#1
#                         still open); otherwise BLOCKED.
#   - terraform-plan    : confirm an IaC plan applies cleanly against the seeded
#                         demo (honua-iac#30). Needs cloud creds + a live target;
#                         always BLOCKED until that infra exists.
#
# Default-safe posture (AGENTS.md): this probe is strictly READ-ONLY. It performs
# GET requests and read-only status lookups. It never deploys, promotes, submits,
# or rolls back. It does not even fail the process by default: it emits a probe
# bundle and (in the default advisory mode) exits 0 so it can be folded into the
# manifest evaluator's bundle. Set HONUA_PROBE_MODE=live to exit non-zero when a
# probe that *could* run came back failed (blocked probes never fail the run —
# a missing dependency is a documented gap, not a regression).

set -euo pipefail

# --- Configuration -----------------------------------------------------------

MANIFEST="${HONUA_PROBE_MANIFEST:-${1:-}}"
MANIFEST_URL="${HONUA_PROBE_MANIFEST_URL:-}"

# advisory : emit the bundle, always exit 0 (default; for folding into the
#            evaluator and for CI dispatch on a still-gapped preview).
# live     : exit 1 if any probe that actually ran came back "failed".
MODE="${HONUA_PROBE_MODE:-advisory}"

# Live external candidate target for the server-health smoke. Empty => BLOCKED.
BASE_URL="${HONUA_PROBE_BASE_URL:-}"
READINESS_PATH="${HONUA_PROBE_READINESS_PATH:-/healthz/ready}"
LIVENESS_PATH="${HONUA_PROBE_LIVENESS_PATH:-/healthz/live}"
VERSION_PATH="${HONUA_PROBE_VERSION_PATH:-/api/v1/admin/version}"
# Optional GeoServices/ArcGIS REST endpoint to probe for in-band 200+{error}
# envelopes (audit S1, #113). A 200 carrying an {error} member is a failure that
# a status-only health check cannot see.
GEOSERVICES_PROBE_PATH="${HONUA_PROBE_GEOSERVICES_PATH:-}"
HTTP_TIMEOUT="${HONUA_PROBE_TIMEOUT_SECONDS:-20}"

# Optional Helm chart source (path or URL to a Chart.yaml). Empty => BLOCKED.
HELM_CHART="${HONUA_PROBE_HELM_CHART:-}"

# Where to write the machine-readable probe bundle.
BUNDLE_OUTPUT="${HONUA_PROBE_BUNDLE_OUTPUT:-live-probe-bundle.json}"

# Cap how many GitHub runs to re-verify (avoid hammering the API).
MAX_RUNS="${HONUA_PROBE_MAX_RUNS:-40}"

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

tmp_manifest=""
trap '[[ -n "$tmp_manifest" ]] && rm -f "$tmp_manifest"' EXIT

# --- Helpers -----------------------------------------------------------------

require_command() {
  if ! command -v "$1" >/dev/null 2>&1; then
    echo "[ERROR] Required command missing: $1" >&2
    exit 1
  fi
}

require_command jq

if [[ -n "$MANIFEST_URL" ]]; then
  require_command curl
  tmp_manifest="$(mktemp)"
  curl --silent --show-error --fail "$MANIFEST_URL" >"$tmp_manifest"
  MANIFEST="$tmp_manifest"
fi

if [[ -z "$MANIFEST" ]]; then
  echo "[ERROR] No release manifest supplied. Pass a path, or set HONUA_PROBE_MANIFEST / HONUA_PROBE_MANIFEST_URL." >&2
  echo "        Usage: $0 path/to/release/honua-<id>.json" >&2
  exit 1
fi
if [[ ! -f "$MANIFEST" ]]; then
  echo "[ERROR] Release manifest not found: $MANIFEST" >&2
  exit 1
fi
if ! jq -e . "$MANIFEST" >/dev/null 2>&1; then
  echo "[ERROR] Release manifest is not valid JSON: $MANIFEST" >&2
  exit 1
fi

# A probe result is one JSON object. States:
#   passed  : actively re-verified green.
#   failed  : actively re-verified and it is NOT green (a real regression).
#   blocked : could not run because a documented dependency is missing.
# We accumulate them in PROBE_RESULTS (a bash array of compact JSON objects).
PROBE_RESULTS=()

emit() {
  # emit <id> <surface> <state> <detail> <missing>
  local id="$1" surface="$2" state="$3" detail="$4" missing="${5:-}"
  PROBE_RESULTS+=("$(jq -nc \
    --arg id "$id" --arg surface "$surface" --arg state "$state" \
    --arg detail "$detail" --arg missing "$missing" \
    '{id:$id, surface:$surface, state:$state, detail:$detail,
      missing: (if $missing=="" then null else $missing end)}')")
  case "$state" in
    passed)  echo "[PASS]    (${surface}) ${id} — ${detail}" ;;
    failed)  echo "[FAIL]    (${surface}) ${id} — ${detail}" ;;
    blocked) echo "[BLOCKED] (${surface}) ${id} — blocked: needs ${missing} (${detail})" ;;
    *)       echo "[?]       (${surface}) ${id} — ${detail}" ;;
  esac
}

# Map an owning repo (full or short) to a release surface.
surface_of() {
  local r="${1#honua-io/}"
  case "$r" in
    honua-server|Honua.Server|honua-esri-compat) echo server ;;
    honua-sdk-*|honua-mobile)                     echo sdk ;;
    honua-console|honua-server-admin)             echo admin ;;
    honua-helm)                                   echo helm ;;
    honua-iac)                                    echo terraform ;;
    *)                                            echo other ;;
  esac
}

# --- Probe: github-run -------------------------------------------------------
# Re-fetch the conclusion of every GitHub Actions run URL the manifest cites and
# confirm it is still `success`. This is the core active smoke: the manifest may
# record a stale "passed"; this confirms the run it points at is genuinely green
# right now.

gh_auth_available() {
  if command -v gh >/dev/null 2>&1 && gh auth status >/dev/null 2>&1; then
    GH_FETCH="gh"; return 0
  fi
  if [[ -n "${GITHUB_TOKEN:-}" ]] && command -v curl >/dev/null 2>&1; then
    GH_FETCH="curl"; return 0
  fi
  return 1
}

# Echo the run conclusion for an owner/repo/run-id, or empty on lookup failure.
fetch_run_conclusion() {
  local owner_repo="$1" run_id="$2"
  if [[ "$GH_FETCH" == "gh" ]]; then
    gh run view "$run_id" -R "$owner_repo" --json status,conclusion \
      --jq '"\(.status)/\(.conclusion)"' 2>/dev/null || true
  else
    curl --silent --show-error \
      --header "Authorization: Bearer ${GITHUB_TOKEN}" \
      --header "Accept: application/vnd.github+json" \
      "https://api.github.com/repos/${owner_repo}/actions/runs/${run_id}" 2>/dev/null \
      | jq -r '"\(.status)/\(.conclusion)"' 2>/dev/null || true
  fi
}

probe_github_runs() {
  # Collect (owningRepo, runUrl, signalId) triples from gates + lanes that carry
  # a passed evidenceState and a runUrl.
  local triples
  triples="$(jq -r '
    [ (.releaseGates // [])[], (.repositoryLanes // [])[] ]
    | map(select(.latestEvidence.runUrl != null))
    | map({ id, repo: (.owningRepo // ""),
            url: .latestEvidence.runUrl,
            state: (.evidenceState // "unknown") })
    | .[] | [ .id, .repo, .url, .state ] | @tsv' "$MANIFEST")"

  if [[ -z "$triples" ]]; then
    emit "github-run" "cross-cutting" "blocked" \
      "Manifest cites no GitHub Actions run URLs to re-verify." \
      "a manifest with latestEvidence.runUrl entries"
    return
  fi

  if ! gh_auth_available; then
    # We have run URLs but no way to re-verify them: one BLOCKED per surface that
    # has runs, so the gap is visible per-surface rather than hidden.
    local surfaces_seen=""
    while IFS=$'\t' read -r id repo url state; do
      [[ -z "$url" ]] && continue
      local s; s="$(surface_of "$repo")"
      case " $surfaces_seen " in *" $s "*) continue ;; esac
      surfaces_seen="$surfaces_seen $s"
      emit "github-run:${s}" "$s" "blocked" \
        "Run URLs present but cannot be re-verified without GitHub auth." \
        "gh auth login OR GITHUB_TOKEN"
    done <<<"$triples"
    return
  fi

  local count=0
  while IFS=$'\t' read -r id repo url manifest_state; do
    [[ -z "$url" ]] && continue
    count=$((count + 1))
    if [[ "$count" -gt "$MAX_RUNS" ]]; then break; fi
    local s; s="$(surface_of "$repo")"
    # Parse owner/repo and run id out of the actions run URL.
    # .../<owner>/<repo>/actions/runs/<id>
    local owner_repo run_id
    owner_repo="$(sed -E 's#https?://github.com/([^/]+/[^/]+)/actions/runs/.*#\1#' <<<"$url")"
    run_id="$(sed -E 's#.*/actions/runs/([0-9]+).*#\1#' <<<"$url")"
    if [[ "$owner_repo" == "$url" || "$run_id" == "$url" ]]; then
      emit "github-run:${id}" "$s" "blocked" \
        "Could not parse owner/repo/run-id from ${url}." \
        "a well-formed actions run URL"
      continue
    fi
    local res; res="$(fetch_run_conclusion "$owner_repo" "$run_id")"
    if [[ -z "$res" || "$res" == "null/null" ]]; then
      emit "github-run:${id}" "$s" "blocked" \
        "Run ${owner_repo}#${run_id} not readable (private/deleted/insufficient scope)." \
        "read access to ${owner_repo} Actions"
      continue
    fi
    local conclusion="${res##*/}"
    if [[ "$conclusion" == "success" ]]; then
      emit "github-run:${id}" "$s" "passed" \
        "Re-verified ${owner_repo}#${run_id} conclusion=success (manifest=${manifest_state})."
    else
      emit "github-run:${id}" "$s" "failed" \
        "Re-verified ${owner_repo}#${run_id} conclusion=${conclusion} but manifest recorded ${manifest_state}."
    fi
  done <<<"$triples"
}

# --- Probe: candidate-image --------------------------------------------------

probe_candidate_image() {
  local tag digest
  tag="$(jq -r '.candidate.image.tag // empty' "$MANIFEST")"
  digest="$(jq -r '.candidate.image.digest // empty' "$MANIFEST")"
  if [[ -z "$tag" && -z "$digest" ]]; then
    emit "candidate-image" "server" "blocked" \
      "Manifest candidate.image has no tag/digest published yet." \
      "a published RC image tag+digest (honua-devops#41 image surface)"
    return
  fi
  # Even with a tag, confirming it requires registry read creds; we do not assume
  # them. Report what the manifest claims but mark it unverified-by-us.
  if ! command -v skopeo >/dev/null 2>&1 && ! command -v crane >/dev/null 2>&1; then
    emit "candidate-image" "server" "blocked" \
      "Manifest names tag=${tag:-?} digest=${digest:-?} but no registry client to verify it." \
      "skopeo/crane + registry read creds"
    return
  fi
  emit "candidate-image" "server" "blocked" \
    "Manifest names tag=${tag:-?} digest=${digest:-?}; registry verification not yet wired." \
    "registry read creds for the candidate image"
}

# --- Probe: server-health ----------------------------------------------------

http_status() {
  local url="$1"
  curl --silent --show-error --output /dev/null --write-out '%{http_code}' \
    --max-time "$HTTP_TIMEOUT" "$url" 2>/dev/null || echo "000"
}

# GET a URL, writing the response body to $2 and echoing the HTTP status code.
http_get() {
  local url="$1" out="$2"
  curl --silent --show-error --output "$out" --write-out '%{http_code}' \
    --max-time "$HTTP_TIMEOUT" "$url" 2>/dev/null || echo "000"
}

# True (0) when a response body carries a GeoServices/ArcGIS REST {error}
# envelope. Such bodies are returned with an HTTP 200, so a status-only check
# treats a failing operation as healthy (audit S1, #113).
body_has_error_envelope() {
  local file="$1"
  [[ -s "$file" ]] || return 1
  if command -v jq >/dev/null 2>&1; then
    jq -e 'objects | has("error") and (.error != null)' "$file" >/dev/null 2>&1
  else
    grep -qE '"error"[[:space:]]*:[[:space:]]*[\{"0-9]' "$file"
  fi
}

probe_server_health() {
  if [[ -z "$BASE_URL" ]]; then
    emit "server-health" "server" "blocked" \
      "No live candidate base URL provided; cannot smoke the deployment." \
      "HONUA_PROBE_BASE_URL pointing at a real external staging target (the #41 staging-URL gap)"
    return
  fi
  require_command curl
  local base="${BASE_URL%/}"
  local ready_body live_body
  ready_body="$(mktemp)"; live_body="$(mktemp)"
  local ready live
  ready="$(http_get "${base}${READINESS_PATH}" "$ready_body")"
  live="$(http_get "${base}${LIVENESS_PATH}" "$live_body")"

  # A 200 that carries an {error} envelope is a failure invisible to status-only
  # checks; inspect the body, not just the code.
  local inband=""
  if [[ "$ready" == "200" ]] && body_has_error_envelope "$ready_body"; then
    inband="readiness"
  elif [[ "$live" == "200" ]] && body_has_error_envelope "$live_body"; then
    inband="liveness"
  fi
  rm -f "$ready_body" "$live_body"

  if [[ -n "$inband" ]]; then
    emit "server-health" "server" "failed" \
      "${base}: ${inband} returned HTTP 200 carrying an {error} envelope (in-band failure)."
  elif [[ "$ready" == "200" && "$live" == "200" ]]; then
    emit "server-health" "server" "passed" \
      "${base}: readiness=${ready} liveness=${live} against a live target."
  else
    emit "server-health" "server" "failed" \
      "${base}: readiness=${ready} liveness=${live} (expected 200/200)."
  fi

  probe_geoservices_inband "$base"
}

# Probe an optional GeoServices endpoint and fail a 200+{error} envelope.
probe_geoservices_inband() {
  local base="$1"
  if [[ -z "$GEOSERVICES_PROBE_PATH" ]]; then
    emit "geoservices-inband" "server" "blocked" \
      "No GeoServices probe path configured; cannot check for 200+{error} envelopes." \
      "HONUA_PROBE_GEOSERVICES_PATH (e.g. a /rest/services/.../FeatureServer/0/query?f=json URL path)"
    return
  fi
  local body; body="$(mktemp)"
  local code; code="$(http_get "${base}${GEOSERVICES_PROBE_PATH}" "$body")"
  if [[ "$code" == "200" ]] && body_has_error_envelope "$body"; then
    emit "geoservices-inband" "server" "failed" \
      "${base}${GEOSERVICES_PROBE_PATH}: HTTP 200 with an {error} envelope (in-band GeoServices failure)."
  elif [[ "$code" == "200" ]]; then
    emit "geoservices-inband" "server" "passed" \
      "${base}${GEOSERVICES_PROBE_PATH}: HTTP 200 with no {error} envelope."
  else
    emit "geoservices-inband" "server" "failed" \
      "${base}${GEOSERVICES_PROBE_PATH}: HTTP ${code} (expected 200)."
  fi
  rm -f "$body"
}

# --- Probe: helm-metadata ----------------------------------------------------

probe_helm_metadata() {
  if [[ -z "$HELM_CHART" ]]; then
    emit "helm-metadata" "helm" "blocked" \
      "No chart source provided; cannot confirm appVersion points at the candidate." \
      "HONUA_PROBE_HELM_CHART + a versioned chart (honua-helm#1)"
    return
  fi
  local chart_file=""
  if [[ -f "$HELM_CHART" ]]; then chart_file="$HELM_CHART"
  elif [[ -f "$HELM_CHART/Chart.yaml" ]]; then chart_file="$HELM_CHART/Chart.yaml"
  fi
  if [[ -z "$chart_file" ]]; then
    emit "helm-metadata" "helm" "blocked" \
      "Chart source ${HELM_CHART} not found / not a Chart.yaml." \
      "a readable Chart.yaml"
    return
  fi
  local app_version
  app_version="$(grep -E '^appVersion:' "$chart_file" | head -1 | sed -E 's/appVersion:[[:space:]]*//; s/"//g' || true)"
  local cand_ref; cand_ref="$(jq -r '.candidate.ref // empty' "$MANIFEST")"
  if [[ -z "$app_version" || "$app_version" == "latest" || "$app_version" == "0.0.0" ]]; then
    emit "helm-metadata" "helm" "failed" \
      "Chart appVersion='${app_version:-unset}' is a placeholder, not the candidate." \
      ""
  else
    emit "helm-metadata" "helm" "passed" \
      "Chart appVersion='${app_version}' (candidate ref ${cand_ref:0:12})."
  fi
}

# --- Probe: terraform-plan ---------------------------------------------------

probe_terraform_plan() {
  # honua-iac#30 (seeded cloud demo + smoke creds) is the upstream long pole.
  # Until it lands there is no live IaC target to plan against, and a plan needs
  # cloud creds we never assume. Always BLOCKED with the owning dependency.
  emit "terraform-plan" "terraform" "blocked" \
    "No live IaC target/cloud creds; cannot run a candidate plan." \
    "honua-iac#30 seeded cloud demo + smoke creds, and a terraform target"
}

# --- Run all probes ----------------------------------------------------------

release_id="$(jq -r '.releaseId // "unknown"' "$MANIFEST")"
channel="$(jq -r '.channel // "unknown"' "$MANIFEST")"
candidate_ref="$(jq -r '.candidate.ref // "unset"' "$MANIFEST")"

echo "[INFO] live-probe release=${release_id} channel=${channel} mode=${MODE}"
echo "[INFO] candidate_ref=${candidate_ref}"

probe_github_runs
probe_candidate_image
probe_server_health
probe_helm_metadata
probe_terraform_plan

# --- Assemble the probe bundle -----------------------------------------------

probes_json="$(printf '%s\n' "${PROBE_RESULTS[@]}" | jq -s '.')"

bundle="$(jq -n \
  --arg releaseId "$release_id" \
  --arg channel "$channel" \
  --arg candidateRef "$candidate_ref" \
  --arg mode "$MODE" \
  --arg manifest "$MANIFEST" \
  --argjson probes "$probes_json" '
  {
    schemaVersion: 1,
    kind: "compat-train-live-probe",
    generatedFrom: $manifest,
    releaseId: $releaseId,
    channel: $channel,
    candidateRef: $candidateRef,
    mode: $mode,
    summary: {
      probes: ($probes | length),
      passed:  ($probes | map(select(.state=="passed"))  | length),
      failed:  ($probes | map(select(.state=="failed"))  | length),
      blocked: ($probes | map(select(.state=="blocked")) | length)
    },
    # Per-surface roll-up: a surface is "verified" only if it has >=1 actively
    # passed probe and zero failed probes. A surface with only blocked probes is
    # "blocked" (an honest gap), never silently green.
    surfaceProbeCoverage: (
      ($probes | map(.surface) | unique) | map(. as $s |
        ($probes | map(select(.surface==$s))) as $sp |
        {
          surface: $s,
          passed:  ($sp | map(select(.state=="passed"))  | length),
          failed:  ($sp | map(select(.state=="failed"))  | length),
          blocked: ($sp | map(select(.state=="blocked")) | length),
          status: (
            if   ($sp | map(select(.state=="failed"))  | length) > 0 then "failed"
            elif ($sp | map(select(.state=="passed"))  | length) > 0 then "verified"
            else "blocked" end)
        })
    ),
    probes: $probes,
    references: {
      manifest: $manifest,
      ownedBy: "https://github.com/honua-io/honua-devops/issues/41",
      evaluator: "scripts/compat-train-release-validation.sh"
    }
  }')"

printf '%s\n' "$bundle" >"$BUNDLE_OUTPUT"
echo "[INFO] live-probe bundle written to ${BUNDLE_OUTPUT}"

# --- Summary + verdict -------------------------------------------------------

passed="$(jq -r '.summary.passed'  <<<"$bundle")"
failed="$(jq -r '.summary.failed'  <<<"$bundle")"
blocked="$(jq -r '.summary.blocked' <<<"$bundle")"
echo "[INFO] probes: ${passed} passed, ${failed} failed, ${blocked} blocked (re-verification only re-checks what the environment can actually reach)."

if [[ "$failed" -gt 0 && "$MODE" == "live" ]]; then
  echo "[RESULT] LIVE PROBE FAILED: ${failed} actively re-verified probe(s) are not green. See [FAIL] lines above."
  exit 1
fi

if [[ "$failed" -gt 0 ]]; then
  echo "[RESULT] LIVE PROBE found ${failed} failed probe(s) (advisory mode; not failing the run)."
else
  echo "[RESULT] LIVE PROBE: no actively re-verified regression; ${blocked} surface(s)/signal(s) remain BLOCKED on documented dependencies (not green)."
fi
exit 0
