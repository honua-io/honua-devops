#!/usr/bin/env python3
"""Honua AI representative eval-corpus runner.

The corpus DEFINITION uses no AI. By default this script only validates the
corpus and prints coverage (zero model calls). A bounded measured sample is
opt-in via --run-sample and is capped at MAX_SAMPLE_CALLS model calls with no
retries. The sample never actuates any DevOps action and never touches the
live demo alias.

Usage:
  # zero-AI: validate structure + print coverage
  python3 run_corpus.py --validate

  # list scenarios (optionally filtered)
  python3 run_corpus.py --list --surface studio --difficulty happy

  # bounded measured sample (opt-in; <= 30 model calls, no retries)
  python3 run_corpus.py --run-sample --out artifacts/sample-run

Studio sample calls demo.honua.io generation endpoints with the admin key.
DevOps sample shells out to `honua-devops --provider claude --prompt` in
plan / pr-first mode. Provide credentials via environment; see --help.
"""

from __future__ import annotations

import argparse
import collections
import json
import os
import subprocess
import sys
import time
from pathlib import Path
from typing import Any

HERE = Path(__file__).resolve().parent
CORPUS = HERE / "corpus.json"
MAX_SAMPLE_CALLS = 30
VALID_BEHAVIORS = {
    "generated", "needs-clarification", "unsupported",
    "description", "diagnosis", "analysis", "proposal", "changeset",
}


def load_corpus() -> dict[str, Any]:
    manifest = json.loads(CORPUS.read_text())
    for surface in manifest["surfaces"]:
        path = HERE / surface["scenarios"]
        surface["data"] = json.loads(path.read_text())
    return manifest


def all_scenarios(manifest: dict[str, Any]) -> list[dict[str, Any]]:
    out: list[dict[str, Any]] = []
    for surface in manifest["surfaces"]:
        area_key = surface["areaKey"]
        for sc in surface["data"]["scenarios"]:
            sc = dict(sc)
            sc["_surface"] = surface["id"]
            sc["_area"] = sc.get(area_key) or sc.get("operationArea") or sc.get("capability")
            out.append(sc)
    return out


def validate(manifest: dict[str, Any]) -> int:
    errors: list[str] = []
    seen_ids: set[str] = set()
    scenarios = all_scenarios(manifest)
    for sc in scenarios:
        sid = sc.get("id")
        if not sid:
            errors.append("scenario missing id")
            continue
        if sid in seen_ids:
            errors.append(f"duplicate id: {sid}")
        seen_ids.add(sid)
        if sc.get("expected_behavior") not in VALID_BEHAVIORS:
            errors.append(f"{sid}: invalid expected_behavior {sc.get('expected_behavior')!r}")
        for req in ("prompt", "pass_criteria", "difficulty"):
            if not sc.get(req):
                errors.append(f"{sid}: missing {req}")
        if not isinstance(sc.get("held_out"), bool):
            errors.append(f"{sid}: held_out must be bool")

    print(f"corpus: {manifest['name']} v{manifest['version']}")
    print(f"scenarios: {len(scenarios)} across {len(manifest['surfaces'])} surfaces\n")
    for surface in manifest["surfaces"]:
        scs = surface["data"]["scenarios"]
        area_key = surface["areaKey"]
        by_area = collections.Counter(sc.get(area_key) or sc.get("operationArea") for sc in scs)
        held = sum(1 for sc in scs if sc.get("held_out"))
        pct = 100 * held / len(scs) if scs else 0
        print(f"[{surface['id']}] {len(scs)} scenarios | held_out {held} ({pct:.0f}%)")
        for area, n in sorted(by_area.items()):
            print(f"    {area:<28} {n}")
        print()

    if errors:
        print("VALIDATION ERRORS:")
        for e in errors:
            print(f"  - {e}")
        return 1
    print("validation: OK")
    return 0


