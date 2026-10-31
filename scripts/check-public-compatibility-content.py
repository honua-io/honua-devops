#!/usr/bin/env python3

"""Reject client-specific licensing and test detail from public compatibility content."""

from __future__ import annotations

import argparse
import re
from pathlib import Path


# Restrict publication of client-specific test, licence, and runner detail.
FOOTPRINT = re.compile(
    r"arc" r"py|arc" r"gis pro|arc" r"gispro|arc" r"gis-pro|\.ap" r"rx|\.at" r"bx",
    re.IGNORECASE,
)

IGNORED_PARTS = {".git", "bin", "obj", "node_modules", ".venv"}
SELF = Path(__file__).resolve()


def matching_lines(root: Path) -> list[tuple[Path, int]]:
    matches: list[tuple[Path, int]] = []
    for path in root.rglob("*"):
        if not path.is_file() or path.resolve() == SELF or IGNORED_PARTS.intersection(path.parts):
            continue
        try:
            lines = path.read_text(encoding="utf-8").splitlines()
        except (UnicodeDecodeError, OSError):
            continue
        for number, line in enumerate(lines, start=1):
            if FOOTPRINT.search(line):
                matches.append((path, number))
    return matches


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Reject client-specific testing, licensing, and runner details from repository content."
    )
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parent.parent)
    args = parser.parse_args()
    root = args.root.resolve()
    actual = matching_lines(root)
    if actual:
        print(f"[ERROR] Restricted compatibility detail found on {len(actual)} line(s).")
        print("        Remove client-specific test, licence, runner, and installation procedures.")
        return 1

    print("Public compatibility content policy passed (no restricted client-specific detail).")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
