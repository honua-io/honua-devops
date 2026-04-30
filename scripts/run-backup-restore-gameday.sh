#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

SERVICE_ID=""
ENVIRONMENT=""
OUTPUT_DIR=""
BACKUP_COMMAND=""
RESTORE_COMMAND=""
RTO_TARGET_MINUTES="${GAME_DAY_RTO_TARGET_MINUTES:-60}"
RPO_TARGET_MINUTES="${GAME_DAY_RPO_TARGET_MINUTES:-15}"
BACKUP_AGE_MINUTES="${GAME_DAY_BACKUP_AGE_MINUTES:-0}"
NOTES="${GAME_DAY_NOTES:-}"

usage() {
  cat <<'EOF'
Usage:
  ./scripts/run-backup-restore-gameday.sh --service-id <id> --environment <env> [options]

Options:
  --service-id <id>             Required service identifier
  --environment <env>           Required environment name
  --output-dir <path>           Evidence directory
  --backup-command <cmd>        Optional shell command that produces a backup/checkpoint
  --restore-command <cmd>       Optional shell command that restores from the checkpoint
  --rto-target-minutes <n>      Default: 60
  --rpo-target-minutes <n>      Default: 15
  --backup-age-minutes <n>      Default: 0
  --notes <text>                Optional notes for the evidence bundle
  --help                        Show this help text
EOF
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
    --service-id)
      SERVICE_ID="$2"
      shift 2
      ;;
    --environment)
      ENVIRONMENT="$2"
      shift 2
      ;;
    --output-dir)
      OUTPUT_DIR="$2"
      shift 2
      ;;
    --backup-command)
      BACKUP_COMMAND="$2"
      shift 2
      ;;
    --restore-command)
      RESTORE_COMMAND="$2"
      shift 2
      ;;
    --rto-target-minutes)
      RTO_TARGET_MINUTES="$2"
      shift 2
      ;;
    --rpo-target-minutes)
      RPO_TARGET_MINUTES="$2"
      shift 2
      ;;
    --backup-age-minutes)
      BACKUP_AGE_MINUTES="$2"
      shift 2
      ;;
    --notes)
      NOTES="$2"
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

if [[ -z "$SERVICE_ID" || -z "$ENVIRONMENT" ]]; then
  echo "[ERROR] --service-id and --environment are required." >&2
  exit 1
fi

if [[ -z "$OUTPUT_DIR" ]]; then
  TIMESTAMP="$(date -u +"%Y%m%dT%H%M%SZ")"
  OUTPUT_DIR="$REPO_ROOT/artifacts/backup-restore-gameday/${SERVICE_ID}-${ENVIRONMENT}-${TIMESTAMP}"
fi

mkdir -p "$OUTPUT_DIR/logs"

BACKUP_LOG="$OUTPUT_DIR/logs/backup.log"
RESTORE_LOG="$OUTPUT_DIR/logs/restore.log"

run_step() {
  local label="$1"
  local command_text="$2"
  local log_path="$3"
  local start end duration
  start="$(date +%s)"

  if [[ -n "$command_text" ]]; then
    bash -lc "$command_text" >"$log_path" 2>&1
  else
    printf "no-op %s command\n" "$label" >"$log_path"
  fi

  end="$(date +%s)"
  duration=$((end - start))
  echo "$duration"
}

echo "Running backup/restore game-day for $SERVICE_ID in $ENVIRONMENT"

backup_duration_seconds="$(run_step "backup" "$BACKUP_COMMAND" "$BACKUP_LOG")"
restore_duration_seconds="$(run_step "restore" "$RESTORE_COMMAND" "$RESTORE_LOG")"

rto_actual_minutes="$(awk -v seconds="$restore_duration_seconds" 'BEGIN { printf "%.2f", seconds / 60 }')"
rpo_actual_minutes="$(awk -v value="$BACKUP_AGE_MINUTES" 'BEGIN { printf "%.2f", value }')"

rto_target_met="false"
rpo_target_met="false"

if awk "BEGIN { exit !($rto_actual_minutes <= $RTO_TARGET_MINUTES) }"; then
  rto_target_met="true"
fi

if awk "BEGIN { exit !($rpo_actual_minutes <= $RPO_TARGET_MINUTES) }"; then
  rpo_target_met="true"
fi

if [[ "$rto_target_met" == "true" && "$rpo_target_met" == "true" ]]; then
  overall_status="pass"
else
  overall_status="fail"
fi

evidence_path="$OUTPUT_DIR/gameday-evidence.json"
report_path="$OUTPUT_DIR/gameday-report.md"

cat >"$evidence_path" <<EOF
{
  "generated_at_utc": "$(date -u +"%Y-%m-%dT%H:%M:%SZ")",
  "service_id": "$(json_escape "$SERVICE_ID")",
  "environment": "$(json_escape "$ENVIRONMENT")",
  "status": "$overall_status",
  "rto_target_minutes": $RTO_TARGET_MINUTES,
  "rto_actual_minutes": $rto_actual_minutes,
  "rto_target_met": $rto_target_met,
  "rpo_target_minutes": $RPO_TARGET_MINUTES,
  "rpo_actual_minutes": $rpo_actual_minutes,
  "rpo_target_met": $rpo_target_met,
  "backup_command": "$(json_escape "$BACKUP_COMMAND")",
  "restore_command": "$(json_escape "$RESTORE_COMMAND")",
  "backup_log": "logs/backup.log",
  "restore_log": "logs/restore.log",
  "notes": "$(json_escape "$NOTES")"
}
EOF

cat >"$report_path" <<EOF
# Backup And Restore Game-Day

- Service: \`$SERVICE_ID\`
- Environment: \`$ENVIRONMENT\`
- Status: \`$overall_status\`
- RTO target / actual: \`${RTO_TARGET_MINUTES}m / ${rto_actual_minutes}m\`
- RPO target / actual: \`${RPO_TARGET_MINUTES}m / ${rpo_actual_minutes}m\`

## Evidence

- \`gameday-evidence.json\`
- \`logs/backup.log\`
- \`logs/restore.log\`

## Notes

$NOTES

## Follow-Up

- If RTO or RPO failed, open remediation items for backup freshness, restore automation, or environment bootstrap drift.
- Attach this evidence bundle to the weekly operating review or release gate review.
EOF

echo "Game-day evidence written to: $OUTPUT_DIR"

if [[ "$overall_status" != "pass" ]]; then
  echo "[ERROR] backup/restore game-day failed RTO or RPO objective. Evidence: $OUTPUT_DIR" >&2
  exit 2
fi
