#!/usr/bin/env bash

# Honua release-candidate validation for the cross-repo compatibility train.
#
# This is the operator-side validation *surface* for honua-devops#41: it consumes
# the canonical release-train manifest published by honua-server
# (release/honua-<id>.json) and decides whether the candidate is releasable as a
# train. It evaluates every release signal the manifest records:
#
#   - releaseGates[]        -> server-surface gates (SDK compat, interop, security,
#                              license activation, ...)
#   - repositoryLanes[]     -> per-repo lanes (SDK, mobile, admin, Helm, ...)
#   - releaseLaneCriteria[] -> cross-cutting lane criteria
#   - candidate.image       -> the immutable release-candidate image must be published
#
# Each signal is mapped to one of the required release surfaces
# (server, sdk, admin, helm, terraform) and the run reports:
#   - exact versions (releaseId, candidate ref, channel) and environment;
#   - a pass/fail check per signal;
#   - the owning follow-up issue(s) for every blocking gap (the manifest's
#     blocker URLs, plus a synthetic follow-up for any required surface that has
#     no evidence at all);
#   - a machine-readable evidence bundle (the release-train scoreboard) that can
#     be attached to the release gate / roadmap project.
#
# Default-safe posture (per AGENTS.md): this validator only *evaluates*. It reads
# a manifest and emits a verdict + evidence bundle; it never deploys, promotes,
# submits, or rolls back. A blocked manifest fails the run (exit 1) in the
# default live mode so it cannot be declared validated on incomplete evidence.
#
# This complements scripts/compat-train-release-gate.sh, which checks the *live*
# nature of per-repo run evidence (the honua-sdk-python#53 local-fallback trap).
# The gate proves "the SDK runs were against a real target"; this validator
# proves "every surface the release manifest tracks is green or waived".

set -euo pipefail

# --- Configuration -----------------------------------------------------------

# The release surfaces that must be covered by at least one green/waived signal.
# Terraform (honua-iac) has no lane in the current manifest, so the default set
# deliberately surfaces that as a release gap with an owning follow-up.
REQUIRED_SURFACES="${COMPAT_TRAIN_REQUIRED_SURFACES:-server sdk admin helm terraform}"

# live     : any blocked/missing signal (or uncovered required surface) fails the run.
# advisory : evaluate and emit the bundle, but always exit 0 (report-only).
MODE="${COMPAT_TRAIN_MODE:-live}"

# Honor per-item .waiver (and an approved waiver counts as a pass). Set false to
# treat waived items as still-blocking.
ALLOW_WAIVERS="${COMPAT_TRAIN_ALLOW_WAIVERS:-true}"

# Environment label for the evidence bundle; defaults to the manifest channel.
ENVIRONMENT="${COMPAT_TRAIN_ENVIRONMENT:-}"

# Optional published client-compatibility scoreboard matrix. When supplied, the
# latest release in the matrix must have no failing client/protocol statuses.
SCOREBOARD_MATRIX="${COMPAT_TRAIN_SCOREBOARD_MATRIX:-}"

# Where to write the machine-readable evidence bundle (the release-train
# scoreboard). Always written so the bundle can be attached as release evidence.
BUNDLE_OUTPUT="${COMPAT_TRAIN_BUNDLE_OUTPUT:-release-validation-bundle.json}"

# Optional human-readable release-notes block output file.
NOTES_OUTPUT="${COMPAT_TRAIN_RELEASE_NOTES_OUTPUT:-}"

MANIFEST="${COMPAT_TRAIN_MANIFEST:-${1:-}}"
MANIFEST_URL="${COMPAT_TRAIN_MANIFEST_URL:-}"

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
  echo "[ERROR] No release manifest supplied. Pass a path, or set COMPAT_TRAIN_MANIFEST / COMPAT_TRAIN_MANIFEST_URL." >&2
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

