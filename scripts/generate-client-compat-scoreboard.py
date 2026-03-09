#!/usr/bin/env python3

from __future__ import annotations

import argparse
import json
from collections import defaultdict
from datetime import datetime, timezone
from html import escape
from pathlib import Path
from typing import Any


STATUS_PRIORITY = {"fail": 3, "pending": 2, "warn": 2, "pass": 1}


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Generate a static compatibility scoreboard from Honua client compatibility packs."
    )
    parser.add_argument(
        "--packs-root",
        required=True,
        help="Root directory containing release-pack folders with evidence/session.json and optional compatibility-results.json.",
    )
    parser.add_argument(
        "--catalog",
        default="compatibility/clients.catalog.json",
        help="Client catalog JSON path.",
    )
    parser.add_argument(
        "--output-dir",
        required=True,
        help="Output directory for generated scoreboard assets.",
    )
    parser.add_argument(
        "--hard-fail",
        action="store_true",
        help="Exit non-zero when the latest release contains any failing client or protocol status.",
    )
    return parser.parse_args()


def load_json(path: Path) -> dict[str, Any]:
    return json.loads(path.read_text(encoding="utf-8"))


def ensure_client_map(catalog: dict[str, Any]) -> dict[str, dict[str, Any]]:
    client_map: dict[str, dict[str, Any]] = {}
    for client in catalog["clients"]:
        client_map[client["name"]] = {
            "name": client["name"],
            "version": "",
            "status": "pending",
            "notes": "",
            "protocols": [{"name": protocol, "status": "pending"} for protocol in client["protocols"]],
        }
    return client_map


def merge_session(client_map: dict[str, dict[str, Any]], session: dict[str, Any]) -> None:
    for session_client in session.get("clients", []):
        name = session_client["name"]
        if name not in client_map:
            continue
        status = session_client.get("status", "pending")
        client_map[name]["version"] = session_client.get("version", client_map[name]["version"])
        client_map[name]["status"] = status
        client_map[name]["notes"] = session_client.get("notes", client_map[name]["notes"])
        for protocol in client_map[name]["protocols"]:
            protocol["status"] = status


def merge_overlay(client_map: dict[str, dict[str, Any]], overlay: dict[str, Any]) -> None:
    for overlay_client in overlay.get("clients", []):
        name = overlay_client["name"]
        if name not in client_map:
            continue

        client_map[name]["version"] = overlay_client.get("version", client_map[name]["version"])
        client_map[name]["status"] = overlay_client.get("status", client_map[name]["status"])
        client_map[name]["notes"] = overlay_client.get("notes", client_map[name]["notes"])

        protocols_by_name = {protocol["name"]: protocol for protocol in client_map[name]["protocols"]}
        for protocol in overlay_client.get("protocols", []):
            if protocol["name"] in protocols_by_name:
                protocols_by_name[protocol["name"]]["status"] = protocol.get("status", "pending")


def aggregate_status(values: list[str]) -> str:
    if not values:
        return "pending"
    return max(values, key=lambda value: STATUS_PRIORITY.get(value, 0))


def build_release_matrix(services: list[dict[str, Any]], catalog: dict[str, Any]) -> list[dict[str, Any]]:
    matrix: list[dict[str, Any]] = []
    for catalog_client in catalog["clients"]:
        client_name = catalog_client["name"]
        protocol_rows = []
        client_statuses = []
        for protocol_name in catalog_client["protocols"]:
            statuses = []
            for service in services:
                service_client = next(item for item in service["clients"] if item["name"] == client_name)
                protocol = next(item for item in service_client["protocols"] if item["name"] == protocol_name)
                statuses.append(protocol["status"])
                client_statuses.append(service_client["status"])
            protocol_rows.append({"name": protocol_name, "status": aggregate_status(statuses)})

        matrix.append(
            {
                "client": client_name,
                "status": aggregate_status(client_statuses + [protocol["status"] for protocol in protocol_rows]),
                "protocols": protocol_rows,
            }
        )
    return matrix


def build_summary(matrix: list[dict[str, Any]]) -> dict[str, int]:
    summary = {"pass": 0, "pending": 0, "fail": 0}
    for client in matrix:
        status = client["status"]
        summary[status] = summary.get(status, 0) + 1
    return summary