def list_scenarios(manifest: dict[str, Any], args: argparse.Namespace) -> int:
    for sc in all_scenarios(manifest):
        if args.surface and sc["_surface"] != args.surface:
            continue
        if args.difficulty and sc.get("difficulty") != args.difficulty:
            continue
        if args.include_held_out is False and sc.get("held_out"):
            continue
        held = " [held-out]" if sc.get("held_out") else ""
        print(f"{sc['id']:<40} {sc['_surface']:<7} {sc['difficulty']:<10} "
              f"-> {sc['expected_behavior']}{held}")
    return 0


# ---------------------------------------------------------------------------
# Bounded measured sample (opt-in only). Default budgets keep total <= 30.
# ---------------------------------------------------------------------------

# Studio: ~2-3 per capability across the difficulty range (not held-out).
STUDIO_SAMPLE = [
    "studio-workflow-01-buffer-happy",
    "studio-workflow-02-multistep-chain",
    "studio-workflow-06-vague-clarify",
    "studio-map-01-single-layer-happy",
    "studio-map-04-vague-clarify",
    "studio-dashboard-02-timeseries-multichart",
    "studio-report-02-multisection-embedded",
    "studio-form-01-survey-happy",
    "studio-form-03-conditional",
    "studio-analysis-02-spatial-join",
    "studio-analysis-05-vague",
    "studio-query-01-attribute-happy",
    "studio-query-03-combined-temporal",
    "studio-app-01-viewer-happy",
]

# DevOps: ~4-6 read-only / proposal-only scenarios (no actuation).
DEVOPS_SAMPLE = [
    "devops-describe-01-happy",
    "devops-diagnose-01-failed-health",
    "devops-explain-release-01",
    "devops-describe-env-drift-01",
    "devops-gitops-proposal-01",
    "devops-offscope-01-refuse",
]

STUDIO_ENDPOINTS = {
    "workflow": ("/api/v1/console/workflow-packages/generate", {}),
    "map": ("/api/v1/studio/map-packages/generate", {}),
    "app": ("/api/v1/studio/app-packages/generate", {}),
    "dashboard": ("/api/v1/console/publications/generate", {"kind": "dashboard"}),
    "report": ("/api/v1/console/publications/generate", {"kind": "report"}),
    "form": ("/api/v1/admin/forms/packages/generate", {}),
    "analysis": ("/api/v1/analysis/content/generate", {}),
    "query": ("/api/v1/analysis/content/queries/generate", {}),
}


def run_studio(scenario: dict[str, Any], base_url: str, admin_key: str) -> dict[str, Any]:
    import urllib.request
    import urllib.error

    path, extra = STUDIO_ENDPOINTS[scenario["capability"]]
    # Omit provider so the demo backend uses its configured default (bedrock /
    # claude-sonnet-4-5). Override via HONUA_STUDIO_PROVIDER if needed.
    body = {"prompt": scenario["prompt"]}
    if os.environ.get("HONUA_STUDIO_PROVIDER"):
        body["provider"] = os.environ["HONUA_STUDIO_PROVIDER"]
    body.update(extra)
    req = urllib.request.Request(
        base_url.rstrip("/") + path,
        data=json.dumps(body).encode(),
        method="POST",
        headers={"Content-Type": "application/json", "X-API-Key": admin_key},
    )
    started = time.time()
    try:
        with urllib.request.urlopen(req, timeout=120) as resp:
            payload = json.loads(resp.read().decode())
            status = payload.get("status", "?")
            return {"ok": True, "http": resp.status, "status": status,
                    "clarifications": len(payload.get("clarifications") or []),
                    "unmapped": len(payload.get("unmappedRequests") or []),
                    "elapsed": round(time.time() - started, 1)}
    except urllib.error.HTTPError as e:
        return {"ok": False, "http": e.code, "error": e.read().decode()[:300],
                "elapsed": round(time.time() - started, 1)}
    except Exception as e:  # noqa: BLE001
        return {"ok": False, "error": str(e)[:300], "elapsed": round(time.time() - started, 1)}


