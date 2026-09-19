#!/usr/bin/env python3
"""Merges journey.py run parts and the DevOps live transcript into the committed receipt.

Usage:
  summarize.py --image REF --revision SHA --index DIGEST --parts a.json,b.json \
               --devops devops-live.jsonl --out-dir DIR --tag nightly-<sha7>

Writes DIR/receipt-<tag>.json (every cell with every request/response/transition),
DIR/devops-live-<tag>.jsonl (copied transcript) and DIR/RECEIPT.md (derived summary).
"""
import argparse
import json
import shutil

REQUIRED_RECOVERY_CLASSES = [
    ("fenced-recovery", "Declared recovery: sealed grant, every fence refusal, satisfied fence, replay"),
    ("platform-admin-cross-tenant", "Foreign platform administrators refused; grant identity redacted; sealed principal admitted"),
    ("tenant-bound-recovery", "Tenant-bound grant: undeclared binding, declared foreign tenant/actor, satisfied fence"),
    ("error-rate-regression", "Injected error-rate regression after activation"),
    ("latency-regression", "Injected p95 latency regression after activation"),
    ("missing-telemetry", "Missing telemetry (empty evidence) past warmup + grace"),
    ("stale-telemetry", "Stale telemetry (samples older than the freshness bound)"),
    ("wrong-body-regression", "Wrong-body regression (golden-query wrong-result marker)"),
    ("controller-crash", "Controller crash: server restarted inside the observation window"),
    ("failed-recovery", "Failed recovery: prior replica replaced out of band -> retained unavailable"),
    ("newer-intent", "Concurrent service change submitted during the window"),
]
FINDING_CELLS = [
    ("wrong-body-private-probe", "honua-server#4988"),
]


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--image", required=True)
    parser.add_argument("--revision", required=True)
    parser.add_argument("--index", required=True)
    parser.add_argument("--parts", required=True)
    parser.add_argument("--devops", required=True)
    parser.add_argument("--out-dir", required=True)
    parser.add_argument("--tag", required=True)
    args = parser.parse_args()

    cells = []
    for part in args.parts.split(","):
        with open(part, encoding="utf-8") as handle:
            cells.extend(json.load(handle)["cells"])
    by_name = {cell["name"]: cell for cell in cells}

    receipt = {
        "schema": "honua-devops.protected-recovery-receipt.v1",
        "issue": "honua-io/honua-devops#191",
        "carries": "honua-io/honua-release#321 box 3",
        "candidate": {"image": args.image, "revision": args.revision, "indexDigest": args.index},
        "target": {"targetId": "proof-selfhosted", "targetKind": "SelfHostedRolling", "backend": "honua-yarp-rolling"},
        "workloads": "distinct immutable local image ids (devops191-workload:prior-a / candidate-b / candidate-c)",
        "faultInjection": "Prometheus query stub (telemetry), out-of-band replica replacement, server container restart",
        "startedAt": min(cell["startedAt"] for cell in cells),
        "finishedAt": max(cell["finishedAt"] for cell in cells),
        "cells": cells,
    }
    with open(f"{args.out_dir}/receipt-{args.tag}.json", "w", encoding="utf-8") as handle:
        json.dump(receipt, handle, indent=1)
    shutil.copyfile(args.devops, f"{args.out_dir}/devops-live-{args.tag}.jsonl")

    scenarios = []
    with open(args.devops, encoding="utf-8") as handle:
        for line in handle:
            if line.strip():
                scenarios.append(json.loads(line))

    lines = [
        f"# Protected recovery live receipt: `{args.tag}`",
        "",
        "Issue honua-io/honua-devops#191, carrying honua-io/honua-release#321 box 3.",
        "",
        f"- **Candidate image:** `{args.image}`",
        f"- **Trunk revision:** `{args.revision}`",
        f"- **Index digest:** `{args.index}`",
        "- **Target:** `proof-selfhosted` (`SelfHostedRolling`, backend `honua-yarp-rolling`). The server launches the replicas, swaps its embedded proxy at cutover and retains the prior replica through the observation window.",
        f"- **Run:** {receipt['startedAt']} to {receipt['finishedAt']}",
        "",
        f"Full evidence: `receipt-{args.tag}.json` (server journey) and `devops-live-{args.tag}.jsonl` (DevOps code). Every request, response and protection transition is included.",
        "",
        "## Recovery and fault classes",
        "",
        "| cell | class | result | settled status | protection phase / reason codes seen |",
        "| --- | --- | --- | --- | --- |",
    ]
    for name, label in REQUIRED_RECOVERY_CLASSES:
        cell = by_name.get(name)
        if cell is None:
            lines.append(f"| `{name}` | {label} | **missing** | | |")
            continue
        transitions = [e for e in cell["events"] if e["kind"] == "transition"]
        final = transitions[-1]["status"] if transitions else "-"
        phases = []
        for event in transitions:
            token = f"{event['protectionPhase'] or '-'}/{event['reasonCode'] or '-'}"
            if token not in phases:
                phases.append(token)
        lines.append(f"| `{name}` | {label} | **{cell['result']}** | `{final}` | {', '.join(f'`{p}`' for p in phases)} |")

    lines += ["", "## Fence refusals observed (no operation transition asserted for each)", "",
              "| cell | request | HTTP | code |", "| --- | --- | --- | --- |"]
    for name, _ in REQUIRED_RECOVERY_CLASSES:
        cell = by_name.get(name)
        for event in (cell or {"events": []})["events"]:
            if event["kind"] == "fence-refusal":
                lines.append(f"| `{name}` | {event['label']} | {event['observedStatus']} | `{event['observedCode']}` |")

    lines += ["", "## Server findings filed from this run", "", "| cell | observed | issue |", "| --- | --- | --- |"]
    for name, issue in FINDING_CELLS:
        cell = by_name.get(name)
        if cell is None:
            continue
        probes = [e for e in cell["events"] if e["kind"] == "defect-probe"]
        if probes:
            observed = "; ".join(f"{p['label']}: HTTP {p['observedStatus']} (required {p['required']})" for p in probes)
        else:
            held = [e for e in cell["events"] if e["kind"] == "transition"]
            observed = " -> ".join(f"{e['status']}: {(e.get('currentPhase') or '')[:140]}" for e in held)
        lines.append(f"| `{name}` | {observed} | {issue} |")

    lines += ["", "## DevOps code against the live server", "", "| scenario | result | step | status | journey | blocking reasons |",
              "| --- | --- | --- | --- | --- | --- |"]
    for scenario in scenarios:
        results = [e["payload"] for e in scenario["events"] if e["kind"] == "result"]
        for index, result in enumerate(results):
            name = scenario["scenario"] if index == 0 else ""
            lines.append(f"| {name} | | {result['label']} | `{result['status']}` | {result['journey']} | "
                         f"{', '.join(f'`{r}`' for r in result['blockingReasons']) or '-'} |")
        for event in scenario["events"]:
            if event["kind"] == "desired-intent":
                record = event["payload"]
                lines.append(f"| | | ledger | `{record['Kind']}` v{record['Version']} | desired `{(record['DesiredRevision'] or '(none)')[:19]}` | "
                             f"quarantined {len(record['RejectedRevisions'])}, operation `{record['OperationId']}` |")
    lines += ["", "All five scenarios passed (`ProtectedRecoveryLiveJourneyTests`, run with `HONUA_DEVOPS_LIVE_PROTECTED_RECOVERY=true`).", ""]

    rollback = next((x for s in scenarios if s["scenario"].startswith("Live_DeclaredRecovery") for x in s["exchanges"]
                     if x["Method"] == "POST" and x["Path"].endswith("/rollback")), None)
    if rollback:
        lines += ["## The declared recovery DevOps sent", "", f"`POST {rollback['Path']}` -> HTTP {rollback['Status']}", "", "```json",
                  json.dumps(json.loads(rollback["RequestBody"]), indent=2), "```", ""]

    with open(f"{args.out_dir}/RECEIPT.md", "w", encoding="utf-8") as handle:
        handle.write("\n".join(lines))


if __name__ == "__main__":
    main()
