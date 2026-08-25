#!/usr/bin/env python3
"""Re-verify `state/blocked` labels against the live state of their cited blockers.

State that no machine re-verifies rots. This is the blocked-label half of the
execution-state hygiene sweep (honua-devops#167): for every open issue carrying
the blocked label in each target repository it parses the blocker citations out
of the body and comments, resolves each cited issue/PR through `gh`, and
classifies the label as STALE, UNCITED, OK, or ERROR.

The convention it enforces is `docs/blocked-label-convention.md`. In one line: a
`state/blocked` label is valid only alongside an explicit blocker reference
(`owner/repo#N`, an org shorthand such as `server#3412`, a bare `#N`, or a full
GitHub URL) introduced by a "Blocked by" / "Blocked on" / "Depends on" marker.

Safety posture:

*   Dry run is the default and the only implemented mode. It reads, prints a
    markdown report, and exits 0 whether or not it found anything.
*   `--enforce` is a guard, not a feature. It refuses unless `ENFORCE_SWEEP=true`
    is also set, and even then refuses, because this slice has no mutation path.
*   An unresolvable citation is never counted as closed, so the sweep can only
    ever argue for keeping a blocked label on incomplete evidence.

Exit codes: 0 the sweep completed, 1 findings were present and `--fail-on-findings`
was requested (or the self-test failed), 2 usage/guard refusal, 3 the sweep could
not run at all (no `gh`, not authenticated).
"""

from __future__ import annotations

import argparse
import json
import os
import re
import shutil
import subprocess
import sys
import time
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

DEFAULT_REPOS = ["honua-io/honua-devops", "honua-io/honua-studio"]
DEFAULT_LABEL = "state/blocked"
DEFAULT_READY_LABEL = "state/ready"
DEFAULT_ORG = "honua-io"

CLASS_STALE = "STALE"
CLASS_UNCITED = "UNCITED"
CLASS_OK = "OK"
CLASS_ERROR = "ERROR"
CLASS_ORDER = [CLASS_STALE, CLASS_UNCITED, CLASS_OK, CLASS_ERROR]

# Repositories in the org whose short names appear as citation shorthand. Both the
# full name and the `honua-`-stripped short form resolve; extend at runtime with
# --repo-alias or HONUA_SWEEP_REPO_ALIASES rather than editing this list for a
# one-off.
KNOWN_REPOS = [
    "agent-delivery-spec",
    "geobench",
    "geospatial-grpc",
    "geospatial-mcp",
    "honua-agentflow",
    "honua-collect",
    "honua-compliance",
    "honua-console",
    "honua-demo-infra",
    "honua-devops",
    "honua-esri-compat",
    "honua-evidence",
    "honua-gis-llm",
    "honua-helm",
    "honua-iac",
    "honua-marketplace",
    "honua-migrate",
    "honua-mobile",
    "honua-portal",
    "honua-qgis-plugin",
    "honua-release",
    "honua-samples",
    "honua-sdk-dotnet",
    "honua-sdk-js",
    "honua-sdk-python",
    "honua-server",
    "honua-server-admin",
    "honua-site",
    "honua-studio",
    "honua-support",
]

# A citation run opens at one of these. `-`/whitespace tolerated inside the phrase
# so "Blocked-by" and "Blocked  by" both read.
MARKER_RE = re.compile(
    r"(?<![A-Za-z0-9])"
    r"(?:blocked[\s\-]*(?:by|on)"
    r"|depend(?:s|ent)?[\s\-]*(?:on|upon)?"
    r"|dependenc(?:y|ies)"
    r"|blocker[s]?)"
    r"\b[\s:\-]*",
    re.IGNORECASE,
)

# ...and closes at a reverse-direction word, so "Depends: a, b. Blocks: c" does not
# swallow `c` as a blocker. The lookahead is what keeps prose out of it: only a
# reverse word that actually introduces something — "Blocks:", "Blocks #41" —
# terminates the run, while "#40 — blocking for the live lane" reads as prose and
# does not truncate the reference list that follows it.
STOP_RE = re.compile(
    r"(?<![A-Za-z0-9\-])"
    r"(?:blocks|blocking|supersed\w*|closes|fixes|resolves)"
    r"\b\s*(?=[:\-–—]|https?://|[A-Za-z0-9._\-/]*#\d)",
    re.IGNORECASE,
)

URL_REF_RE = re.compile(
    r"https?://(?:www\.)?github\.com/"
    r"([A-Za-z0-9][A-Za-z0-9\-_.]*)/([A-Za-z0-9][A-Za-z0-9\-_.]*)/"
    r"(?:issues|pull)/(\d+)",
    re.IGNORECASE,
)

