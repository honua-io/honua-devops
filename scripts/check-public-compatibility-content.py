#!/usr/bin/env python3

"""Keep public compatibility references within the approved nominative inventory."""

from __future__ import annotations

import argparse
import hashlib
import re
from collections import Counter
from pathlib import Path


# R30 permits the inventoried nominative references, but not new testing detail.
# Store line digests rather than the text itself so this enforcement mechanism
# does not duplicate the restricted footprint it protects.
APPROVED_LINE_DIGESTS = Counter(
    {
        # Filled from the R30-reviewed inventory below. Counts preserve duplicate
        # generated rows while allowing unrelated line-number changes.
        "2943d9fb4da6cc52a6aed6e3971ec865b442791a9c144c7e34b52a98c3f9ca5f": 1,
        "30034d69abb324e9d6f5412526d98120a083d3ea3559825d82734a0d1b185578": 1,
        "343f305d8d52dd243340c80aaf74230304f7dca032cba052311860ed17d668ba": 2,
        "8bd969baef8217d29f98ad060ba0b76efc6c479ab4bd1dbbfe4b3ce23cfe83ae": 2,
        "ae36fbebeed2ef3a0306b6208a3cfe34a93c9775e33cb30d9d0239bc2b2a0677": 1,
        "c1a58eaf81d1e5d6aa58664421cf5b9926bcb48b44d3ba2a0bd19ec71cdd42a2": 1,
        "e02cbc5645c5507f4be084b394accdfec646d0011ec03cfa50891fce1c85a65e": 2,
    }
)

FOOTPRINT = re.compile(
    r"arc" r"py|arc" r"gis pro|arc" r"gispro|arc" r"gis-pro|\.ap" r"rx|\.at" r"bx",
    re.IGNORECASE,
)

IGNORED_PARTS = {".git", "bin", "obj", "node_modules", ".venv"}
SELF = Path(__file__).resolve()


def digest(path: Path, line: str, root: Path) -> str:
    relative = path.relative_to(root).as_posix()
    return hashlib.sha256(f"{relative}\0{line}".encode()).hexdigest()


def matching_lines(root: Path) -> Counter[str]:
    matches: Counter[str] = Counter()
    for path in root.rglob("*"):
        if not path.is_file() or path.resolve() == SELF or IGNORED_PARTS.intersection(path.parts):
            continue
        try:
            lines = path.read_text(encoding="utf-8").splitlines()
        except (UnicodeDecodeError, OSError):
            continue
        for line in lines:
            if FOOTPRINT.search(line):
                matches[digest(path, line, root)] += 1
    return matches


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Reject desktop-client compatibility detail outside the R30 nominative inventory."
    )
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parent.parent)
    args = parser.parse_args()
    root = args.root.resolve()
    actual = matching_lines(root)

    unexpected = actual - APPROVED_LINE_DIGESTS
    missing = APPROVED_LINE_DIGESTS - actual
    if unexpected or missing:
        print("[ERROR] Public compatibility content differs from the R30-approved nominative inventory.")
        print(f"        unexpected matching lines: {sum(unexpected.values())}")
        print(f"        missing approved lines: {sum(missing.values())}")
        print("        Keep product references nominative; do not add test, runner, UI, licence, installation, screenshot, or per-client result detail.")
        return 1

    print(f"Public compatibility content policy passed ({sum(actual.values())} approved nominative lines).")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
