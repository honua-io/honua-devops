#!/usr/bin/env python3

from __future__ import annotations

import argparse
import json
import os
import shlex
import subprocess
import sys
from datetime import datetime, timezone
from pathlib import Path
from typing import Any


STATUS_PASSED = "passed"
STATUS_PASSED_WITH_SKIPS = "passed-with-skips"
STATUS_FAILED = "failed"
STATUS_SKIPPED = "skipped"

# Per-lane wall-clock budget. A single hung model invocation must not block the whole serial
# run with no diagnostics; on expiry the lane is recorded as FAILED. Overridable per-lane
# (matrix `timeoutSeconds`) or globally via HONUA_EVAL_LANE_TIMEOUT_SECONDS.
DEFAULT_LANE_TIMEOUT_SECONDS = 900
LANE_TIMEOUT_ENV = "HONUA_EVAL_LANE_TIMEOUT_SECONDS"


def resolve_lane_timeout(lane: dict[str, Any]) -> int:
    raw = os.environ.get(LANE_TIMEOUT_ENV, "").strip()
    if not raw:
        raw = str(lane.get("timeoutSeconds") or "").strip()
    if not raw:
        return DEFAULT_LANE_TIMEOUT_SECONDS
    try:
        value = int(raw)
    except ValueError as exc:
        raise SystemExit(
            f"Invalid lane timeout {raw!r}: must be a positive integer number of seconds."
        ) from exc
    if value < 1:
        raise SystemExit(f"Invalid lane timeout {raw!r}: must be >= 1 second.")
    return value


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Run Honua multi-model operator eval lanes against the canonical server eval report."
    )
    parser.add_argument(
        "--matrix",
        default="eval/model-matrix.json",
        help="Model matrix JSON. Defaults to eval/model-matrix.json.",
    )
    parser.add_argument(
        "--server-report",
        default=None,
        help="Server-side eval-report.json from honua-server. Defaults to matrix serverReport.defaultPath.",
    )
    parser.add_argument(
        "--scenario-dir",
        default=None,
        help="Directory containing server-side eval scenario JSON files. Defaults to matrix serverReport.scenarioDir.",
    )
    parser.add_argument(
        "--output-dir",
        default="artifacts/multi-model-operator-evals",
        help="Directory for report.json and report.md.",
    )
    parser.add_argument(
        "--run-lanes",
        action="store_true",
        help="Execute enabled model lane commands. Without this, lane execution is skipped after contract validation.",
    )
    parser.add_argument(
        "--hard-fail",
        action="store_true",
        help="Exit 2 when the server gate or any enabled release-gate lane fails.",
    )
    parser.add_argument(
        "--require-release-gates",
        action="store_true",
        help="With --hard-fail, treat skipped release-gate lanes as failures.",
    )
    return parser.parse_args()


def utc_now() -> str:
    return datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


def load_json(path: Path) -> dict[str, Any]:
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except FileNotFoundError as exc:
        raise SystemExit(f"[ERROR] JSON file not found: {path}") from exc
    except json.JSONDecodeError as exc:
        raise SystemExit(f"[ERROR] Invalid JSON in {path}: {exc}") from exc


def resolve_path(value: str, base: Path) -> Path:
    path = Path(value)
    if path.is_absolute():
        return path
    return (base / path).resolve()


def truthy_env(name: str | None) -> bool:
    if not name:
        return False
    value = os.environ.get(name, "").strip().lower()
    return value in {"1", "true", "yes", "on"}


def load_scenarios(scenario_dir: Path | None) -> dict[str, dict[str, Any]]:
    if scenario_dir is None:
        return {}
    if not scenario_dir.exists():
        raise SystemExit(f"[ERROR] Scenario directory not found: {scenario_dir}")

    scenarios: dict[str, dict[str, Any]] = {}
    for path in sorted(scenario_dir.glob("*.json")):
        payload = load_json(path)
        scenario_id = payload.get("id")
        if isinstance(scenario_id, str) and scenario_id:
            scenarios[scenario_id] = {
                "path": str(path),
                "name": payload.get("name", scenario_id),
                "mode": payload.get("mode", ""),
                "goal": payload.get("intent", {}).get("goal", ""),
                "expectsMapPackage": payload.get("expectedOutcome", {}).get("expectsMapPackage"),
                "expectsAppPackage": payload.get("expectedOutcome", {}).get("expectsAppPackage"),
            }
    if not scenarios:
        raise SystemExit(f"[ERROR] Scenario directory contains no scenario JSON files: {scenario_dir}")
    return scenarios