def run_devops(scenario: dict[str, Any], agent_dir: Path) -> dict[str, Any]:
    env = dict(os.environ)
    env.setdefault("HONUA_DEVOPS_PROVIDER", "claude")
    env.setdefault("HONUA_DEVOPS_EXECUTION_MODE", "plan")
    env.setdefault("HONUA_DEVOPS_APPROVAL_MODE", "pr-first")
    cmd = ["dotnet", "run", "--project", "src/Honua.DevOps.Agent", "--",
           "--provider", "claude", "--prompt", scenario["prompt"]]
    started = time.time()
    try:
        proc = subprocess.run(cmd, cwd=str(agent_dir), env=env,
                              capture_output=True, text=True, timeout=300)
        return {"ok": proc.returncode == 0, "exit": proc.returncode,
                "stdout": proc.stdout[-2000:], "stderr": proc.stderr[-500:],
                "elapsed": round(time.time() - started, 1)}
    except subprocess.TimeoutExpired:
        return {"ok": False, "error": "timeout", "elapsed": round(time.time() - started, 1)}


def run_sample(manifest: dict[str, Any], args: argparse.Namespace) -> int:
    index = {sc["id"]: sc for sc in all_scenarios(manifest)}
    selected = [index[i] for i in (STUDIO_SAMPLE + DEVOPS_SAMPLE) if i in index]
    if len(selected) > MAX_SAMPLE_CALLS:
        print(f"refusing: sample {len(selected)} exceeds budget {MAX_SAMPLE_CALLS}")
        return 2
    for sc in selected:
        if sc.get("held_out"):
            print(f"refusing: held-out scenario {sc['id']} must not be in the sample")
            return 2

    out_dir = Path(args.out)
    out_dir.mkdir(parents=True, exist_ok=True)
    base_url = os.environ.get("HONUA_STUDIO_BASE_URL", "https://demo.honua.io")
    admin_key = os.environ.get("HONUA_STUDIO_ADMIN_KEY", "")
    agent_dir = Path(os.environ.get("HONUA_DEVOPS_AGENT_DIR", HERE.parents[1]))

    results = []
    print(f"running bounded sample: {len(selected)} scenarios (budget {MAX_SAMPLE_CALLS})\n")
    for sc in selected:
        print(f"  {sc['id']} ...", flush=True)
        if sc["_surface"] == "studio":
            if not admin_key:
                r = {"ok": False, "error": "HONUA_STUDIO_ADMIN_KEY unset"}
            else:
                r = run_studio(sc, base_url, admin_key)
        else:
            r = run_devops(sc, agent_dir)
        results.append({"id": sc["id"], "surface": sc["_surface"],
                        "expected_behavior": sc["expected_behavior"], "result": r})
        print(f"    -> {r}")

    (out_dir / "results.json").write_text(json.dumps(results, indent=2))
    print(f"\nwrote {out_dir / 'results.json'}")
    return 0


def main() -> int:
    p = argparse.ArgumentParser(description=__doc__,
                                formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--validate", action="store_true", help="Validate + print coverage (no AI).")
    p.add_argument("--list", action="store_true", help="List scenarios (no AI).")
    p.add_argument("--surface", choices=["studio", "devops"])
    p.add_argument("--difficulty")
    p.add_argument("--include-held-out", dest="include_held_out", action="store_true", default=True)
    p.add_argument("--exclude-held-out", dest="include_held_out", action="store_false")
    p.add_argument("--run-sample", action="store_true",
                   help="Opt-in BOUNDED measured run (<= 30 model calls, no retries).")
    p.add_argument("--out", default="artifacts/sample-run")
    args = p.parse_args()

    manifest = load_corpus()
    if args.run_sample:
        return run_sample(manifest, args)
    if args.list:
        return list_scenarios(manifest, args)
    # default + --validate: zero-AI validation/coverage
    return validate(manifest)


if __name__ == "__main__":
    sys.exit(main())