def build_diff(current: list[dict[str, Any]], previous: list[dict[str, Any]]) -> list[dict[str, str]]:
    previous_map = {
        (client["client"], protocol["name"]): protocol["status"]
        for client in previous
        for protocol in client["protocols"]
    }
    changes: list[dict[str, str]] = []
    for client in current:
        for protocol in client["protocols"]:
            key = (client["client"], protocol["name"])
            old_status = previous_map.get(key)
            new_status = protocol["status"]
            if old_status is not None and old_status != new_status:
                changes.append(
                    {
                        "client": client["client"],
                        "protocol": protocol["name"],
                        "from": old_status,
                        "to": new_status,
                    }
                )
    return changes


def build_badge(latest_release: dict[str, Any]) -> dict[str, Any]:
    summary = latest_release["summary"]
    if summary.get("fail", 0) > 0:
        color = "red"
    elif summary.get("pending", 0) > 0:
        color = "yellow"
    else:
        color = "green"

    return {
        "schemaVersion": 1,
        "label": "compatibility",
        "message": f"{summary.get('pass', 0)} pass / {summary.get('pending', 0)} pending / {summary.get('fail', 0)} fail",
        "color": color,
    }


def discover_services(packs_root: Path, catalog: dict[str, Any]) -> dict[str, list[dict[str, Any]]]:
    releases: dict[str, list[dict[str, Any]]] = defaultdict(list)
    for pack_dir in sorted(path for path in packs_root.glob("*/*") if path.is_dir()):
        session_path = pack_dir / "evidence" / "session.json"
        overlay_path = pack_dir / "compatibility-results.json"
        if not session_path.exists() and not overlay_path.exists():
            continue

        client_map = ensure_client_map(catalog)
        session = load_json(session_path) if session_path.exists() else {}
        overlay = load_json(overlay_path) if overlay_path.exists() else {}

        if session:
            merge_session(client_map, session)
        if overlay:
            merge_overlay(client_map, overlay)

        release_name = overlay.get("release", pack_dir.parent.name)
        releases[release_name].append(
            {
                "release": release_name,
                "release_date": overlay.get("release_date", pack_dir.parent.name),
                "service_id": overlay.get("service_id", session.get("service_id", pack_dir.name)),
                "service_title": overlay.get("service_title", session.get("service_title", pack_dir.name)),
                "source_pack": overlay.get("source_pack", str(pack_dir.relative_to(packs_root.parent))),
                "clients": list(client_map.values()),
            }
        )
    return dict(releases)


def build_scoreboard(release_services: dict[str, list[dict[str, Any]]], catalog: dict[str, Any]) -> dict[str, Any]:
    releases_output: list[dict[str, Any]] = []
    previous_matrix: list[dict[str, Any]] = []

    for release_name in sorted(release_services.keys()):
        services = release_services[release_name]
        matrix = build_release_matrix(services, catalog)
        summary = build_summary(matrix)
        diff = build_diff(matrix, previous_matrix) if previous_matrix else []
        releases_output.append(
            {
                "release": release_name,
                "release_date": services[0]["release_date"],
                "services": services,
                "summary": summary,
                "matrix": matrix,
                "changed_from_previous": diff,
            }
        )
        previous_matrix = matrix

    return {
        "generated_at_utc": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
        "releases": list(reversed(releases_output)),
    }


def render_markdown(scoreboard: dict[str, Any]) -> str:
    lines = ["# Client Compatibility Scoreboard", ""]
    lines.append(f"Generated: `{scoreboard['generated_at_utc']}`")
    lines.append("")

    for release in scoreboard["releases"]:
        lines.append(f"## Release {release['release']}")
        lines.append("")
        summary = release["summary"]
        lines.append(
            f"Summary: pass={summary.get('pass', 0)}, pending={summary.get('pending', 0)}, fail={summary.get('fail', 0)}"
        )
        lines.append("")
        lines.append("| Client | Overall | Protocol Status |")
        lines.append("| --- | --- | --- |")
        for client in release["matrix"]:
            protocol_summary = ", ".join(
                f"{protocol['name']}: {protocol['status']}" for protocol in client["protocols"]
            )
            lines.append(f"| {client['client']} | {client['status']} | {protocol_summary} |")
        lines.append("")

        if release["changed_from_previous"]:
            lines.append("### Changes Since Previous Release")
            lines.append("")
            for change in release["changed_from_previous"]:
                lines.append(
                    f"- {change['client']} / {change['protocol']}: {change['from']} -> {change['to']}"
                )
            lines.append("")

    return "\n".join(lines) + "\n"