# `owner/repo#N`, `repo#N`, or `#N`. The lookbehind keeps `foo#1` inside a URL path
# or an anchor from re-matching after URLs have been consumed.
SHORT_REF_RE = re.compile(
    r"(?<![A-Za-z0-9_.\-/])"
    r"(?:(?P<owner>[A-Za-z0-9][A-Za-z0-9\-_.]*)/)?"
    r"(?P<repo>[A-Za-z0-9][A-Za-z0-9\-_.]*)?"
    r"#(?P<number>\d+)\b"
)

# Lines that can continue a marker line whose own text carried no reference, e.g.
# "Blocked by:" followed by a bullet list. Bounded so a marker never eats a section.
CONTINUATION_LOOKAHEAD = 6

RETRY_SLEEPS_SECONDS = [5, 20, 60]
RETRYABLE_MARKERS = (
    "rate limit",
    "secondary rate",
    "abuse detection",
    "was submitted too quickly",
    "server error",
    "502 bad gateway",
    "503 service unavailable",
    "504 gateway",
    "connection reset",
    "timeout",
)


class SweepError(RuntimeError):
    """The sweep cannot run at all (missing tool, no auth)."""


# --------------------------------------------------------------------------------------
# Citation parsing (pure — exercised by --self-test, no network)
# --------------------------------------------------------------------------------------


def build_alias_map(extra: list[str] | None = None, org: str = DEFAULT_ORG) -> dict[str, str]:
    """Map citation shorthand -> `owner/repo`, lowercase keys."""
    aliases: dict[str, str] = {}
    for name in KNOWN_REPOS:
        full = f"{org}/{name}"
        aliases[name.lower()] = full
        if name.lower().startswith("honua-"):
            aliases[name[len("honua-"):].lower()] = full

    env_extra = os.environ.get("HONUA_SWEEP_REPO_ALIASES", "").strip()
    raw_extra = [item for item in env_extra.split(",") if item.strip()]
    raw_extra.extend(extra or [])
    for item in raw_extra:
        if "=" not in item:
            raise SystemExit(f"[ERROR] invalid --repo-alias {item!r}: expected short=owner/repo")
        short, _, target = item.partition("=")
        short = short.strip().lower()
        target = target.strip()
        if not short or "/" not in target:
            raise SystemExit(f"[ERROR] invalid --repo-alias {item!r}: expected short=owner/repo")
        aliases[short] = target
    return aliases


def _citation_regions(text: str) -> list[str]:
    """Return the text runs that follow a blocker marker."""
    lines = text.splitlines()
    regions: list[str] = []
    for index, line in enumerate(lines):
        for marker in MARKER_RE.finditer(line):
            tail = line[marker.end():]
            stop = STOP_RE.search(tail)
            region = tail[: stop.start()] if stop else tail
            if _has_reference(region):
                regions.append(region)
                continue
            # A marker line with no reference of its own may introduce a list.
            regions.extend(_continuation_regions(lines, index))
    return regions


def _continuation_regions(lines: list[str], start: int) -> list[str]:
    """Collect references from the lines a bare marker line introduces.

    `## Dependencies` followed by a blank line and a bullet list is the shape most
    of the corpus actually uses, so blank lines are skipped *before* the first
    reference is found and terminate the run *after* it. A markdown heading always
    terminates: a marker never reaches into the next section.
    """
    collected: list[str] = []
    for line in lines[start + 1: start + 1 + CONTINUATION_LOOKAHEAD]:
        stripped = line.strip()
        if stripped.startswith("#"):
            break
        if not stripped:
            if collected:
                break
            continue
        stop = STOP_RE.search(stripped)
        region = stripped[: stop.start()] if stop else stripped
        if not _has_reference(region):
            break
        collected.append(region)
        if stop:
            break
    return collected


def _has_reference(region: str) -> bool:
    return bool(URL_REF_RE.search(region) or SHORT_REF_RE.search(region))


