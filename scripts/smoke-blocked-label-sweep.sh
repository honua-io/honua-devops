#!/usr/bin/env bash

# Offline self-test for the blocked-label sweeper (honua-devops#167).
#
# Two tiers, neither of which touches the network:
#   1. the sweeper's own --self-test, which pins the citation grammar against
#      fixture strings taken from real issue bodies;
#   2. an end-to-end sweep driven by a stub `gh` on PATH, which pins the
#      classification, the missing-label path, the rate-limit retry, the report
#      shape, and — most importantly — that the enforcement guard refuses
#      BEFORE any `gh` call is made.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
SWEEPER="$REPO_ROOT/scripts/sweep-blocked-labels.py"

require_command() {
  local command_name="$1"
  if ! command -v "$command_name" >/dev/null 2>&1; then
    echo "[ERROR] required command not found: $command_name" >&2
    exit 1
  fi
}

require_command python3

WORKDIR="$(mktemp -d)"
trap 'rm -rf "$WORKDIR"' EXIT

fail() {
  echo "[FAIL] $*" >&2
  exit 1
}

echo "== Tier 1: citation-grammar self-test =="
python3 "$SWEEPER" --self-test

echo
echo "== Tier 2: end-to-end sweep against a stub gh =="

mkdir -p "$WORKDIR/bin"
export GH_STUB_STATE="$WORKDIR/state"
mkdir -p "$GH_STUB_STATE"
: >"$GH_STUB_STATE/calls.log"

cat >"$WORKDIR/bin/gh_stub.py" <<'PY'
"""Minimal `gh` stand-in: fixture repositories, one forced rate-limit retry."""
import json
import os
import sys

state = os.environ["GH_STUB_STATE"]
args = sys.argv[1:]
with open(os.path.join(state, "calls.log"), "a", encoding="utf-8") as log:
    log.write(" ".join(args) + "\n")


def emit(payload):
    print(json.dumps(payload))
    raise SystemExit(0)


def arg_after(name, default=None):
    return args[args.index(name) + 1] if name in args else default


if args[:2] == ["auth", "status"]:
    print("Logged in to github.com as stub")
    raise SystemExit(0)

if args[:2] == ["label", "list"]:
    repo = arg_after("--repo")
    if repo == "test-org/repo-nolabel":
        emit([{"name": "bug"}])
    emit([{"name": "bug"}, {"name": "state/blocked"}, {"name": "state/ready"}])

if args[:2] == ["issue", "list"]:
    emit([
        # every cited blocker closed -> STALE
        {"number": 1, "title": "Stale one", "url": "https://x/1",
         "body": "Blocked by #100.", "updatedAt": "2026-08-01T00:00:00Z"},
        # prose blocker, no reference -> UNCITED
        {"number": 2, "title": "Prose | pipe", "url": "https://x/2",
         "body": "Blocked on the platform team's capacity.", "updatedAt": "2026-08-02T00:00:00Z"},
        # one blocker still open -> OK
        {"number": 3, "title": "Still blocked", "url": "https://x/3",
         "body": "Depends: #100, #101. Blocks: #102.", "updatedAt": "2026-08-03T00:00:00Z"},
        # citation lives only in a comment, and it is closed -> STALE
        {"number": 4, "title": "Cited in a comment", "url": "https://x/4",
         "body": "", "updatedAt": "2026-08-04T00:00:00Z"},
        # unresolvable shorthand is never evidence of closure -> OK
        {"number": 5, "title": "Unresolvable", "url": "https://x/5",
         "body": "Blocked by nosuchrepo#9", "updatedAt": "2026-08-05T00:00:00Z"},
        # a body that is not a string at all must not throw
        {"number": 6, "title": "No body", "url": "https://x/6",
         "body": None, "updatedAt": "2026-08-06T00:00:00Z"},
    ])

if args[:2] == ["issue", "view"]:
    number = args[2]
    if number == "4":
        emit({"comments": [{"body": "Rebased. Blocked by test-org/repo-b#5 now."}]})
    emit({"comments": []})

if args[0] == "api":
    route = args[1]
    number = route.rsplit("/", 1)[-1]
    if number == "101":
        # First read of the one OPEN blocker fails with a rate limit; the sweeper
        # must retry rather than record it as unresolved.
        marker = os.path.join(state, "ratelimited")
        if not os.path.exists(marker):
            open(marker, "w").close()
            print("API rate limit exceeded for user ID 1.", file=sys.stderr)
            raise SystemExit(1)
        emit({"state": "open", "title": "Open blocker"})
    if number == "9":
        print("gh: Not Found (HTTP 404)", file=sys.stderr)
        raise SystemExit(1)
    if number == "5":
        emit({"state": "closed", "title": "Merged PR", "pull_request": {"merged_at": "2026-08-01T00:00:00Z"}})
    emit({"state": "closed", "title": "Closed blocker"})

print(f"gh stub: unhandled args {args}", file=sys.stderr)
raise SystemExit(1)
PY

