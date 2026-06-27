#!/usr/bin/env python3
"""Tests for the bounded-sample scoring in run_corpus.py.

Runnable either under pytest or standalone: ``python3 eval/corpus/test_run_corpus.py``.
These cover the audit fix that a clean call must NOT be auto-credited as a pass.
"""

from __future__ import annotations

import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

import run_corpus  # noqa: E402


def test_failed_call_scores_fail() -> None:
    verdict, scored = run_corpus.score_scenario("studio", "generated", {"ok": False, "error": "boom"})
    assert verdict == "fail" and scored is True


def test_studio_needs_clarification_requires_clarifications() -> None:
    # Expected a clarification but got none -> fail (was previously credited ok on HTTP 200).
    assert run_corpus.score_scenario(
        "studio", "needs-clarification", {"ok": True, "clarifications": 0, "unmapped": 0}
    ) == ("fail", True)
    assert run_corpus.score_scenario(
        "studio", "needs-clarification", {"ok": True, "clarifications": 2, "unmapped": 0}
    ) == ("pass", True)


def test_studio_unsupported_requires_unmapped() -> None:
    assert run_corpus.score_scenario(
        "studio", "unsupported", {"ok": True, "clarifications": 0, "unmapped": 0}
    ) == ("fail", True)
    assert run_corpus.score_scenario(
        "studio", "unsupported", {"ok": True, "clarifications": 0, "unmapped": 1}
    ) == ("pass", True)


def test_studio_generated_must_not_clarify_or_leave_unmapped() -> None:
    assert run_corpus.score_scenario(
        "studio", "generated", {"ok": True, "clarifications": 0, "unmapped": 0}
    ) == ("pass", True)
    # A "generated" expectation that actually clarified is NOT a pass.
    assert run_corpus.score_scenario(
        "studio", "generated", {"ok": True, "clarifications": 1, "unmapped": 0}
    ) == ("fail", True)


def test_devops_clean_run_is_smoke_not_pass() -> None:
    # DevOps text behaviors have no observable signal here: a clean exit 0 is 'smoke',
    # explicitly NOT credited as pass.
    verdict, scored = run_corpus.score_scenario("devops", "proposal", {"ok": True, "exit": 0})
    assert verdict == "smoke" and scored is False


def _main() -> int:
    failures = 0
    for name, fn in sorted(globals().items()):
        if name.startswith("test_") and callable(fn):
            try:
                fn()
                print(f"ok   {name}")
            except AssertionError as exc:  # noqa: PERF203
                failures += 1
                print(f"FAIL {name}: {exc}")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(_main())
