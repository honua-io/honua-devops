#!/usr/bin/env bash

# Honua release-candidate validation AGGREGATOR for the cross-repo compatibility
# train (honua-devops#41).
#
# The compatibility-train RC validation is built from layers that already exist
# in this repo and each answer one question:
#
#   1. conformance-gate  (scripts/compat-train-conformance-gate.sh, #68)
#        PRODUCER: runs every consumer SDK's shared-fixture conformance against
#        the candidate and emits per-repo evidence (repos.<repo>.{status,...}).
#   2. release-gate      (scripts/compat-train-release-gate.sh, #65)
#        Evaluates that per-repo evidence and proves the runs were LIVE, not the
#        seeded local fallback (the honua-sdk-python#53 trap).
#   3. release-validation (scripts/compat-train-release-validation.sh, #41)
#        Evaluates the canonical release-train MANIFEST: every required surface
#        (server, sdk, admin, helm, terraform) is green/waived, with the owning
#        follow-up for each gap.
#   4. live-probe        (scripts/compat-train-live-probe.sh, #41)
#        Actively RE-VERIFIES the signals the manifest cites (re-fetches every
#        cited GitHub run conclusion, smokes the server/helm surfaces when wired)
#        and never fakes a green it did not verify.
#
# Those four run as separate workflow jobs. This aggregator is the orchestration
# glue #41 asks for: it consumes the bundles the four layers emit and folds them
# into ONE machine-readable release-candidate evidence bundle with a single
# overall verdict and a single de-duplicated list of owning follow-up issues, so
# the RC decision and its evidence are one artifact that can be attached to the
# release gate / roadmap Project.
#
# It REUSES the existing layers' outputs; it does not re-implement any check.
#
# Default-safe posture (per AGENTS.md): this aggregator only reads the layers'
# bundles and emits a verdict + evidence bundle. It never deploys, promotes,
# submits, or rolls back. In the default `live` mode an unreleasable train exits
# non-zero; `advisory` mode reports but exits 0.

set -euo pipefail

# --- Configuration -----------------------------------------------------------

# Bundles emitted by the upstream layers. Any that is absent is treated as a
# "not-run" layer and degrades the verdict honestly (a missing layer is a gap,
# never a silent pass) unless it is explicitly marked optional below.
CONFORMANCE_EVIDENCE="${COMPAT_TRAIN_CONFORMANCE_EVIDENCE:-}"
RELEASE_GATE_RESULT="${COMPAT_TRAIN_RELEASE_GATE_RESULT:-}"   # pass|fail (the gate exit verdict)
VALIDATION_BUNDLE="${COMPAT_TRAIN_VALIDATION_BUNDLE:-}"
PROBE_BUNDLE="${COMPAT_TRAIN_PROBE_BUNDLE:-}"

# Which layers are required for an overall "releasable" verdict. The probe layer
# is advisory-by-default (it is mostly BLOCKED on un-provisioned infra), so it is
# NOT required by default; set to "true" to require it.
REQUIRE_PROBE="${COMPAT_TRAIN_RC_REQUIRE_PROBE:-false}"

# live     : an unreleasable train (any required layer not green) exits 1.
# advisory : emit the bundle, always exit 0 (report-only).
MODE="${COMPAT_TRAIN_RC_MODE:-live}"

# Where to write the aggregated RC evidence bundle + release-notes block.
BUNDLE_OUTPUT="${COMPAT_TRAIN_RC_BUNDLE_OUTPUT:-rc-validation-bundle.json}"
NOTES_OUTPUT="${COMPAT_TRAIN_RC_NOTES_OUTPUT:-}"

# Labels for the bundle header (fall back to the validation bundle when present).
RELEASE_ID="${COMPAT_TRAIN_RELEASE_ID:-}"
ENVIRONMENT="${COMPAT_TRAIN_ENVIRONMENT:-}"

# --- Helpers -----------------------------------------------------------------

require_command() {
  if ! command -v "$1" >/dev/null 2>&1; then
    echo "[ERROR] Required command missing: $1" >&2
    exit 1
  fi
}

require_command jq

# read_json_or_null <path> -> the file's JSON, or the literal null when absent.
read_json_or_null() {
  local path="$1"
  if [[ -n "$path" && -f "$path" ]]; then
    if ! jq -e . "$path" >/dev/null 2>&1; then
      echo "[ERROR] Not valid JSON: $path" >&2
      exit 1
    fi
    cat "$path"
  else
    echo "null"
  fi
}

is_truthy() {
  case "${1,,}" in
    1 | true | yes | on) return 0 ;;
    *) return 1 ;;
  esac
}

# --- Load layer bundles ------------------------------------------------------