cat >"$WORKDIR/bin/gh" <<PY
#!/usr/bin/env bash
exec python3 "$WORKDIR/bin/gh_stub.py" "\$@"
PY
chmod +x "$WORKDIR/bin/gh"

export PATH="$WORKDIR/bin:$PATH"
export HONUA_SWEEP_RETRY_SLEEP_SECONDS=0

echo "-- enforcement guard refuses before any gh call --"
if ENFORCE_SWEEP= python3 "$SWEEPER" --enforce --repo test-org/repo-a >"$WORKDIR/enforce1.out" 2>"$WORKDIR/enforce1.err"; then
  fail "--enforce without ENFORCE_SWEEP=true should have exited non-zero"
fi
grep -q "ENFORCE_SWEEP=true is not set" "$WORKDIR/enforce1.err" \
  || fail "--enforce refusal did not name ENFORCE_SWEEP"

if ENFORCE_SWEEP=true python3 "$SWEEPER" --enforce --repo test-org/repo-a >"$WORKDIR/enforce2.out" 2>"$WORKDIR/enforce2.err"; then
  fail "--enforce with ENFORCE_SWEEP=true should still refuse in this slice"
fi
grep -q "enforcement is not implemented" "$WORKDIR/enforce2.err" \
  || fail "--enforce refusal did not name the unimplemented mutation path"

if [[ -s "$GH_STUB_STATE/calls.log" ]]; then
  fail "the enforcement guard let a gh call through: $(cat "$GH_STUB_STATE/calls.log")"
fi
echo "[PASS] both --enforce paths refuse, with zero gh calls"

echo "-- dry-run sweep classifies the fixture repositories --"
python3 "$SWEEPER" \
  --repo test-org/repo-a \
  --repo test-org/repo-nolabel \
  --output "$WORKDIR/report.md" \
  --json-out "$WORKDIR/report.json" \
  >"$WORKDIR/sweep.out" 2>"$WORKDIR/sweep.err"

grep -q "gh transient failure, retrying" "$WORKDIR/sweep.err" \
  || fail "the forced rate limit did not trigger a retry"

python3 - "$WORKDIR/report.json" <<'PY'
import json
import sys

report = json.loads(open(sys.argv[1], encoding="utf-8").read())
repos = {entry["repo"]: entry for entry in report["repos"]}

expected = {1: "STALE", 2: "UNCITED", 3: "OK", 4: "STALE", 5: "OK", 6: "UNCITED"}
actual = {issue["number"]: issue["class"] for issue in repos["test-org/repo-a"]["issues"]}
assert actual == expected, f"classification mismatch: {actual} != {expected}"

# #3 must have collected exactly its two Depends: refs, never the Blocks: one.
cited = sorted(c["number"] for c in
               next(i for i in repos["test-org/repo-a"]["issues"] if i["number"] == 3)["citations"])
assert cited == [100, 101], f"direction handling broke: {cited}"

# #5's unresolvable shorthand must be recorded, not dropped.
five = next(i for i in repos["test-org/repo-a"]["issues"] if i["number"] == 5)
assert [c["state"] for c in five["citations"]] == ["unresolved"], five["citations"]

# The merged PR cited from a comment counts as closed.
four = next(i for i in repos["test-org/repo-a"]["issues"] if i["number"] == 4)
assert four["citations"][0]["state"] == "merged", four["citations"]
assert four["citations"][0]["origin"].startswith("comment"), four["citations"]

missing = repos["test-org/repo-nolabel"]
assert missing["labelPresent"] is False, missing
assert missing["issues"] == [], missing
assert any("not defined" in err for err in missing["errors"]), missing

totals = report["totals"]
assert (totals["STALE"], totals["UNCITED"], totals["OK"], totals["ERROR"]) == (2, 2, 2, 0), totals
assert report["mode"] == "dry-run", report["mode"]
print("[PASS] classification, direction, unresolved handling, missing label, totals")
PY

grep -q "^# Blocked-label sweep" "$WORKDIR/report.md" || fail "report is missing its heading"
grep -q "Prose \\\\| pipe" "$WORKDIR/report.md" || fail "a pipe in an issue title was not escaped"
grep -q "Nothing was mutated" "$WORKDIR/report.md" || fail "report is missing the dry-run footer"
echo "[PASS] markdown report shape"

echo "-- --fail-on-findings gates, plain dry run does not --"
if ! python3 "$SWEEPER" --repo test-org/repo-a >/dev/null 2>&1; then
  fail "a plain dry run with findings must still exit 0"
fi
if python3 "$SWEEPER" --repo test-org/repo-a --fail-on-findings >/dev/null 2>&1; then
  fail "--fail-on-findings must exit non-zero when findings exist"
fi
echo "[PASS] exit-code contract"

echo "-- invalid input is refused, not swept --"
if python3 "$SWEEPER" --repo not-a-repo >/dev/null 2>&1; then
  fail "a malformed --repo should be refused"
fi
echo "[PASS] argument validation"

echo
echo "[RESULT] blocked-label sweeper smoke passed"