def parse_citations(text: str | None, source_repo: str, aliases: dict[str, str], origin: str = "body") -> list[dict[str, Any]]:
    """Extract blocker citations. Never raises on malformed input."""
    if not text:
        return []
    try:
        regions = _citation_regions(text)
    except Exception:  # defensive: a parser crash must never fail a sweep
        return []

    citations: list[dict[str, Any]] = []
    for region in regions:
        for match in URL_REF_RE.finditer(region):
            owner, repo, number = match.group(1), match.group(2), match.group(3)
            citations.append(
                _citation(f"{owner}/{repo}", int(number), match.group(0), "url", origin)
            )
        # Blank out URLs so their `/N` path segments cannot be re-read as refs.
        remainder = URL_REF_RE.sub(lambda m: " " * len(m.group(0)), region)
        for match in SHORT_REF_RE.finditer(remainder):
            owner = match.group("owner")
            repo = match.group("repo")
            number = int(match.group("number"))
            raw = match.group(0)
            if owner and repo:
                citations.append(_citation(f"{owner}/{repo}", number, raw, "qualified", origin))
            elif repo:
                resolved = aliases.get(repo.lower())
                if resolved:
                    citations.append(_citation(resolved, number, raw, "alias", origin))
                else:
                    citations.append(
                        _citation(None, number, raw, "alias", origin, unresolved="unknown-repo-alias")
                    )
            else:
                citations.append(_citation(source_repo, number, raw, "bare", origin))

    return _dedupe(citations)


def _citation(
    repo: str | None,
    number: int,
    raw: str,
    kind: str,
    origin: str,
    unresolved: str | None = None,
) -> dict[str, Any]:
    return {
        "repo": repo,
        "number": number,
        "raw": raw.strip(),
        "kind": kind,
        "origin": origin,
        "unresolved": unresolved,
    }


def _dedupe(citations: list[dict[str, Any]]) -> list[dict[str, Any]]:
    seen: set[tuple[str | None, int]] = set()
    unique: list[dict[str, Any]] = []
    for citation in citations:
        key = (citation["repo"], citation["number"])
        if key in seen:
            continue
        seen.add(key)
        unique.append(citation)
    return unique


# --------------------------------------------------------------------------------------
# gh access
# --------------------------------------------------------------------------------------


def _retry_sleep(attempt: int) -> None:
    override = os.environ.get("HONUA_SWEEP_RETRY_SLEEP_SECONDS")
    if override is not None:
        try:
            seconds = float(override)
        except ValueError:
            seconds = 0.0
    else:
        seconds = float(RETRY_SLEEPS_SECONDS[min(attempt, len(RETRY_SLEEPS_SECONDS) - 1)])
    if seconds > 0:
        time.sleep(seconds)


def run_gh(args: list[str], *, attempts: int = 4) -> tuple[bool, str, str]:
    """Run `gh` and return (ok, stdout, stderr). Retries transient/rate-limit failures."""
    last_stdout = ""
    last_stderr = ""
    for attempt in range(attempts):
        try:
            completed = subprocess.run(
                ["gh", *args],
                capture_output=True,
                text=True,
                check=False,
            )
        except OSError as exc:  # gh vanished mid-run
            return False, "", str(exc)
        if completed.returncode == 0:
            return True, completed.stdout, completed.stderr
        last_stdout, last_stderr = completed.stdout, completed.stderr
        blob = f"{completed.stdout}\n{completed.stderr}".lower()
        if attempt + 1 < attempts and any(marker in blob for marker in RETRYABLE_MARKERS):
            print(f"[WARN] gh transient failure, retrying: {last_stderr.strip()[:200]}", file=sys.stderr)
            _retry_sleep(attempt)
            continue
        break
    return False, last_stdout, last_stderr


def gh_json(args: list[str]) -> tuple[Any, str | None]:
    ok, stdout, stderr = run_gh(args)
    if not ok:
        return None, (stderr or stdout or "gh failed").strip()
    stripped = stdout.strip()
    if not stripped:
        return None, None
    try:
        return json.loads(stripped), None
    except json.JSONDecodeError as exc:
        return None, f"malformed gh JSON: {exc}"


def preflight() -> None:
    if shutil.which("gh") is None:
        raise SweepError("required command not found: gh")
    ok, _, stderr = run_gh(["auth", "status"], attempts=1)
    if not ok:
        raise SweepError(f"gh is not authenticated: {stderr.strip()[:400]}")


def label_exists(repo: str, label: str) -> bool | None:
    """True/False, or None when the label list could not be read."""
    payload, error = gh_json(["label", "list", "--repo", repo, "--limit", "200", "--json", "name"])
    if error is not None or payload is None:
        return None
    try:
        return any(entry.get("name") == label for entry in payload)
    except (AttributeError, TypeError):
        return None


