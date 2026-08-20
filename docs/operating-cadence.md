# Operating Cadence

This repo uses `honua-devops#10` as the standing weekly backlog and close-hygiene checkpoint.

## Weekly Rhythm

Once per week, post a dated review comment that captures:

- backlog triage status
- next two weeks of ready work
- blocked items and dependencies
- explicit scope tradeoffs
- what was closed
- what remains partially complete

## Checklist

### Backlog Review

- new issues are triaged with `area/*`, `priority/*`, `effort/*`, and `phase/*`
- the next two weeks have enough `ready-to-start` work
- blocked issues name the dependency or owning repo

### Scope Gate

- new scope names the tradeoff or deferral it caused
- MVP/Beta/GA mix still matches the current release goal
- oversized `effort/XL` work is split or explicitly accepted

### Done/Close Hygiene

- verified work is closed within 24 hours
- partial work gets a comment with exact remaining tasks
- stale items are rephased or closed

## Posting The Review

Use the helper script to post or preview the weekly comment:

```bash
./scripts/post-weekly-backlog-review.sh \
  --week-of 2026-03-08 \
  --completed "Closed verified operator tickets #12-#20." \
  --next "Pull #5 for SLO enforcement beyond the current baseline gate script." \
  --blocked "Live cloud evidence is still needed before closing #1 and #2." \
  --scope-decision "Keep cloud-validation evidence separate from repo-only contract work."
```

Add `--dry-run` to preview the comment without posting it.

## Default Standing Decisions

- Close an issue only after code/docs/scripts and verification all match the issue bar.
- If acceptance is met in-repo but live environment evidence is still missing, leave the issue open and comment with the exact remaining external validation step.
- Use the weekly review comment to note any cross-repo blocker that should be escalated into `honua-server` or `honua-iac`.