conformance_json="$(read_json_or_null "$CONFORMANCE_EVIDENCE")"
validation_json="$(read_json_or_null "$VALIDATION_BUNDLE")"
probe_json="$(read_json_or_null "$PROBE_BUNDLE")"

# Normalize the release-gate result to pass|fail|missing.
case "$(echo "${RELEASE_GATE_RESULT}" | tr '[:upper:]' '[:lower:]')" in
  pass | passed | success | ok | green | true | 0) gate_result="pass" ;;
  "" ) gate_result="missing" ;;
  *) gate_result="fail" ;;
esac

# --- Build the aggregated bundle ---------------------------------------------

bundle="$(
  jq -n \
    --argjson conformance "$conformance_json" \
    --argjson validation "$validation_json" \
    --argjson probe "$probe_json" \
    --arg gateResult "$gate_result" \
    --arg requireProbe "$REQUIRE_PROBE" \
    --arg mode "$MODE" \
    --arg releaseId "$RELEASE_ID" \
    --arg environment "$ENVIRONMENT" '

    # --- per-layer status -----------------------------------------------------

    # conformance: derived from the per-repo evidence the producer emitted. A
    # layer that was not run is "missing"; any repo not "pass"/"skipped" fails it.
    (if $conformance == null then
        { name: "conformance", status: "missing", detail: "conformance-gate evidence not supplied" }
      else
        ([ $conformance.repos // {} | to_entries[] ]) as $repos |
        ([ $repos[] | select((.value.status // "missing") as $s
            | $s != "pass" and $s != "skipped") ]) as $bad |
        { name: "conformance",
          status: (if ($bad | length) > 0 then "fail" else "pass" end),
          repos: ($repos | length),
          failing: ($bad | map(.key)),
          detail: (if ($bad | length) > 0
            then "consumer(s) not green: " + ($bad | map(.key) | join(", "))
            else "all consumers green against the candidate" end) }
      end) as $confLayer |

    # release-gate: the live-evidence verdict (already computed by the gate).
    ({ name: "release-gate", status: $gateResult,
       detail: (if $gateResult == "pass" then "per-repo evidence is live (not seeded local fallback)"
                elif $gateResult == "missing" then "release-gate verdict not supplied"
                else "per-repo live-evidence gate blocked the train" end) }) as $gateLayer |

    # release-validation: the manifest verdict (already computed).
    (if $validation == null then
        { name: "release-validation", status: "missing", detail: "manifest validation bundle not supplied" }
      else
        { name: "release-validation",
          status: ($validation.verdict // "missing"),
          surfacesMissing: ($validation.summary.surfacesMissing // []),
          detail: ("manifest verdict=" + ($validation.verdict // "?")
            + (if (($validation.summary.surfacesMissing // []) | length) > 0
               then "; uncovered surfaces: " + ($validation.summary.surfacesMissing | join(", "))
               else "" end)) }
      end) as $valLayer |

    # live-probe: verified iff it has >=1 passed probe and zero failed probes.
    # A probe layer that is all-blocked is "blocked" (an honest gap), not green.
    (if $probe == null then
        { name: "live-probe", status: "missing", detail: "live-probe bundle not supplied" }
      else
        (($probe.summary.passed // 0)) as $p |
        (($probe.summary.failed // 0)) as $f |
        { name: "live-probe",
          status: (if $f > 0 then "fail" elif $p > 0 then "pass" else "blocked" end),
          passed: $p, failed: $f, blocked: ($probe.summary.blocked // 0),
          detail: ("probes: " + ($p|tostring) + " passed, " + ($f|tostring)
            + " failed, " + (($probe.summary.blocked // 0)|tostring) + " blocked") }
      end) as $probeLayer |

    ([ $confLayer, $gateLayer, $valLayer, $probeLayer ]) as $layers |

    # --- which layers are required for "releasable" ---------------------------
    # conformance, release-gate and release-validation are always required;
    # live-probe is required only when COMPAT_TRAIN_RC_REQUIRE_PROBE=true.
    (["conformance", "release-gate", "release-validation"]
      + (if $requireProbe == "true" then ["live-probe"] else [] end)) as $required |

    ([ $layers[] | select(.name as $n | $required | index($n))
       | select(.status != "pass") ]) as $blockingLayers |

    (($blockingLayers | length) == 0) as $releasable |

    # --- unified follow-ups ---------------------------------------------------
    # Pull the manifest validator owning follow-ups (the richest source),
    # add the failing conformance consumers, and add any required layer that is
    # missing/blocked so every gap has an owner in one place.
    ( ($validation.followUps // [])
      + [ $confLayer | select(.status == "fail") | .failing[]?
          | { url: null, repo: ("honua-io/" + .), number: null,
              reason: ("conformance: consumer " + . + " breaks against the candidate") } ]
      + [ $blockingLayers[]
          | select(.status == "missing" or .status == "blocked")
          | { url: null, repo: null, number: null,
              reason: (.name + " layer is " + .status + " — " + (.detail // "no detail")) } ]
    ) as $allFollow |

    {
      schemaVersion: 1,
      kind: "compat-train-rc-validation",
      releaseId: (if $releaseId != "" then $releaseId
                  else ($validation.releaseId // $conformance.candidate.version // "unknown") end),
      channel: ($validation.channel // null),
      environment: (if $environment != "" then $environment
                    else ($validation.environment // $conformance.environment // "preview") end),
      candidateRef: ($validation.candidate.ref // $probe.candidateRef // null),
      candidateImage: ($conformance.candidate.image // $validation.candidate.image // null),
      mode: $mode,
      verdict: (if $releasable then "releasable" else "blocked" end),
      requiredLayers: $required,
      layers: $layers,
      summary: {
        layers: ($layers | length),
        passed: ($layers | map(select(.status == "pass")) | length),
        blocking: ($blockingLayers | length),
        blockingLayers: ($blockingLayers | map(.name))
      },
      followUps: ($allFollow | unique_by((.url // "") + "|" + (.reason // ""))),
      sources: {
        conformanceEvidence: ($conformance != null),
        releaseGateResult: $gateResult,
        validationBundle: ($validation != null),
        probeBundle: ($probe != null)
      },
      references: {
        ownedBy: "https://github.com/honua-io/honua-devops/issues/41",
        roadmapProject: "https://github.com/orgs/honua-io/projects/2",
        producer: "scripts/compat-train-conformance-gate.sh",
        liveEvidenceGate: "scripts/compat-train-release-gate.sh",
        manifestValidator: "scripts/compat-train-release-validation.sh",
        liveProbe: "scripts/compat-train-live-probe.sh"
      }
    }
  '
)"

printf '%s\n' "$bundle" >"$BUNDLE_OUTPUT"

# --- Human-readable report ---------------------------------------------------

verdict="$(jq -r '.verdict' <<<"$bundle")"
release_id="$(jq -r '.releaseId' <<<"$bundle")"
environment="$(jq -r '.environment' <<<"$bundle")"

echo "[INFO] release=${release_id} environment=${environment} mode=${MODE}"
echo "[INFO] required_layers=$(jq -r '.requiredLayers | join(" ")' <<<"$bundle")"

while IFS=$'\t' read -r name status detail; do
  case "$status" in
    pass)    echo "[PASS]    ${name} — ${detail}" ;;
    blocked) echo "[BLOCKED] ${name} — ${detail}" ;;
    missing) echo "[MISSING] ${name} — ${detail}" ;;
    *)       echo "[FAIL]    ${name} — ${detail}" ;;
  esac
done < <(jq -r '.layers[] | [ .name, .status, (.detail // "") ] | @tsv' <<<"$bundle")

follow_count="$(jq -r '.followUps | length' <<<"$bundle")"
if [[ "$follow_count" -gt 0 ]]; then
  echo "[INFO] owning follow-ups (${follow_count}):"
  jq -r '.followUps[] | "  - " + ((.url // "(no issue yet)")) + " — " + .reason' <<<"$bundle"
fi

# --- Release-notes block -----------------------------------------------------

notes="$(
  cat <<EOF
# Honua compatibility-train release-candidate validation

- release: ${release_id}
- environment: ${environment}
- mode: ${MODE}
- verdict: ${verdict}

## Layer verdicts
$(jq -r '.layers[] | "- " + .name + ": " + .status + " — " + (.detail // "")' <<<"$bundle")

## Owning follow-ups
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
echo "[INFO] aggregated RC evidence bundle written to ${BUNDLE_OUTPUT}"

if [[ -n "$NOTES_OUTPUT" ]]; then
  printf '%s\n' "$notes" >"$NOTES_OUTPUT"
  echo "[INFO] release notes written to ${NOTES_OUTPUT}"
fi

# --- Verdict -----------------------------------------------------------------

if [[ "$verdict" == "releasable" ]]; then
  echo "[RESULT] Release-candidate validation: train ${release_id} is RELEASABLE to ${environment} (all required layers green)."
  exit 0
fi

if [[ "$MODE" == "advisory" ]]; then
  echo "[RESULT] Release-candidate validation found blocking layers for ${release_id} (advisory mode; not failing the run). See follow-ups above."
  exit 0
fi

echo "[RESULT] Release-candidate validation: train ${release_id} is BLOCKED for ${environment}. Blocking layers: $(jq -r '.summary.blockingLayers | join(", ")' <<<"$bundle"). See owning follow-ups above."
exit 1