def list_blocked_issues(repo: str, label: str, limit: int) -> tuple[list[dict[str, Any]], str | None]:
    payload, error = gh_json(
        [
            "issue", "list",
            "--repo", repo,
            "--label", label,
            "--state", "open",
            "--limit", str(limit),
            "--json", "number,title,url,body,updatedAt",
        ]
    )
    if error is not None:
        return [], error
    if payload is None:
        return [], None
    if not isinstance(payload, list):
        return [], "unexpected gh issue list payload"
    return payload, None


def list_comment_bodies(repo: str, number: int) -> tuple[list[str], str | None]:
    payload, error = gh_json(["issue", "view", str(number), "--repo", repo, "--json", "comments"])
    if error is not None:
        return [], error
    if not isinstance(payload, dict):
        return [], None
    comments = payload.get("comments") or []
    bodies: list[str] = []
    for comment in comments:
        if isinstance(comment, dict) and isinstance(comment.get("body"), str):
            bodies.append(comment["body"])
    return bodies, None


class BlockerResolver:
    """Resolves cited issue/PR references to open/closed, with a per-run cache."""

    def __init__(self) -> None:
        self._cache: dict[tuple[str, int], dict[str, Any]] = {}

    def resolve(self, repo: str | None, number: int) -> dict[str, Any]:
        if repo is None:
            return {"state": "unresolved", "reason": "unknown-repo-alias"}
        key = (repo.lower(), number)
        if key in self._cache:
            return self._cache[key]
        result = self._fetch(repo, number)
        self._cache[key] = result
        return result

    def _fetch(self, repo: str, number: int) -> dict[str, Any]:
        # `repos/{owner}/{repo}/issues/{n}` answers for issues AND pull requests, so
        # one call covers both citation targets.
        payload, error = gh_json(["api", f"repos/{repo}/issues/{number}"])
        if error is not None or not isinstance(payload, dict):
            reason = "not-found" if error and "404" in error else "unreadable"
            return {"state": "unresolved", "reason": reason, "detail": (error or "")[:200]}
        raw_state = str(payload.get("state") or "").lower()
        pull_request = payload.get("pull_request")
        merged = bool(isinstance(pull_request, dict) and pull_request.get("merged_at"))
        if raw_state == "closed":
            state = "merged" if merged else "closed"
        elif raw_state == "open":
            state = "open"
        else:
            return {"state": "unresolved", "reason": "unknown-state"}
        return {
            "state": state,
            "kind": "pr" if pull_request else "issue",
            "title": payload.get("title") or "",
        }


# --------------------------------------------------------------------------------------
# Sweep
# --------------------------------------------------------------------------------------


def classify(citations: list[dict[str, Any]]) -> str:
    if not citations:
        return CLASS_UNCITED
    if any(citation["state"] == "open" for citation in citations):
        return CLASS_OK
    # Unresolved is never evidence of closure: an issue with an unreadable blocker
    # stays OK rather than being proposed for a flip to ready.
    if any(citation["state"] == "unresolved" for citation in citations):
        return CLASS_OK
    return CLASS_STALE


def sweep_repo(
    repo: str,
    label: str,
    limit: int,
    aliases: dict[str, str],
    resolver: BlockerResolver,
    skip_comments: bool,
) -> dict[str, Any]:
    result: dict[str, Any] = {
        "repo": repo,
        "labelPresent": None,
        "issues": [],
        "errors": [],
    }

    present = label_exists(repo, label)
    result["labelPresent"] = present
    if present is False:
        result["errors"].append(f"label `{label}` is not defined in {repo}; nothing to verify")
        return result

    issues, error = list_blocked_issues(repo, label, limit)
    if error is not None:
        result["errors"].append(f"could not list blocked issues: {error[:300]}")
        return result

    for issue in issues:
        result["issues"].append(
            sweep_issue(repo, issue, aliases, resolver, skip_comments)
        )
    return result


def sweep_issue(
    repo: str,
    issue: dict[str, Any],
    aliases: dict[str, str],
    resolver: BlockerResolver,
    skip_comments: bool,
) -> dict[str, Any]:
    number = issue.get("number")
    record: dict[str, Any] = {
        "number": number,
        "title": (issue.get("title") or "").strip(),
        "url": issue.get("url") or "",
        "updatedAt": issue.get("updatedAt") or "",
        "citations": [],
        "notes": [],
    }
    if not isinstance(number, int):
        record["class"] = CLASS_ERROR
        record["notes"].append("issue payload had no number")
        return record

    citations = parse_citations(issue.get("body"), repo, aliases, origin="body")

    if not skip_comments:
        bodies, comment_error = list_comment_bodies(repo, number)
        if comment_error is not None:
            record["notes"].append(f"comments unreadable: {comment_error[:160]}")
        for index, body in enumerate(bodies, start=1):
            citations.extend(parse_citations(body, repo, aliases, origin=f"comment #{index}"))
        citations = _dedupe(citations)

    for citation in citations:
        if citation["unresolved"]:
            citation["state"] = "unresolved"
            citation["reason"] = citation["unresolved"]
            continue
        resolved = resolver.resolve(citation["repo"], citation["number"])
        citation["state"] = resolved["state"]
        if resolved.get("reason"):
            citation["reason"] = resolved["reason"]
        if resolved.get("kind"):
            citation["targetKind"] = resolved["kind"]

    record["citations"] = citations
    record["class"] = classify(citations)
    return record