def normalize_server_status(value: str) -> str:
    normalized = value.strip().lower().replace("_", "-")
    if normalized == "passed":
        return STATUS_PASSED
    if normalized == "passedwithskips" or normalized == "passed-with-skips":
        return STATUS_PASSED_WITH_SKIPS
    if normalized == "failed":
        return STATUS_FAILED
    if normalized == "skipped":
        return STATUS_SKIPPED
    return normalized or "unknown"


def normalize_lane_status(value: str) -> str:
    normalized = value.strip().lower().replace("_", "-")
    if normalized in {"pass", STATUS_PASSED}:
        return STATUS_PASSED
    if normalized in {"passedwithskips", STATUS_PASSED_WITH_SKIPS}:
        return STATUS_PASSED_WITH_SKIPS
    if normalized in {"fail", STATUS_FAILED}:
        return STATUS_FAILED
    if normalized == STATUS_SKIPPED:
        return STATUS_SKIPPED
    return normalized or "unknown"


def summarize_server_report(
    report: dict[str, Any],
    report_path: Path,
    scenarios: dict[str, dict[str, Any]],
    allowed_statuses: set[str],
) -> dict[str, Any]:
    scenario_rows: list[dict[str, Any]] = []
    failed: list[str] = []
    skipped: list[str] = []
    missing_fixture: list[str] = []

    for scenario in report.get("scenarios", []):
        scenario_id = str(scenario.get("id", ""))
        status = str(scenario.get("status", ""))
        normalized_status = normalize_server_status(status)
        if normalized_status not in allowed_statuses:
            failed.append(scenario_id or "<missing-id>")
        if normalized_status == STATUS_PASSED_WITH_SKIPS:
            skipped.append(scenario_id)
        if scenarios and scenario_id not in scenarios:
            missing_fixture.append(scenario_id)

        stage_counts: dict[str, int] = {}
        for stage in scenario.get("stages", []):
            stage_status = str(stage.get("status", "unknown")).lower()
            stage_counts[stage_status] = stage_counts.get(stage_status, 0) + 1

        scenario_rows.append(
            {
                "id": scenario_id,
                "name": scenario.get("name", scenario_id),
                "mode": scenario.get("mode", ""),
                "status": normalized_status,
                "rawStatus": status,
                "stageCounts": stage_counts,
                "fixture": scenarios.get(scenario_id),
            }
        )

    status = STATUS_FAILED if failed or missing_fixture else STATUS_PASSED
    if status == STATUS_PASSED and skipped:
        status = STATUS_PASSED_WITH_SKIPS

    rollup = report.get("rollup") or {}
    if not rollup:
        rollup = {
            "total": len(scenario_rows),
            "passed": sum(1 for row in scenario_rows if row["status"] == STATUS_PASSED),
            "failed": sum(1 for row in scenario_rows if row["status"] == STATUS_FAILED),
            "passedWithSkips": sum(1 for row in scenario_rows if row["status"] == STATUS_PASSED_WITH_SKIPS),
        }

    return {
        "path": str(report_path),
        "reportSchemaVersion": report.get("reportSchemaVersion"),
        "status": status,
        "generatedAt": report.get("generatedAt"),
        "environment": report.get("environment", {}),
        "rollup": rollup,
        "scenarioCount": len(scenario_rows),
        "scenarios": scenario_rows,
        "failures": failed,
        "missingFixtures": missing_fixture,
    }


def build_lane_prompt(scenario: dict[str, Any], server_summary: dict[str, Any]) -> str:
    fixture = scenario.get("fixture") or {}
    lines = [
        "Evaluate this Honua operator workflow scenario.",
        f"Scenario id: {scenario['id']}",
        f"Name: {scenario['name']}",
        f"Mode: {scenario['mode']}",
        f"Goal: {fixture.get('goal', '')}",
        f"Server scenario status: {scenario['status']}",
        f"Server corpus: {server_summary['environment'].get('corpusVersion', 'unknown')}",
        "",
        "Return JSON with status and optional metrics for clarificationQuality, planValidity, executionSuccess, resultCorrectness, and packageUsefulness.",
    ]
    return "\n".join(lines)


