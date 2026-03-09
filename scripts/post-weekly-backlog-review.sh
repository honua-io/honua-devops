#!/usr/bin/env bash

set -euo pipefail

REPO="${BACKLOG_REVIEW_REPO:-honua-io/honua-devops}"
ISSUE_NUMBER="${BACKLOG_REVIEW_ISSUE:-10}"
WEEK_OF="${BACKLOG_REVIEW_WEEK_OF:-$(date +%F)}"
DRY_RUN=false

declare -a COMPLETED_ITEMS=()
declare -a NEXT_ITEMS=()
declare -a BLOCKED_ITEMS=()
declare -a SCOPE_DECISIONS=()
declare -a NOTES=()

usage() {
  cat <<'EOF'
Usage:
  ./scripts/post-weekly-backlog-review.sh [options]

Options:
  --repo <owner/repo>         Default: honua-io/honua-devops
  --issue <number>            Default: 10
  --week-of <YYYY-MM-DD>      Default: today
  --completed <text>          Repeatable
  --next <text>               Repeatable
  --blocked <text>            Repeatable
  --scope-decision <text>     Repeatable
  --note <text>               Repeatable
  --dry-run                   Print the generated comment instead of posting it
  --help                      Show this help text
EOF
}

require_command() {
  local command_name="$1"
  if ! command -v "$command_name" >/dev/null 2>&1; then
    echo "[ERROR] required command not found: $command_name" >&2
    exit 1
  fi
}

append_bullets() {
  local file_path="$1"
  local fallback="$2"
  shift 2

  if [[ "$#" -eq 0 ]]; then
    printf -- "- %s\n" "$fallback" >>"$file_path"
    return
  fi

  local item
  for item in "$@"; do
    printf -- "- %s\n" "$item" >>"$file_path"
  done
}

while [[ "$#" -gt 0 ]]; do
  case "$1" in
    --repo)
      REPO="$2"
      shift 2
      ;;
    --issue)
      ISSUE_NUMBER="$2"
      shift 2
      ;;
    --week-of)
      WEEK_OF="$2"
      shift 2
      ;;
    --completed)
      COMPLETED_ITEMS+=("$2")
      shift 2
      ;;
    --next)
      NEXT_ITEMS+=("$2")
      shift 2
      ;;
    --blocked)
      BLOCKED_ITEMS+=("$2")
      shift 2
      ;;
    --scope-decision)
      SCOPE_DECISIONS+=("$2")
      shift 2
      ;;
    --note)
      NOTES+=("$2")
      shift 2
      ;;
    --dry-run)
      DRY_RUN=true
      shift
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

tmp_file="$(mktemp)"
trap 'rm -f "$tmp_file"' EXIT

{
  printf "Weekly backlog review - %s\n\n" "$WEEK_OF"
  printf "**Backlog Review**\n"
} >>"$tmp_file"
append_bullets "$tmp_file" "Triage completed or intentionally deferred with explicit ownership."
{
  printf "\n**Next Two Weeks Ready Work**\n"
} >>"$tmp_file"
append_bullets "$tmp_file" "No ready-to-start work was recorded this week." "${NEXT_ITEMS[@]}"
{
  printf "\n**Blocked Items**\n"
} >>"$tmp_file"
append_bullets "$tmp_file" "No blockers recorded this week." "${BLOCKED_ITEMS[@]}"
{
  printf "\n**Scope Gate**\n"
} >>"$tmp_file"
append_bullets "$tmp_file" "No new scope tradeoff recorded this week." "${SCOPE_DECISIONS[@]}"
{
  printf "\n**Done/Close Hygiene**\n"
} >>"$tmp_file"
append_bullets "$tmp_file" "No completed work recorded this week." "${COMPLETED_ITEMS[@]}"
{
  printf "\n**Notes**\n"
} >>"$tmp_file"
append_bullets "$tmp_file" "Partial work should include exact remaining tasks before the next review." "${NOTES[@]}"

if [[ "$DRY_RUN" == "true" ]]; then
  cat "$tmp_file"
  exit 0
fi

require_command gh
gh issue comment "$ISSUE_NUMBER" --repo "$REPO" --body-file "$tmp_file"