# --------------------------------------------------------------------------------------
# Reporting
# --------------------------------------------------------------------------------------


def _ref(citation: dict[str, Any]) -> str:
    if citation["repo"] is None:
        return f"`{citation['raw']}`"
    return f"[{citation['repo']}#{citation['number']}](https://github.com/{citation['repo']}/issues/{citation['number']})"


def _refs(citations: list[dict[str, Any]], states: tuple[str, ...]) -> str:
    selected = [_ref(citation) for citation in citations if citation.get("state") in states]
    return ", ".join(selected) if selected else "—"


def _unresolved(citations: list[dict[str, Any]]) -> str:
    selected = [
        f"{_ref(citation)} ({citation.get('reason', 'unresolved')})"
        for citation in citations
        if citation.get("state") == "unresolved"
    ]
    return ", ".join(selected) if selected else "—"


def _escape(text: str) -> str:
    return text.replace("|", "\\|").replace("\n", " ").strip()


def counts_for(repo_result: dict[str, Any]) -> dict[str, int]:
    tally = {name: 0 for name in CLASS_ORDER}
    for issue in repo_result["issues"]:
        tally[issue.get("class", CLASS_ERROR)] += 1
    return tally


def render_markdown(report: dict[str, Any]) -> str:
    lines: list[str] = []
    lines.append("# Blocked-label sweep — dry run")
    lines.append("")
    lines.append(
        f"Generated `{report['generatedAt']}` · label `{report['label']}` · "
        f"mode **dry-run (no mutations)** · convention `docs/blocked-label-convention.md`"
    )
    lines.append("")
    lines.append("| Repo | Blocked open issues | STALE | UNCITED | OK | ERROR |")
    lines.append("| --- | ---: | ---: | ---: | ---: | ---: |")
    for repo_result in report["repos"]:
        tally = counts_for(repo_result)
        lines.append(
            f"| `{repo_result['repo']}` | {len(repo_result['issues'])} | "
            f"{tally[CLASS_STALE]} | {tally[CLASS_UNCITED]} | {tally[CLASS_OK]} | {tally[CLASS_ERROR]} |"
        )
    totals = report["totals"]
    lines.append(
        f"| **total** | **{totals['issues']}** | **{totals[CLASS_STALE]}** | "
        f"**{totals[CLASS_UNCITED]}** | **{totals[CLASS_OK]}** | **{totals[CLASS_ERROR]}** |"
    )
    lines.append("")

    for repo_result in report["repos"]:
        lines.append(f"## `{repo_result['repo']}`")
        lines.append("")
        for error in repo_result["errors"]:
            lines.append("> [!WARNING]")
            lines.append(f"> {error}")
            lines.append("")
        if not repo_result["issues"]:
            if not repo_result["errors"]:
                lines.append(f"No open issues carry `{report['label']}`.")
                lines.append("")
            continue

        stale = [i for i in repo_result["issues"] if i["class"] == CLASS_STALE]
        uncited = [i for i in repo_result["issues"] if i["class"] == CLASS_UNCITED]
        ok = [i for i in repo_result["issues"] if i["class"] == CLASS_OK]
        errored = [i for i in repo_result["issues"] if i["class"] == CLASS_ERROR]

        lines.append(f"### STALE — every cited blocker is closed ({len(stale)})")
        lines.append("")
        if stale:
            lines.append(f"Suggested action: swap `{report['label']}` -> `{report['readyLabel']}` and comment naming the closed blockers.")
            lines.append("")
            lines.append("| Issue | Title | Closed blockers |")
            lines.append("| --- | --- | --- |")
            for issue in stale:
                lines.append(
                    f"| [#{issue['number']}]({issue['url']}) | {_escape(issue['title'])} | "
                    f"{_refs(issue['citations'], ('closed', 'merged'))} |"
                )
        else:
            lines.append("None.")
        lines.append("")

        lines.append(f"### UNCITED — labelled with no parseable blocker citation ({len(uncited)})")
        lines.append("")
        if uncited:
            lines.append("Suggested action: comment asking for a blocker citation per the convention, or drop the label.")
            lines.append("")
            lines.append("| Issue | Title | Last updated |")
            lines.append("| --- | --- | --- |")
            for issue in uncited:
                lines.append(
                    f"| [#{issue['number']}]({issue['url']}) | {_escape(issue['title'])} | {issue['updatedAt']} |"
                )
        else:
            lines.append("None.")
        lines.append("")

        lines.append(f"### OK — at least one blocker still open or unverifiable ({len(ok)})")
        lines.append("")
        if ok:
            lines.append("| Issue | Open blockers | Closed blockers | Unresolved |")
            lines.append("| --- | --- | --- | --- |")
            for issue in ok:
                lines.append(
                    f"| [#{issue['number']}]({issue['url']}) | {_refs(issue['citations'], ('open',))} | "
                    f"{_refs(issue['citations'], ('closed', 'merged'))} | {_unresolved(issue['citations'])} |"
                )
        else:
            lines.append("None.")
        lines.append("")

        if errored:
            lines.append(f"### ERROR — could not be read ({len(errored)})")
            lines.append("")
            lines.append("| Issue | Notes |")
            lines.append("| --- | --- |")
            for issue in errored:
                lines.append(f"| #{issue.get('number', '?')} | {_escape('; '.join(issue['notes']))} |")
            lines.append("")

    lines.append("---")
    lines.append("")
    lines.append(
        "Nothing was mutated. Enforcement (comment + label flip) is not implemented in this "
        "slice; when it lands it runs only under `--enforce` with `ENFORCE_SWEEP=true` and "
        "every action is attributable to the bot identity that performed it."
    )
    lines.append("")
    return "\n".join(lines)