def run_lane_command(
    lane: dict[str, Any],
    scenario: dict[str, Any],
    server_report_path: Path,
    output_dir: Path,
    command: str,
    model_value: str,
    server_summary: dict[str, Any],
) -> dict[str, Any]:
    scenario_id = scenario["id"]
    lane_id = lane["id"]
    lane_dir = output_dir / "lanes" / lane_id
    lane_dir.mkdir(parents=True, exist_ok=True)
    prompt_path = lane_dir / f"{scenario_id}.prompt.txt"
    lane_output_path = lane_dir / f"{scenario_id}.result.json"
    stdout_path = lane_dir / f"{scenario_id}.stdout.txt"
    stderr_path = lane_dir / f"{scenario_id}.stderr.txt"
    prompt_path.write_text(build_lane_prompt(scenario, server_summary), encoding="utf-8")

    env = os.environ.copy()
    env.update(
        {
            "HONUA_EVAL_LANE_ID": lane_id,
            "HONUA_EVAL_SCENARIO_ID": scenario_id,
            "HONUA_EVAL_SCENARIO_MODE": str(scenario.get("mode", "")),
            "HONUA_EVAL_SCENARIO_PATH": str((scenario.get("fixture") or {}).get("path", "")),
            "HONUA_EVAL_SERVER_REPORT": str(server_report_path),
            "HONUA_EVAL_PROMPT_PATH": str(prompt_path),
            "HONUA_EVAL_LANE_OUTPUT": str(lane_output_path),
            "HONUA_EVAL_MODEL": model_value,
        }
    )

    replacements = {
        "{lane_id}": shlex.quote(lane_id),
        "{scenario_id}": shlex.quote(scenario_id),
        "{scenario_path}": shlex.quote(env["HONUA_EVAL_SCENARIO_PATH"]),
        "{server_report}": shlex.quote(str(server_report_path)),
        "{prompt_path}": shlex.quote(str(prompt_path)),
        "{lane_output}": shlex.quote(str(lane_output_path)),
        "{model}": shlex.quote(model_value),
    }
    formatted_command = command
    for token, replacement in replacements.items():
        formatted_command = formatted_command.replace(token, replacement)

    # Stream stdout/stderr straight to the lane log files (instead of buffering the whole
    # output in memory) and enforce a per-lane timeout so one hung invocation cannot block the
    # serial run with no diagnostics.
    timeout_seconds = resolve_lane_timeout(lane)
    timed_out = False
    returncode: int | None
    with stdout_path.open("w", encoding="utf-8") as stdout_file, \
            stderr_path.open("w", encoding="utf-8") as stderr_file:
        try:
            completed = subprocess.run(
                formatted_command,
                shell=True,
                check=False,
                text=True,
                stdout=stdout_file,
                stderr=stderr_file,
                env=env,
                timeout=timeout_seconds,
            )
            returncode = completed.returncode
        except subprocess.TimeoutExpired:
            timed_out = True
            returncode = None
            stderr_file.write(
                f"\n[eval-runner] lane '{lane_id}' scenario '{scenario_id}' timed out "
                f"after {timeout_seconds}s and was terminated.\n"
            )

    # A usable result requires a non-empty, parseable result file.
    payload: dict[str, Any] = {}
    result_present = lane_output_path.exists() and lane_output_path.stat().st_size > 0
    if result_present:
        try:
            payload = load_json(lane_output_path)
        except (json.JSONDecodeError, ValueError):
            payload = {}
            result_present = False

    findings = list(payload.get("findings", []))
    if timed_out:
        status = STATUS_FAILED
        findings.append(f"Lane timed out after {timeout_seconds}s; recorded as failed.")
    elif returncode != 0:
        status = STATUS_FAILED
    elif not result_present:
        # CRITICAL: a bare exit 0 with no usable result file must NOT be credited as a pass.
        # Otherwise a release-gate lane goes green without actually emitting any verdict.
        status = STATUS_FAILED
        findings.append(
            "Lane exited 0 but produced no usable result JSON; not credited as passed."
        )
    else:
        # Present-but-statusless results also fail closed rather than defaulting to passed.
        status = normalize_lane_status(str(payload.get("status") or STATUS_FAILED))

    return {
        "scenarioId": scenario_id,
        "status": status,
        "exitCode": returncode,
        "timedOut": timed_out,
        "metrics": payload.get("metrics", {}),
        "findings": findings,
        "artifacts": payload.get("artifacts", []),
        "stdoutPath": str(stdout_path),
        "stderrPath": str(stderr_path),
        "resultPath": str(lane_output_path) if result_present else None,
    }