# Optional scoreboard -> compact { release, summary } or null.
scoreboard_json="null"
if [[ -n "$SCOREBOARD_MATRIX" ]]; then
  if [[ ! -f "$SCOREBOARD_MATRIX" ]]; then
    echo "[ERROR] Scoreboard matrix not found: $SCOREBOARD_MATRIX" >&2
    exit 1
  fi
  if ! jq -e . "$SCOREBOARD_MATRIX" >/dev/null 2>&1; then
    echo "[ERROR] Scoreboard matrix is not valid JSON: $SCOREBOARD_MATRIX" >&2
    exit 1
  fi
  scoreboard_json="$(jq -c '{release: .releases[0].release, summary: .releases[0].summary}' "$SCOREBOARD_MATRIX")"
fi

# --- Build the evidence bundle from the manifest -----------------------------

bundle="$(
  jq -n \
    --slurpfile manifest "$MANIFEST" \
    --argjson scoreboard "$scoreboard_json" \
    --arg manifestPath "$MANIFEST" \
    --arg requiredSurfaces "$REQUIRED_SURFACES" \
    --arg mode "$MODE" \
    --arg allowWaivers "$ALLOW_WAIVERS" \
    --arg environment "$ENVIRONMENT" '

    ($manifest[0]) as $m |

    # Map an owning repo to its release surface.
    def surface_of($repo):
      (($repo // "") | sub("^honua-io/"; "")) as $r |
      if   $r == "honua-server" or $r == "Honua.Server" then "server"
      elif ($r | test("^honua-sdk-")) or $r == "honua-mobile" then "sdk"
      elif $r == "honua-console" or $r == "honua-server-admin" then "admin"
      elif $r == "honua-helm" then "helm"
      elif $r == "honua-iac" then "terraform"
      elif $r == "honua-devops" then "tooling"
      else "other" end;

    def follow_ups($blockers):
      ($blockers // []) | map({ url: .url, repo: .repo, number: .number, reason: .reason });

    # Resolve an item state: passed -> "passed"; an approved waiver (when honored)
    # -> "waived" (counts as a pass); anything else -> "blocked".
    def state_of($ev; $waiver):
      if $ev == "passed" then "passed"
      elif ($waiver != null and $allowWaivers == "true") then "waived"
      else "blocked" end;

    ($requiredSurfaces | split(" ") | map(select(length > 0))) as $required |

    # --- release gates (server surface) ---
    ([ $m.releaseGates[]? | {
        id, name: (.name // .id), kind: "releaseGate",
        owningRepo: (.owningRepo // "honua-io/honua-server"),
        surface: surface_of(.owningRepo // "honua-io/honua-server"),
        requirement: (.requirement // .name // .id),
        evidenceState: .evidenceState,
        state: state_of(.evidenceState; .waiver),
        followUps: follow_ups(.blockers)
    } ]) as $gates |

    # --- per-repo lanes (sdk / admin / helm / ...) ---
    ([ $m.repositoryLanes[]? | {
        id, name: (.name // .id), kind: "repositoryLane",
        owningRepo: (.owningRepo // ""),
        surface: surface_of(.owningRepo // ""),
        requirement: (.requirement // .id),
        evidenceState: .evidenceState,
        state: state_of(.evidenceState; .waiver),
        followUps: follow_ups(.blockers)
    } ]) as $lanes |

    # --- cross-cutting lane criteria ---
    ([ $m.releaseLaneCriteria[]? | {
        id, name: .id, kind: "releaseLaneCriteria",
        owningRepo: ((.blockers[0].repo) // ""),
        surface: "cross-cutting",
        requirement: (.requirement // .id),
        evidenceState: .evidenceState,
        state: state_of(.evidenceState; .waiver),
        followUps: follow_ups(.blockers)
    } ]) as $criteria |

    # --- the release-candidate image (server surface) ---
    ([ $m.candidate.image // empty | {
        id: "candidate-image", name: "Release-candidate image published",
        kind: "candidateImage",
        owningRepo: (.repository // "honua-io/honua-server"),
        surface: "server",
        requirement: "Immutable release-candidate image tag and digest are published.",
        evidenceState: .evidenceState,
        state: (if .evidenceState == "passed" then "passed" else "blocked" end),
        followUps: follow_ups(.blockers)
    } ]) as $image |

    # --- client-compatibility scoreboard (optional, cross-cutting) ---
    (if $scoreboard != null and ($scoreboard.summary != null) then
      ($scoreboard.summary.fail // 0) as $fail |
      [ {
        id: "client-compatibility-scoreboard", name: "Client compatibility scoreboard",
        kind: "scoreboard", owningRepo: "honua-io/honua-devops",
        surface: "cross-cutting",
        requirement: "Latest scoreboard release has no failing clients.",
        evidenceState: (if $fail > 0 then "blocked" else "passed" end),
        state: (if $fail > 0 then "blocked" else "passed" end),
        followUps: (if $fail > 0
          then [ { url: null, repo: null, number: null,
                   reason: ("Scoreboard release \($scoreboard.release) has \($fail) failing client(s)/protocol(s).") } ]
          else [] end)
      } ]
     else [] end) as $sbcheck |

    ($gates + $lanes + $criteria + $image + $sbcheck) as $checks |

    # Surface coverage: each required surface needs >=1 passed/waived check.
    ([ $required[] as $s |
        ($checks | map(select(.surface == $s))) as $sc |
        ($sc | map(select(.state == "passed" or .state == "waived")) | length) as $pass |
        { surface: $s, covered: ($pass > 0), passing: $pass,
          total: ($sc | length), checks: ($sc | map(.id)) }
    ]) as $coverage |

    ($coverage | map(select(.covered | not) | .surface)) as $missingSurfaces |
    ($checks | map(select(.state == "blocked"))) as $blocked |

    # Owning follow-ups for every gap: manifest blocker URLs, a synthetic note
    # for any blocked check with no tracked issue, and one per uncovered surface.
    ( [ $blocked[] | select(.followUps | length > 0) | .followUps[] ]
      + [ $blocked[] | select(.followUps | length == 0)
          | { url: null, repo: null, number: null,
              reason: ("\(.id): blocked with no tracked follow-up issue.") } ]
      + [ $missingSurfaces[]
          | { url: null, repo: null, number: null,
              reason: ("No \(.) lane/evidence present in the release manifest; add release-candidate evidence for the \(.) surface.") } ]
    ) as $allFollow |

    (($blocked | length) == 0 and ($missingSurfaces | length) == 0) as $ok |

    {
      schemaVersion: 1,
      kind: "compat-train-release-validation",
      generatedFrom: $manifestPath,
      releaseId: $m.releaseId,
      channel: $m.channel,
      environment: (if $environment == "" then ($m.channel // "preview") else $environment end),
      observedAt: $m.observedAt,
      mode: $mode,
      candidate: {
        ref: $m.candidate.ref,
        refSource: $m.candidate.refSource,
        image: ($m.candidate.image // null)
      },
      verdict: (if $ok then "pass" else "fail" end),
      summary: {
        checks: ($checks | length),
        passed: ($checks | map(select(.state == "passed")) | length),
        waived: ($checks | map(select(.state == "waived")) | length),
        blocked: ($blocked | length),
        requiredSurfaces: $required,
        surfacesCovered: ($coverage | map(select(.covered) | .surface)),
        surfacesMissing: $missingSurfaces
      },
      surfaceCoverage: $coverage,
      checks: $checks,
      scoreboard: $scoreboard,
      followUps: ($allFollow | unique_by(.url // .reason)),
      references: {
        manifest: $manifestPath,
        roadmapProject: "https://github.com/orgs/honua-io/projects/2",
        ownedBy: "https://github.com/honua-io/honua-devops/issues/41"
      }
    }
  '
)"

# --- Write the bundle --------------------------------------------------------

printf '%s\n' "$bundle" >"$BUNDLE_OUTPUT"

# --- Human-readable report ---------------------------------------------------

verdict="$(jq -r '.verdict' <<<"$bundle")"
release_id="$(jq -r '.releaseId // "unknown"' <<<"$bundle")"
channel="$(jq -r '.channel // "unknown"' <<<"$bundle")"
environment="$(jq -r '.environment' <<<"$bundle")"
candidate_ref="$(jq -r '.candidate.ref // "unset"' <<<"$bundle")"

echo "[INFO] release=${release_id} channel=${channel} environment=${environment} mode=${MODE}"
echo "[INFO] candidate_ref=${candidate_ref}"
echo "[INFO] required_surfaces=${REQUIRED_SURFACES}"

# Per-check lines, grouped by surface.
while IFS=$'\t' read -r state surface id reason; do
  case "$state" in
    passed) echo "[PASS] (${surface}) ${id}" ;;
    waived) echo "[WAIVED] (${surface}) ${id} — accepted via approved waiver" ;;
    *)      echo "[FAIL] (${surface}) ${id} — ${reason}" ;;
  esac
done < <(jq -r '
  .checks
  | sort_by(.surface, .id)[]
  | [ .state, .surface, .id,
      (if (.followUps | length) > 0 then (.followUps[0].reason // "blocked") else "blocked" end) ]
  | @tsv' <<<"$bundle")

# Surface coverage.
while IFS=$'\t' read -r surface covered passing; do
  if [[ "$covered" == "true" ]]; then
    echo "[COVER] surface ${surface}: covered (${passing} passing)"
  else
    echo "[GAP]   surface ${surface}: NO green/waived evidence"
  fi
done < <(jq -r '.surfaceCoverage[] | [ .surface, (.covered|tostring), (.passing|tostring) ] | @tsv' <<<"$bundle")

# Owning follow-up issues.
follow_count="$(jq -r '.followUps | length' <<<"$bundle")"
if [[ "$follow_count" -gt 0 ]]; then
  echo "[INFO] owning follow-up issues (${follow_count}):"
  jq -r '.followUps[] | "  - " + ((.url // "(no issue yet)")) + " — " + .reason' <<<"$bundle"
fi

# --- Release-notes block -----------------------------------------------------

notes="$(
  cat <<EOF
# Honua compatibility-train release-candidate validation

- release: ${release_id} (channel: ${channel})
- environment: ${environment}
- candidate ref: ${candidate_ref}
- validation mode: ${MODE}
- verdict: ${verdict}

## Surface coverage
$(jq -r '.surfaceCoverage[] | "- " + .surface + ": " + (if .covered then "covered (" + (.passing|tostring) + " passing)" else "**gap — no evidence**" end)' <<<"$bundle")

## Blocking follow-ups
$(if [[ "$follow_count" -gt 0 ]]; then
    jq -r '.followUps[] | "- " + (.url // "(no issue yet)") + " — " + .reason' <<<"$bundle"
  else
    echo "- none"
  fi)

Evidence bundle: ${BUNDLE_OUTPUT}
EOF
)"

echo "----- release-notes -----"
echo "$notes"
echo "-------------------------"
echo "[INFO] evidence bundle written to ${BUNDLE_OUTPUT}"

if [[ -n "$NOTES_OUTPUT" ]]; then
  printf '%s\n' "$notes" >"$NOTES_OUTPUT"
  echo "[INFO] release notes written to ${NOTES_OUTPUT}"
fi

# --- Verdict -----------------------------------------------------------------

if [[ "$verdict" == "pass" ]]; then
  echo "[RESULT] Release-candidate validation PASSED for ${release_id}; the train is releasable to ${environment}."
  exit 0
fi

if [[ "$MODE" == "advisory" ]]; then
  echo "[RESULT] Release-candidate validation found gaps for ${release_id} (advisory mode; not failing the run). See follow-ups above."
  exit 0
fi

echo "[RESULT] Release-candidate validation FAILED for ${release_id}; the train is NOT releasable to ${environment}. See owning follow-ups above."
exit 1