# --------------------------------------------------------------------------------------
# Self-test
# --------------------------------------------------------------------------------------

SELF_TEST_REPO = "honua-io/honua-studio"


def _cites(text: str | None, aliases: dict[str, str], repo: str = SELF_TEST_REPO) -> list[tuple[str | None, int]]:
    return [(c["repo"], c["number"]) for c in parse_citations(text, repo, aliases)]


def self_test() -> int:
    aliases = build_alias_map()
    failures: list[str] = []

    def check(name: str, actual: Any, expected: Any) -> None:
        if actual == expected:
            print(f"[PASS] {name}")
        else:
            print(f"[FAIL] {name}\n       expected: {expected!r}\n       actual:   {actual!r}")
            failures.append(name)

    # Reference forms.
    check(
        "qualified ref",
        _cites("Blocked by honua-io/honua-server#3475, which supplies the envelope.", aliases),
        [("honua-io/honua-server", 3475)],
    )
    check(
        "org shorthand + bare ref",
        _cites("Depends on #30, sdk-js#1397, server#3412, and server#3303.", aliases),
        [
            ("honua-io/honua-studio", 30),
            ("honua-io/honua-sdk-js", 1397),
            ("honua-io/honua-server", 3412),
            ("honua-io/honua-server", 3303),
        ],
    )
    check(
        "full URL ref",
        _cites(
            "Depends on: https://github.com/honua-io/honua-sdk-js/issues/1330 (S-A publish)",
            aliases,
        ),
        [("honua-io/honua-sdk-js", 1330)],
    )
    check(
        "pull URL ref",
        _cites("Blocked on https://github.com/honua-io/honua-server/pull/9 for the fix.", aliases),
        [("honua-io/honua-server", 9)],
    )
    check(
        "word between marker and ref",
        _cites("Depend on Studio #40, server#3303, and server#3412.", aliases),
        [
            ("honua-io/honua-studio", 40),
            ("honua-io/honua-server", 3303),
            ("honua-io/honua-server", 3412),
        ],
    )

    # Direction: `Blocks:` must not contribute blockers.
    check(
        "stops at Blocks:",
        _cites(
            "Depends: studio#40, server#3303, server#3312. Blocks: server#3305 and the receipt.",
            aliases,
        ),
        [
            ("honua-io/honua-studio", 40),
            ("honua-io/honua-server", 3303),
            ("honua-io/honua-server", 3312),
        ],
    )
    check(
        "stops at Blocks: across URLs",
        _cites(
            "Depends on: https://github.com/honua-io/honua-sdk-js/issues/1330 (S-A publish), "
            "https://github.com/honua-io/honua-studio/issues/30 (S-D pin bump)  ·  Blocks: "
            "https://github.com/honua-io/honua-studio/issues/41 (S-J)",
            aliases,
        ),
        [("honua-io/honua-sdk-js", 1330), ("honua-io/honua-studio", 30)],
    )
    check(
        "a Blocks-only line cites nothing",
        _cites("Blocks: server#3305 and the compose+save release receipt.", aliases),
        [],
    )
    check(
        "an unlabelled 'Blocks #N' still stops the run",
        _cites("Depends on #5 and blocks #6.", aliases),
        [("honua-io/honua-studio", 5)],
    )
    check(
        "the word 'blocking' as prose does not truncate",
        _cites("Depends on #5 — blocking the lane — and #6.", aliases),
        [("honua-io/honua-studio", 5), ("honua-io/honua-studio", 6)],
    )

    # Continuation onto a bullet list.
    check(
        "marker introduces a list",
        _cites("Blocked by:\n- #12 the schema\n- server#7 the route\n\n- #999 unrelated", aliases),
        [("honua-io/honua-studio", 12), ("honua-io/honua-server", 7)],
    )
    check(
        "heading + blank line + list",
        _cites("## Dependencies\n\n- #40 (live agent loop) — blocking for the live lane\n- sdk-js#1397\n", aliases),
        [("honua-io/honua-studio", 40), ("honua-io/honua-sdk-js", 1397)],
    )
    check(
        "a marker never reaches into the next section",
        _cites("## Dependencies\n\n## Acceptance\n\n- #40 is not a dependency here\n", aliases),
        [],
    )

    # Non-citations.
    check("prose blocker is uncited", _cites("Blocked on the platform team's capacity.", aliases), [])
    check("no marker at all", _cites("This relates to #77 somehow.", aliases), [])
    check("empty body", _cites("", aliases), [])
    check("absent body", _cites(None, aliases), [])
    check("heading only", _cites("## Depends / blocks\n", aliases), [])

    # Malformed / hostile input must not raise.
    check("unterminated markdown", _cites("Blocked by ```#5", aliases), [("honua-io/honua-studio", 5)])
    check("unknown shorthand is unresolved", _cites("Blocked by nosuchrepo#5", aliases), [(None, 5)])
    check(
        "unicode body survives",
        _cites("Blocked by server#1 — 日本語 · emoji 🚧", aliases),
        [("honua-io/honua-server", 1)],
    )
    check("duplicate refs collapse", _cites("Blocked by #5, #5, honua-io/honua-studio#5", aliases), [("honua-io/honua-studio", 5)])
    check("huge number parses", _cites("Blocked by #999999999", aliases), [("honua-io/honua-studio", 999999999)])
    check("hash without number", _cites("Blocked by # nothing", aliases), [])

    # Classification.
    def klass(states: list[str]) -> str:
        return classify([{"state": state} for state in states])

    check("all closed -> STALE", klass(["closed", "merged"]), CLASS_STALE)
    check("one open -> OK", klass(["closed", "open"]), CLASS_OK)
    check("unresolved never STALE", klass(["closed", "unresolved"]), CLASS_OK)
    check("no citations -> UNCITED", klass([]), CLASS_UNCITED)

    # Alias overrides.
    check(
        "alias override",
        _cites("Blocked by acme#3", build_alias_map(["acme=other-org/acme"])),
        [("other-org/acme", 3)],
    )

    # Report rendering never crashes on an empty sweep.
    empty = build_report([], DEFAULT_LABEL, DEFAULT_READY_LABEL)
    check("renders an empty report", "Blocked-label sweep" in render_markdown(empty), True)

    print("")
    if failures:
        print(f"[RESULT] self-test FAILED: {len(failures)} case(s): {', '.join(failures)}")
        return 1
    print("[RESULT] self-test passed")
    return 0


