#!/usr/bin/env python3
"""Verify a blind-eval scorecard artifact before it is published.

This is the last gate in front of the artifact store. It checks three things the
CLI cannot check for itself:

1.  Provenance — the scorecard is pinned to the commit and lane the workflow ran
    (`--expect-commit`, `--expect-lane`), so a stale or fixture-lane artifact can
    never be published as credentialed release evidence.
2.  Contract — the document matches `contracts/blind-eval-scorecard.v1.schema.json`
    field-for-field at the top level.
3.  Redaction (honua-devops#155 NFR-001) — every scenario entry carries digests and
    scores only. Any unexpected property, or a `promptDigest`/`responseDigest` that
    is not a sha256 digest, is treated as a possible transcript leak and fails.

Exit codes: 0 verified, 1 verification failed, 2 the scorecard could not be read.
"""

from __future__ import annotations

import argparse
import json
import re
import sys
from pathlib import Path

DIGEST_PATTERN = re.compile(r"^sha256:[0-9a-f]{64}$")

REQUIRED_TOP_LEVEL = {
    "schemaVersion",
    "kind",
    "runId",
    "lane",
    "provider",
    "modelId",
    "faultSet",
    "evaluationMode",
    "commitSha",
    "startedAt",
    "completedAt",
    "harness",
    "thresholds",
    "scenarios",
    "aggregate",
}

ALLOWED_SCENARIO_KEYS = {
    "scenarioId",
    "scenarioName",
    "category",
    "remediationScope",
    "promptDigest",
    "responseDigest",
    "responseChars",
    "latencySeconds",
    "diagnosisCorrect",
    "evidenceQuality",
    "remediationSafe",
    "policyCompliant",
    "rollbackGuidanceCorrect",
    "recoveryVerified",
    "serviceHealthRestored",
    "compositeScore",
    "result",
    "failureModes",
    "error",
}

EXPECTED_KIND = "honua-devops.blind-eval-scorecard"


def parse_args(argv: list[str]) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("scorecard", help="Path to the scorecard JSON artifact.")
    parser.add_argument("--expect-lane", choices=["live", "fixture"], default=None)
    parser.add_argument("--expect-commit", default=None)
    parser.add_argument("--expect-result", choices=["pass", "fail"], default=None)
    return parser.parse_args(argv)


def verify(document: dict, args: argparse.Namespace) -> list[str]:
    failures: list[str] = []

    missing = REQUIRED_TOP_LEVEL - set(document)
    if missing:
        failures.append(f"missing required top-level fields: {sorted(missing)}")

    unexpected = set(document) - REQUIRED_TOP_LEVEL
    if unexpected:
        failures.append(f"unexpected top-level fields: {sorted(unexpected)}")

    if document.get("kind") != EXPECTED_KIND:
        failures.append(f"kind is {document.get('kind')!r}, expected {EXPECTED_KIND!r}")

    if document.get("schemaVersion") != "1":
        failures.append(f"schemaVersion is {document.get('schemaVersion')!r}, expected '1'")

    if args.expect_lane is not None and document.get("lane") != args.expect_lane:
        failures.append(f"lane is {document.get('lane')!r}, expected {args.expect_lane!r}")

    if args.expect_commit is not None and document.get("commitSha") != args.expect_commit:
        failures.append(
            f"commitSha is {document.get('commitSha')!r}, expected {args.expect_commit!r}"
        )

    scenarios = document.get("scenarios")
    if not isinstance(scenarios, list) or not scenarios:
        failures.append("scenarios must be a non-empty array")
        scenarios = []

    for index, scenario in enumerate(scenarios):
        if not isinstance(scenario, dict):
            failures.append(f"scenarios[{index}] is not an object")
            continue

        leaked = set(scenario) - ALLOWED_SCENARIO_KEYS
        if leaked:
            failures.append(
                f"scenarios[{index}] carries non-contract fields {sorted(leaked)} "
                "(NFR-001: digests and scores only)"
            )

        for digest_field in ("promptDigest", "responseDigest"):
            value = scenario.get(digest_field)
            if not isinstance(value, str) or not DIGEST_PATTERN.match(value):
                failures.append(
                    f"scenarios[{index}].{digest_field} is not a sha256 digest "
                    "(NFR-001: the artifact must never carry prompt or transcript text)"
                )

    aggregate = document.get("aggregate")
    if not isinstance(aggregate, dict):
        failures.append("aggregate must be an object")
    else:
        total = aggregate.get("scenariosTotal")
        parts = [
            aggregate.get("scenariosPassed"),
            aggregate.get("scenariosFailed"),
            aggregate.get("scenariosErrored"),
        ]
        if all(isinstance(part, int) for part in parts) and isinstance(total, int):
            if sum(parts) != total:
                failures.append(
                    f"aggregate counts {parts} do not sum to scenariosTotal {total}"
                )
        else:
            failures.append("aggregate scenario counts must be integers")

        if isinstance(total, int) and total != len(scenarios):
            failures.append(
                f"aggregate.scenariosTotal {total} does not match {len(scenarios)} scenario entries"
            )

        if args.expect_result is not None and aggregate.get("result") != args.expect_result:
            failures.append(
                f"aggregate.result is {aggregate.get('result')!r}, expected {args.expect_result!r}"
            )

    return failures


def main(argv: list[str]) -> int:
    args = parse_args(argv)

    path = Path(args.scorecard)
    try:
        document = json.loads(path.read_text(encoding="utf-8"))
    except FileNotFoundError:
        print(f"[ERROR] scorecard not found: {path}", file=sys.stderr)
        return 2
    except json.JSONDecodeError as error:
        print(f"[ERROR] scorecard is not valid JSON: {error}", file=sys.stderr)
        return 2

    if not isinstance(document, dict):
        print("[ERROR] scorecard root must be a JSON object", file=sys.stderr)
        return 2

    failures = verify(document, args)

    if failures:
        for failure in failures:
            print(f"[ERROR] {failure}", file=sys.stderr)
        return 1

    aggregate = document["aggregate"]
    print(
        f"Scorecard verified: lane={document['lane']} provider={document['provider']} "
        f"model={document['modelId']} commit={document['commitSha']} "
        f"result={aggregate['result']} "
        f"({aggregate['scenariosPassed']}/{aggregate['scenariosTotal']} passed, "
        f"errored={aggregate['scenariosErrored']})"
    )
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
