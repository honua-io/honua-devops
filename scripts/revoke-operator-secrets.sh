#!/usr/bin/env bash

set -euo pipefail

REPO="honua-io/honua-devops"
OUTPUT_DIR=""
DRY_RUN=false
REVOCATION_REASON="incident-revocation"
declare -a SECRET_NAMES=()

usage() {
  cat <<'EOF'
Usage:
  ./scripts/revoke-operator-secrets.sh --secret <name> [options]

Options:
  --secret <name>         Repeatable. Secret name to revoke
  --repo <owner/repo>     Default: honua-io/honua-devops
  --output-dir <path>     Evidence directory
  --dry-run               Do not call gh; write evidence only
  --reason <text>         Revocation reason
EOF
}

require_command() {
  local command_name="$1"
  if ! command -v "$command_name" >/dev/null 2>&1; then
    echo "[ERROR] required command not found: $command_name" >&2
    exit 1
  fi
}

json_escape() {
  local value="$1"
  value="${value//\\/\\\\}"
  value="${value//\"/\\\"}"
  value="${value//$'\n'/\\n}"
  value="${value//$'\r'/\\r}"
  value="${value//$'\t'/\\t}"
  printf '%s' "$value"
}

while [[ "$#" -gt 0 ]]; do
  case "$1" in
    --secret)
      SECRET_NAMES+=("$2")
      shift 2
      ;;
    --repo)
      REPO="$2"
      shift 2
      ;;
    --output-dir)
      OUTPUT_DIR="$2"
      shift 2
      ;;
    --dry-run)
      DRY_RUN=true
      shift
      ;;
    --reason)
      REVOCATION_REASON="$2"
      shift 2
      ;;
    --help)
      usage
      exit 0
      ;;
    *)
      echo "[ERROR] unknown argument: $1" >&2
      usage >&2
      exit 1
      ;;
  esac
done

if [[ "${#SECRET_NAMES[@]}" -eq 0 ]]; then
  echo "[ERROR] at least one --secret is required." >&2
  exit 1
fi

if [[ -z "$OUTPUT_DIR" ]]; then
  TIMESTAMP="$(date -u +"%Y%m%dT%H%M%SZ")"
  OUTPUT_DIR="artifacts/secret-revocation/${TIMESTAMP}"
fi

mkdir -p "$OUTPUT_DIR"

if [[ "$DRY_RUN" != "true" ]]; then
  require_command gh
fi

for secret_name in "${SECRET_NAMES[@]}"; do
  if [[ "$DRY_RUN" == "true" ]]; then
    echo "[DRY-RUN] would revoke secret: $secret_name"
  else
    gh secret delete "$secret_name" --repo "$REPO"
    echo "Revoked secret: $secret_name"
  fi
done

evidence_path="$OUTPUT_DIR/revocation-evidence.json"

{
  printf '{\n'
  printf '  "generated_at_utc": "%s",\n' "$(date -u +"%Y-%m-%dT%H:%M:%SZ")"
  printf '  "repo": "%s",\n' "$(json_escape "$REPO")"
  printf '  "dry_run": %s,\n' "$DRY_RUN"
  printf '  "revocation_reason": "%s",\n' "$(json_escape "$REVOCATION_REASON")"
  printf '  "revoked_secret_names": [\n'
  for i in "${!SECRET_NAMES[@]}"; do
    suffix=','
    if [[ "$i" -eq $((${#SECRET_NAMES[@]} - 1)) ]]; then
      suffix=''
    fi
    printf '    "%s"%s\n' "$(json_escape "${SECRET_NAMES[$i]}")" "$suffix"
  done
  printf '  ]\n'
  printf '}\n'
} >"$evidence_path"

echo "Revocation evidence written to: $evidence_path"