# --------------------------------------------------------------------------------------
# Entry point
# --------------------------------------------------------------------------------------


def build_report(repo_results: list[dict[str, Any]], label: str, ready_label: str) -> dict[str, Any]:
    totals = {name: 0 for name in CLASS_ORDER}
    totals["issues"] = 0
    for repo_result in repo_results:
        tally = counts_for(repo_result)
        for name in CLASS_ORDER:
            totals[name] += tally[name]
        totals["issues"] += len(repo_result["issues"])
    return {
        "schemaVersion": 1,
        "kind": "honua-devops.blocked-label-sweep",
        "generatedAt": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
        "mode": "dry-run",
        "label": label,
        "readyLabel": ready_label,
        "repos": repo_results,
        "totals": totals,
    }


def parse_args(argv: list[str]) -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Re-verify state/blocked labels against the live state of their cited blockers.",
    )
    parser.add_argument(
        "--repo",
        action="append",
        dest="repos",
        metavar="OWNER/REPO",
        help=f"Target repository, repeatable. Defaults to: {', '.join(DEFAULT_REPOS)}",
    )
    parser.add_argument("--label", default=DEFAULT_LABEL, help=f"Blocked label. Default {DEFAULT_LABEL}.")
    parser.add_argument(
        "--ready-label",
        default=DEFAULT_READY_LABEL,
        help=f"Label a stale issue should move to. Default {DEFAULT_READY_LABEL}. Reported, never applied.",
    )
    parser.add_argument("--limit", type=int, default=200, help="Max blocked issues per repo. Default 200.")
    parser.add_argument(
        "--repo-alias",
        action="append",
        dest="repo_aliases",
        metavar="SHORT=OWNER/REPO",
        help="Extra citation shorthand, repeatable. Also read from HONUA_SWEEP_REPO_ALIASES.",
    )
    parser.add_argument("--output", default=None, help="Write the markdown report here as well as stdout.")
    parser.add_argument("--json-out", default=None, help="Write the machine-readable report here.")
    parser.add_argument(
        "--skip-comments",
        action="store_true",
        help="Parse citations from issue bodies only (one fewer API call per issue).",
    )
    parser.add_argument(
        "--fail-on-findings",
        action="store_true",
        help="Exit 1 when any STALE or UNCITED issue is found. Off by default: a dry run reports, it does not gate.",
    )
    parser.add_argument(
        "--enforce",
        action="store_true",
        help="Guard only. Refuses unless ENFORCE_SWEEP=true, and refuses regardless in this slice: no mutation path exists.",
    )
    parser.add_argument("--self-test", action="store_true", help="Exercise the citation parser on fixtures. No network.")
    return parser.parse_args(argv)