def render_html(scoreboard: dict[str, Any]) -> str:
    sections: list[str] = []
    for release in scoreboard["releases"]:
        rows = []
        for client in release["matrix"]:
            protocol_summary = "<br>".join(
                f"{escape(protocol['name'])}: <strong>{escape(protocol['status'])}</strong>"
                for protocol in client["protocols"]
            )
            rows.append(
                "<tr>"
                f"<td>{escape(client['client'])}</td>"
                f"<td>{escape(client['status'])}</td>"
                f"<td>{protocol_summary}</td>"
                "</tr>"
            )

        diff_items = ""
        if release["changed_from_previous"]:
            diff_items = "<ul>" + "".join(
                f"<li>{escape(change['client'])} / {escape(change['protocol'])}: {escape(change['from'])} -> {escape(change['to'])}</li>"
                for change in release["changed_from_previous"]
            ) + "</ul>"

        sections.append(
            f"""
            <section>
              <h2>Release {escape(release['release'])}</h2>
              <p>Summary: pass={release['summary'].get('pass', 0)}, pending={release['summary'].get('pending', 0)}, fail={release['summary'].get('fail', 0)}</p>
              <table>
                <thead>
                  <tr><th>Client</th><th>Overall</th><th>Protocol Status</th></tr>
                </thead>
                <tbody>
                  {''.join(rows)}
                </tbody>
              </table>
              {diff_items}
            </section>
            """
        )

    return f"""<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>Honua Compatibility Scoreboard</title>
  <style>
    body {{ font-family: Georgia, serif; margin: 2rem auto; max-width: 1100px; padding: 0 1rem; color: #1d2428; background: linear-gradient(180deg, #f9f6ef 0%, #eef4f5 100%); }}
    h1, h2 {{ font-family: "Iowan Old Style", "Palatino Linotype", serif; }}
    table {{ width: 100%; border-collapse: collapse; margin-bottom: 2rem; background: rgba(255,255,255,0.9); }}
    th, td {{ border: 1px solid #d7dfdf; padding: 0.75rem; text-align: left; vertical-align: top; }}
    th {{ background: #dce9ea; }}
    section {{ margin-bottom: 2rem; }}
    code {{ background: rgba(255,255,255,0.8); padding: 0.1rem 0.3rem; }}
  </style>
</head>
<body>
  <h1>Honua Client Compatibility Scoreboard</h1>
  <p>Generated: <code>{escape(scoreboard['generated_at_utc'])}</code></p>
  {''.join(sections)}
</body>
</html>
"""


def render_rss(scoreboard: dict[str, Any]) -> str:
    items = []
    for release in scoreboard["releases"]:
        if not release["changed_from_previous"]:
            continue
        description = "; ".join(
            f"{change['client']} / {change['protocol']}: {change['from']} -> {change['to']}"
            for change in release["changed_from_previous"]
        )
        items.append(
            f"""
            <item>
              <title>Compatibility changes for {release['release']}</title>
              <description>{escape(description)}</description>
              <pubDate>{escape(release['release_date'])}</pubDate>
              <guid>{escape(release['release'])}</guid>
            </item>
            """
        )
    return f"""<?xml version="1.0" encoding="UTF-8"?>
<rss version="2.0">
  <channel>
    <title>Honua Compatibility Changes</title>
    <description>Per-release compatibility changes for Honua client integrations.</description>
    <link>https://compatibility.honua.dev/</link>
    {''.join(items)}
  </channel>
</rss>
"""


def main() -> int:
    args = parse_args()
    packs_root = Path(args.packs_root).resolve()
    catalog = load_json(Path(args.catalog).resolve())
    output_dir = Path(args.output_dir).resolve()
    output_dir.mkdir(parents=True, exist_ok=True)

    release_services = discover_services(packs_root, catalog)
    scoreboard = build_scoreboard(release_services, catalog)
    latest_release = scoreboard["releases"][0]

    (output_dir / "compatibility-matrix.json").write_text(
        json.dumps(scoreboard, indent=2) + "\n", encoding="utf-8"
    )
    (output_dir / "compatibility-matrix.md").write_text(render_markdown(scoreboard), encoding="utf-8")
    (output_dir / "index.html").write_text(render_html(scoreboard), encoding="utf-8")
    (output_dir / "compatibility-changes.xml").write_text(render_rss(scoreboard), encoding="utf-8")
    (output_dir / "badge.json").write_text(
        json.dumps(build_badge(latest_release), indent=2) + "\n", encoding="utf-8"
    )

    if args.hard_fail and latest_release["summary"].get("fail", 0) > 0:
        return 2

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
