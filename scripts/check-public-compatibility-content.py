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
        "07862ae51bfd902663ab332a7184ba1979b68311d0ce6878717a37a3f35ea2d8": 1,
        "2943d9fb4da6cc52a6aed6e3971ec865b442791a9c144c7e34b52a98c3f9ca5f": 1,
        "29f6cf55bf09bfa82b31cfaffcc5e8eb2f40fed182c11d74065c25002b42ec7f": 1,
        "2c24cb0e1268fbed8543e897b3586cc86c3169471b88928a461464d9dfa2e31a": 1,
        "343f305d8d52dd243340c80aaf74230304f7dca032cba052311860ed17d668ba": 2,
        "3bb64c787c7955635b56d6653f5c1786dbdb9e5f92db16edc074fc4c5a60565d": 1,
        "569e7668d56d94ca0d4ed60210166f207469c957f283a9b2caedfb2adbd174c5": 1,
        "58d1714442039a4f6cfb5fa936998be81eae93654dae815c2f2e9eb9f241bbd4": 1,
        "5ad58a52647db8d7708c066e88cc19dd9aaea8d677b7f022cdba34b9abc2bc77": 1,
        "6034f7f4d581e39093868a6ca33aaa11afc3390d053e791679b098b0d335674d": 1,
        "63bf70af80ce5e195e29f98698a11800d588f12975f649daf43e873bf9f37723": 1,
        "8bd969baef8217d29f98ad060ba0b76efc6c479ab4bd1dbbfe4b3ce23cfe83ae": 2,
        "92206889f740e903b704a28aea93231ea9db8429a168aa736d56aaf4ee4050ca": 1,
        "a4edb2089282e02e7220c862bba9aecc77d5820582caef4944b9f763ac3fb773": 1,
        "a749078bb3863f3aeca6f2fa6df05597631916c5854cbd6f65a3ebf266240fa2": 1,
        "a957c107b3373da9dfbecd52c7c7afb404b62fc79b0ac009a7d1584b7b14d90c": 1,
        "ae36fbebeed2ef3a0306b6208a3cfe34a93c9775e33cb30d9d0239bc2b2a0677": 1,
        "b3934754106282f4720427301f590a13a83f62825afdae331c0a8f54467c1c9f": 1,
        "c1a58eaf81d1e5d6aa58664421cf5b9926bcb48b44d3ba2a0bd19ec71cdd42a2": 1,
        "c2795f77343fc9dd6958591e6d17678fa281b09b210a7ef0fcf14a179164652e": 1,
        "d5978940334f4c260229e291ea7a17fe81b5db534e8d12e9c0f51fb1ed889924": 1,
        "e02cbc5645c5507f4be084b394accdfec646d0011ec03cfa50891fce1c85a65e": 2,
        "e7730b8cfe6ff3a382b7ed2eca98720bb545248196229fc76ca1ad4430ce92f6": 1,
        "ed024e675ab259ee48ff27bc273a8338745abe1d3c643a6e13ffbbf6eb196ce0": 1,
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