def run_lane(
    lane: dict[str, Any],
    scenarios: list[dict[str, Any]],
    server_report_path: Path,
    output_dir: Path,
    run_lanes: bool,
    server_summary: dict[str, Any],
) -> dict[str, Any]:
    enabled = truthy_env(lane.get("enabledEnv"))
    command = os.environ.get(lane.get("commandEnv", ""), "").strip() if lane.get("commandEnv") else ""
    model_value = os.environ.get(lane.get("modelEnv", ""), "").strip() if lane.get("modelEnv") else ""

    lane_result: dict[str, Any] = {
        "id": lane["id"],
        "displayName": lane.get("displayName", lane["id"]),
        "role": lane.get("role", "portability"),
        "provider": lane.get("provider", lane["id"]),
        "requiredForRelease": bool(lane.get("requiredForRelease")),
        "enabled": enabled,
        "model": model_value,
        "scenarioResults": [],
    }

    if not run_lanes:
        lane_result["status"] = STATUS_SKIPPED
        lane_result["reason"] = "contract-only"
        lane_result["scenarioResults"] = [
            {"scenarioId": scenario["id"], "status": STATUS_SKIPPED, "reason": "contract-only"}
            for scenario in scenarios
        ]
        return lane_result

    if not enabled:
        lane_result["status"] = STATUS_SKIPPED
        lane_result["reason"] = "lane-disabled"
        lane_result["scenarioResults"] = [
            {"scenarioId": scenario["id"], "status": STATUS_SKIPPED, "reason": "lane-disabled"}
            for scenario in scenarios
        ]
        return lane_result

    if not command:
        lane_result["status"] = STATUS_FAILED
        lane_result["reason"] = f"command-env-missing:{lane.get('commandEnv')}"
        lane_result["scenarioResults"] = [
            {
                "scenarioId": scenario["id"],
                "status": STATUS_FAILED,
                "reason": lane_result["reason"],
            }
            for scenario in scenarios
        ]
        return lane_result

    results = [
        run_lane_command(lane, scenario, server_report_path, output_dir, command, model_value, server_summary)
        for scenario in scenarios
    ]
    lane_result["scenarioResults"] = results
    lane_result["rollup"] = rollup_status_counts(results)

    if any(result["status"] == STATUS_FAILED for result in results):
        lane_result["status"] = STATUS_FAILED
    elif any(result["status"] in {STATUS_PASSED_WITH_SKIPS, STATUS_SKIPPED} for result in results):
        lane_result["status"] = STATUS_PASSED_WITH_SKIPS
    else:
        lane_result["status"] = STATUS_PASSED

    return lane_result


def rollup_status_counts(rows: list[dict[str, Any]]) -> dict[str, int]:
    rollup = {STATUS_PASSED: 0, STATUS_PASSED_WITH_SKIPS: 0, STATUS_FAILED: 0, STATUS_SKIPPED: 0}
    for row in rows:
        status = row.get("status", "unknown")
        rollup[status] = rollup.get(status, 0) + 1
    return rollup


def build_report(
    matrix: dict[str, Any],
    matrix_path: Path,
    server_summary: dict[str, Any],
    lane_results: list[dict[str, Any]],
) -> dict[str, Any]:
    release_lanes = [lane for lane in lane_results if lane.get("role") == "release-gate"]
    portability_lanes = [lane for lane in lane_results if lane.get("role") == "portability"]

    release_gate_status = summarize_group_status(release_lanes)
    portability_status = summarize_group_status(portability_lanes)
    overall_status = STATUS_FAILED if server_summary["status"] == STATUS_FAILED or release_gate_status == STATUS_FAILED else STATUS_PASSED
    if overall_status == STATUS_PASSED and (
        server_summary["status"] == STATUS_PASSED_WITH_SKIPS or release_gate_status in {STATUS_SKIPPED, STATUS_PASSED_WITH_SKIPS}
    ):
        overall_status = STATUS_PASSED_WITH_SKIPS

    return {
        "schemaVersion": "1",
        "generatedAtUtc": utc_now(),
        "matrixPath": str(matrix_path),
        "serverEval": server_summary,
        "scoreDimensions": matrix.get("scoreDimensions", []),
        "lanes": lane_results,
        "rollup": {
            "status": overall_status,
            "releaseGateStatus": release_gate_status,
            "portabilityStatus": portability_status,
            "totalLanes": len(lane_results),
            "releaseGateLanes": len(release_lanes),
            "portabilityLanes": len(portability_lanes),
        },
    }


def summarize_group_status(lanes: list[dict[str, Any]]) -> str:
    if not lanes:
        return STATUS_SKIPPED
    statuses = {lane.get("status") for lane in lanes}
    if STATUS_FAILED in statuses:
        return STATUS_FAILED
    if STATUS_PASSED in statuses and statuses <= {STATUS_PASSED, STATUS_PASSED_WITH_SKIPS}:
        return STATUS_PASSED_WITH_SKIPS if STATUS_PASSED_WITH_SKIPS in statuses else STATUS_PASSED
    if STATUS_PASSED in statuses or STATUS_PASSED_WITH_SKIPS in statuses:
        return STATUS_PASSED_WITH_SKIPS
    return STATUS_SKIPPED


