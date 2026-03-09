#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

WORKDIR="$(mktemp -d)"
trap 'rm -rf "$WORKDIR"' EXIT

OUTPUT_DIR="$WORKDIR/gameday"

./scripts/run-backup-restore-gameday.sh \
  --service-id roads-api \
  --environment staging \
  --output-dir "$OUTPUT_DIR" \
  --backup-command "printf backup-ok > '$WORKDIR/backup-artifact.txt'" \
  --restore-command "printf restore-ok > '$WORKDIR/restore-artifact.txt'" \
  --rto-target-minutes 5 \
  --rpo-target-minutes 15 \
  --backup-age-minutes 4 \
  --notes "Smoke coverage for backup/restore game-day."

test -f "$OUTPUT_DIR/gameday-evidence.json"
test -f "$OUTPUT_DIR/gameday-report.md"
test -f "$OUTPUT_DIR/logs/backup.log"
test -f "$OUTPUT_DIR/logs/restore.log"
python3 -m json.tool "$OUTPUT_DIR/gameday-evidence.json" >/dev/null
grep -nF -- '"status": "pass"' "$OUTPUT_DIR/gameday-evidence.json" >/dev/null
grep -nF -- '"rto_target_met": true' "$OUTPUT_DIR/gameday-evidence.json" >/dev/null
grep -nF -- '"rpo_target_met": true' "$OUTPUT_DIR/gameday-evidence.json" >/dev/null

echo "Backup/restore game-day smoke check passed."
