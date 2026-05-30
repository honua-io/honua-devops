#!/usr/bin/env bash

# Honua Console preview-environment planner.
#
# Plans an ephemeral preview/staging deployment of the unified Console runtime
# from a branch or release candidate. Per the repo's default-safe posture this is
# a PLAN-only tool: it derives a deterministic preview descriptor (namespace,
# hostname, artifact ref, TTL) and emits it as JSON for downstream GitOps. It does
# not apply manifests, submit, or roll back.
#
# A preview is only emitted once the release gate evidence is satisfied, so a
# failing Console smoke cannot spin up a promotable preview. Pass --skip-gate only
# for descriptor-shape debugging.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

REF="${CONSOLE_PREVIEW_REF:-}"
KIND="${CONSOLE_PREVIEW_KIND:-branch}" # branch | release-candidate
ARTIFACT_VERSION="${CONSOLE_ARTIFACT_VERSION:-}"
ARTIFACT_REPOSITORY="${CONSOLE_ARTIFACT_REPOSITORY:-ghcr.io/honua-io/honua-console}"
PREVIEW_DOMAIN="${CONSOLE_PREVIEW_DOMAIN:-preview.honua.dev}"
PREVIEW_TTL_HOURS="${CONSOLE_PREVIEW_TTL_HOURS:-72}"
EVIDENCE_FILE="${CONSOLE_EVIDENCE_FILE:-}"
OUTPUT="${CONSOLE_PREVIEW_OUTPUT:-}"
SKIP_GATE="false"

usage() {
  cat <<'EOF'
Usage:
  scripts/console-preview-env.sh --ref <branch-or-tag> [options]

Options:
  --ref <value>            Branch name or release-candidate tag (required).
  --kind <value>           branch | release-candidate. Default: branch
  --version <value>        Console artifact version/tag to deploy.
  --evidence <file>        Console CI evidence JSON to gate on (recommended).
  --output <file>          Write the preview descriptor JSON to this path.
  --skip-gate              Emit descriptor without running the release gate.
  --help                   Show help.

Environment overrides: CONSOLE_PREVIEW_REF, CONSOLE_PREVIEW_KIND,
  CONSOLE_ARTIFACT_VERSION, CONSOLE_ARTIFACT_REPOSITORY, CONSOLE_PREVIEW_DOMAIN,
  CONSOLE_PREVIEW_TTL_HOURS, CONSOLE_EVIDENCE_FILE, CONSOLE_PREVIEW_OUTPUT.
EOF
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --ref) REF="${2:-}"; shift 2 ;;
    --kind) KIND="${2:-}"; shift 2 ;;
    --version) ARTIFACT_VERSION="${2:-}"; shift 2 ;;
    --evidence) EVIDENCE_FILE="${2:-}"; shift 2 ;;
    --output) OUTPUT="${2:-}"; shift 2 ;;
    --skip-gate) SKIP_GATE="true"; shift ;;
    --help | -h) usage; exit 0 ;;
    *) echo "[ERROR] Unknown arg: $1" >&2; usage; exit 1 ;;
  esac
done

require_command() {
  if ! command -v "$1" >/dev/null 2>&1; then
    echo "[ERROR] Required command missing: $1" >&2
    exit 1
  fi
}

require_command jq

if [[ -z "$REF" ]]; then
  echo "[ERROR] --ref (or CONSOLE_PREVIEW_REF) is required" >&2
  usage
  exit 1
fi

case "$KIND" in
  branch | release-candidate) ;;
  *)
    echo "[ERROR] --kind must be 'branch' or 'release-candidate' (got: $KIND)" >&2
    exit 1
    ;;
esac

# Deterministic, DNS-safe slug from the ref.
slug="$(printf '%s' "$REF" | tr '[:upper:]' '[:lower:]' | tr -c 'a-z0-9' '-')"
slug="${slug##-}"
slug="${slug%%-}"
# Collapse repeated dashes.
while [[ "$slug" == *--* ]]; do slug="${slug//--/-}"; done
if [[ -z "$slug" ]]; then
  echo "[ERROR] ref '$REF' produced an empty slug" >&2
  exit 1
fi
# Keep namespaces short enough for Kubernetes (63 char limit, leave headroom).
short_slug="${slug:0:40}"
short_slug="${short_slug%%-}"

NAMESPACE="console-preview-${short_slug}"
HOSTNAME="${short_slug}.${PREVIEW_DOMAIN}"
[[ -z "$ARTIFACT_VERSION" ]] && ARTIFACT_VERSION="preview-${short_slug}"

# Gate the preview on Console CI evidence unless explicitly skipped.
if [[ "$SKIP_GATE" != "true" ]]; then
  if [[ -z "$EVIDENCE_FILE" ]]; then
    echo "[ERROR] release gate requires --evidence <file> (or pass --skip-gate)." >&2
    exit 1
  fi
  echo "[INFO] Running Console release gate against evidence: $EVIDENCE_FILE"
  CONSOLE_EVIDENCE_FILE="$EVIDENCE_FILE" \
    CONSOLE_ENVIRONMENT="preview" \
    CONSOLE_GATE_MODE="preview" \
    CONSOLE_ARTIFACT_VERSION="$ARTIFACT_VERSION" \
    "$SCRIPT_DIR/console-release-gate.sh"
fi

descriptor="$(jq -n \
  --arg ref "$REF" \
  --arg kind "$KIND" \
  --arg namespace "$NAMESPACE" \
  --arg hostname "$HOSTNAME" \
  --arg repository "$ARTIFACT_REPOSITORY" \
  --arg version "$ARTIFACT_VERSION" \
  --argjson ttl "$PREVIEW_TTL_HOURS" \
  '{
    apiVersion: "honua.io/v1alpha1",
    kind: "ConsolePreviewEnvironment",
    metadata: {
      name: $namespace,
      labels: { "managed-by": "honua-devops", surface: "console" }
    },
    spec: {
      mode: "plan",
      source: { kind: $kind, ref: $ref },
      runtime: "unified-console",
      artifact: { repository: $repository, version: $version },
      namespace: $namespace,
      hostname: $hostname,
      ttlHours: $ttl,
      submitImmediately: false
    }
  }')"

echo "----- preview-descriptor -----"
printf '%s\n' "$descriptor"
echo "------------------------------"

if [[ -n "$OUTPUT" ]]; then
  printf '%s\n' "$descriptor" >"$OUTPUT"
  echo "[INFO] preview descriptor written to $OUTPUT"
fi

echo "[RESULT] Console preview environment planned (plan-only) for ${KIND} '${REF}' -> ${HOSTNAME}"