def render_markdown(report: dict[str, Any]) -> str:
    lines = [
        "# Multi-Model Operator Eval Report",
        "",
        f"Generated: `{report['generatedAtUtc']}`",
        f"Overall status: `{report['rollup']['status']}`",
        f"Server eval status: `{report['serverEval']['status']}`",
        f"Release gate status: `{report['rollup']['releaseGateStatus']}`",
        f"Portability status: `{report['rollup']['portabilityStatus']}`",
        "",
        "## Server Scenarios",
        "",
        "| Scenario | Mode | Status | Fixture |",
        "| --- | --- | --- | --- |",
    ]
    for scenario in report["serverEval"]["scenarios"]:
        fixture = "yes" if scenario.get("fixture") else "n/a"
        lines.append(f"| `{scenario['id']}` | `{scenario['mode']}` | `{scenario['status']}` | {fixture} |")

    lines.extend(
        [
            "",
            "## Model Lanes",
            "",
            "| Lane | Role | Enabled | Status | Model |",
            "| --- | --- | --- | --- | --- |",
        ]
    )
    for lane in report["lanes"]:
        lines.append(
            f"| `{lane['id']}` | `{lane['role']}` | `{str(lane['enabled']).lower()}` | `{lane['status']}` | `{lane.get('model', '')}` |"
        )

    lines.extend(
        [
            "",
            "## Guidance",
            "",
            "- Claude and Codex lanes are release gates when enabled.",
            "- Local Llama is a portability/regression lane; it does not replace hosted release gates.",
            "- Lane commands should emit JSON to `HONUA_EVAL_LANE_OUTPUT` with status, metrics, findings, and artifacts.",
            "",
        ]
    )
    return "\n".join(lines)


def should_exit_hard(report: dict[str, Any], require_release_gates: bool) -> bool:
    if report["serverEval"]["status"] == STATUS_FAILED:
        return True
    for lane in report["lanes"]:
        if lane.get("role") != "release-gate":
            continue
        if lane.get("status") == STATUS_FAILED:
            return True
        if require_release_gates and lane.get("status") == STATUS_SKIPPED:
            return True
    return False


def main() -> int:
    args = parse_args()
    repo_root = Path.cwd()
    matrix_path = resolve_path(args.matrix, repo_root)
    matrix = load_json(matrix_path)

    server_report_value = args.server_report or os.environ.get("HONUA_EVAL_SERVER_REPORT") or matrix["serverReport"]["defaultPath"]
    scenario_dir_value = args.scenario_dir or os.environ.get("HONUA_EVAL_SCENARIO_DIR") or matrix["serverReport"].get("scenarioDir")
    server_report_path = resolve_path(server_report_value, repo_root)
    scenario_dir = resolve_path(scenario_dir_value, repo_root) if scenario_dir_value else None
    output_dir = resolve_path(args.output_dir, repo_root)
    output_dir.mkdir(parents=True, exist_ok=True)

    server_report = load_json(server_report_path)
    scenarios = load_scenarios(scenario_dir)
    allowed_statuses = {
        normalize_server_status(str(status))
        for status in matrix.get("serverReport", {}).get("allowedScenarioStatuses", ["Passed", "PassedWithSkips"])
    }
    server_summary = summarize_server_report(server_report, server_report_path, scenarios, allowed_statuses)
    scenario_rows = server_summary["scenarios"]

    if not scenario_rows:
        raise SystemExit("[ERROR] Server eval report does not contain scenarios.")

    lane_results = [
        run_lane(lane, scenario_rows, server_report_path, output_dir, args.run_lanes, server_summary)
        for lane in matrix.get("lanes", [])
    ]

    report = build_report(matrix, matrix_path, server_summary, lane_results)
    report_json = output_dir / "report.json"
    report_md = output_dir / "report.md"
    report_json.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    report_md.write_text(render_markdown(report), encoding="utf-8")

    print(f"[RESULT] Multi-model operator eval status: {report['rollup']['status']}")
    print(f"[RESULT] Report JSON: {report_json}")
    print(f"[RESULT] Report Markdown: {report_md}")

    if args.hard_fail and should_exit_hard(report, args.require_release_gates):
        return 2
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except KeyboardInterrupt:
        raise SystemExit(130)