def main(argv: list[str]) -> int:
    args = parse_args(argv)

    if args.self_test:
        return self_test()

    # The guard runs before anything reads or writes, so a mistaken --enforce can
    # never reach an API call.
    if args.enforce:
        if os.environ.get("ENFORCE_SWEEP") != "true":
            print(
                "[ERROR] --enforce refused: ENFORCE_SWEEP=true is not set. "
                "Enforcement requires both the flag and the environment variable.",
                file=sys.stderr,
            )
            return 2
        print(
            "[ERROR] --enforce refused: enforcement is not implemented in this slice "
            "(honua-devops#167 first slice is dry-run only). Nothing was mutated.",
            file=sys.stderr,
        )
        return 2

    repos = args.repos or list(DEFAULT_REPOS)
    for repo in repos:
        if repo.count("/") != 1 or not all(repo.split("/")):
            print(f"[ERROR] invalid --repo {repo!r}: expected owner/repo", file=sys.stderr)
            return 2
    if args.limit < 1:
        print("[ERROR] --limit must be >= 1", file=sys.stderr)
        return 2

    aliases = build_alias_map(args.repo_aliases)

    try:
        preflight()
    except SweepError as exc:
        print(f"[ERROR] {exc}", file=sys.stderr)
        return 3

    resolver = BlockerResolver()
    repo_results = [
        sweep_repo(repo, args.label, args.limit, aliases, resolver, args.skip_comments)
        for repo in repos
    ]

    report = build_report(repo_results, args.label, args.ready_label)
    markdown = render_markdown(report)
    print(markdown)

    if args.output:
        path = Path(args.output)
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(markdown, encoding="utf-8")
        print(f"[RESULT] Markdown report: {path}", file=sys.stderr)
    if args.json_out:
        path = Path(args.json_out)
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
        print(f"[RESULT] JSON report: {path}", file=sys.stderr)

    totals = report["totals"]
    print(
        f"[RESULT] blocked={totals['issues']} stale={totals[CLASS_STALE]} "
        f"uncited={totals[CLASS_UNCITED]} ok={totals[CLASS_OK]} error={totals[CLASS_ERROR]} (dry run, nothing mutated)",
        file=sys.stderr,
    )

    if args.fail_on_findings and (totals[CLASS_STALE] or totals[CLASS_UNCITED]):
        return 1
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main(sys.argv[1:]))
    except KeyboardInterrupt:
        raise SystemExit(130)
