#!/usr/bin/env bash

set -euo pipefail

REPO="honua-io/honua-devops"
ENV_FILE=""
OUTPUT_DIR=""
DRY_RUN=false
ROTATION_REASON="scheduled-rotation"
CADENCE="30d"

usage() {
  cat <<'EOF'
Usage:
  ./scripts/rotate-operator-secrets.sh --env-file <path> [options]

Options:
  --env-file <path>       Required env file with KEY=VALUE lines
  --repo <owner/repo>     Default: honua-io/honua-devops
  --output-dir <path>     Evidence directory
  --dry-run               Do not call gh; write evidence only
  --reason <text>         Rotation reason
  --cadence <text>        Rotation cadence label
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
    --env-file)
      ENV_FILE="$2"
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
      ROTATION_REASON="$2"
      shift 2
      ;;
    --cadence)
      CADENCE="$2"
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

if [[ -z "$ENV_FILE" ]]; then
  echo "[ERROR] --env-file is required." >&2
  exit 1
fi

if [[ -z "$OUTPUT_DIR" ]]; then
  TIMESTAMP="$(date -u +"%Y%m%dT%H%M%SZ")"
  OUTPUT_DIR="artifacts/secret-rotation/${TIMESTAMP}"
fi

mkdir -p "$OUTPUT_DIR"

if [[ "$DRY_RUN" != "true" ]]; then
  require_command gh
fi

declare -a rotated_names=()

while IFS='=' read -r key value; do
  [[ -n "${key// }" ]] || continue
  [[ "$key" =~ ^# ]] && continue
  value="${value:-}"
  rotated_names+=("$key")
  if [[ "$DRY_RUN" == "true" ]]; then
    echo "[DRY-RUN] would rotate secret: $key"
  else
    gh secret set "$key" --repo "$REPO" --body "$value"
    echo "Rotated secret: $key"
  fi
done <"$ENV_FILE"

evidence_path="$OUTPUT_DIR/rotation-evidence.json"

{
  printf '{\n'
  printf '  "generated_at_utc": "%s",\n' "$(date -u +"%Y-%m-%dT%H:%M:%SZ")"
  printf '  "repo": "%s",\n' "$(json_escape "$REPO")"
  printf '  "dry_run": %s,\n' "$DRY_RUN"
  printf '  "rotation_reason": "%s",\n' "$(json_escape "$ROTATION_REASON")"
  printf '  "cadence": "%s",\n' "$(json_escape "$CADENCE")"
  printf '  "rotated_secret_names": [\n'
  for i in "${!rotated_names[@]}"; do
    suffix=','
    if [[ "$i" -eq $((${#rotated_names[@]} - 1)) ]]; then
      suffix=''
    fi
    printf '    "%s"%s\n' "$(json_escape "${rotated_names[$i]}")" "$suffix"
  done
  printf '  ]\n'
  printf '}\n'
} >"$evidence_path"

echo "Rotation evidence written to: $evidence_path"
