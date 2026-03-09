# Backup And Restore Game-Day

Baseline disaster-readiness implementation for `honua-devops#7`.

## Run The Drill

```bash
./scripts/run-backup-restore-gameday.sh \
  --service-id roads-api \
  --environment staging \
  --backup-command "terraform output -raw latest_backup_id" \
  --restore-command "./scripts/restore-known-good.sh" \
  --rto-target-minutes 60 \
  --rpo-target-minutes 15 \
  --backup-age-minutes 10 \
  --notes "Weekly staging recovery drill."
```

Outputs:

- `gameday-evidence.json`
- `gameday-report.md`
- `logs/backup.log`
- `logs/restore.log`

## What Gets Tracked

- RTO target vs actual
- RPO target vs actual
- backup and restore command paths
- log capture for both phases
- pass/fail status for the drill

## Evidence Loop

1. Run the game-day script in a lower environment first.
2. Attach the evidence bundle to the weekly backlog review or release-gate review.
3. If RTO/RPO misses the target, open a remediation item immediately.
4. Re-run the drill after remediation rather than waiting for the next release cycle.

## Verification

```bash
./scripts/smoke-backup-restore-gameday.sh
```
