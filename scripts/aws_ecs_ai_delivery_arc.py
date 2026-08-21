#!/usr/bin/env python3
"""Run and seal the candidate-bound AWS ECS AI delivery arc.

This is the cloud-side producer consumed by honua-release.  It deliberately
does not provision or destroy Terraform resources: the caller invokes it after
the existing ECS readiness probe and finalizes its receipts only after the
existing teardown path has completed.  Secrets are resolved into the SDK child
process environment and are never placed in argv, checkpoints, receipts, or
evidence files.
"""
from __future__ import annotations

import argparse
import hashlib
import hmac
import ipaddress
import json
import os
import re
import subprocess
import sys
from pathlib import Path
from typing import Any
from urllib.error import HTTPError, URLError
from urllib.parse import quote, urlencode, urlparse
from urllib.request import Request, urlopen

try:
    import yaml
except ImportError:  # pragma: no cover - surfaced as a bounded runtime refusal
    yaml = None


SHA = re.compile(r"^[0-9a-f]{40}$")
SHA256 = re.compile(r"^[0-9a-f]{64}$")
AWS_SECRET_ARN = re.compile(
    r"^arn:aws(?:-us-gov|-cn)?:secretsmanager:[a-z0-9-]+:[0-9]{12}:secret:[A-Za-z0-9/_+=.@-]+$"
)
ENVIRONMENT_NAME = re.compile(r"^[A-Z][A-Z0-9_]{0,127}$")
SDK_CHILD_ENVIRONMENT_KEYS = {
    "PATH", "HOME", "USERPROFILE", "HOMEDRIVE", "HOMEPATH", "SYSTEMROOT",
    "WINDIR", "COMSPEC", "PATHEXT", "TEMP", "TMP", "TMPDIR", "LANG",
    "LC_ALL", "TZ", "CI", "NODE_OPTIONS", "NODE_PATH", "SSL_CERT_FILE",
    "SSL_CERT_DIR",
}
HANDOFF_SCHEMA = "honua.mcp-proxy.handoff/v1"
PROVISION_BINDING_SCHEMA = "honua.aws-ecs.provision-binding/v1"
TEARDOWN_SCHEMA = "honua.aws-ecs.teardown-evidence/v1"
RECEIPT_SCHEMA = "honua.release.evidence-receipt/v1"
CHECKPOINT_SCHEMA = "honua.zero-to-map.checkpoint/v1"
JOURNEY_RECEIPT_SCHEMA = "honua.zero-to-map.receipt/v1"
REAL_MODEL_RECEIPT_SCHEMA = "honua.aws-ecs.real-model-ai-arc/v1"
REAL_MODEL_EVIDENCE_SCHEMA = "honua.aws-ecs.real-model-ai-arc-evidence/v1"
CONSOLE_EVIDENCE_SCHEMA = "honua.console.ai-arc-evidence/v1"
JOURNEY_ID = "2026.1-zero-to-map"
RELEASE_CONTRACT = "honua-release#123/D9.3"

ARC_COMPONENTS = (
    "honua-server",
    "honua-sdk-js",
    "honua-console",
    "honua-studio",
    "honua-devops",
    "honua-iac",
)
PROVISION_COMPONENTS = ("honua-devops", "honua-iac")
PROVISION_CHECKS = (
    "terraform-plan",
    "terraform-apply",
    "readiness",
    "admin-mcp-handoff",
    "teardown",
)
ARC_CHECKS = (
    "candidate-image-install",
    "admin-configure-and-publish",
    "esri-gp-mcp",
    "esri-gpserver",
    "native-analysis-artifact",
    "studio-map-app-dashboard-save-reopen",
    "governed-publication-approval",
    "console-audit-recovery",
    "public-share-http-200",
)
REAL_MODEL_CHECKS = (
    "natural-language-admin-setup-config-publish",
    "natural-language-esri-gp",
    "natural-language-native-analysis",
    "natural-language-map-app-dashboard-composition-publication",
    "deterministic-id-join",
    "same-endpoint-candidate",
    "no-secret-serialization",
)
REAL_MODEL_LANES = ("admin", "esriGp", "nativeAnalysis", "studioPublication")
# Exact ordered 58-action real-model roster owned by the manifest-pinned SDK plan.
# Tuple fields: actionId, lane, role, family, kind, emitted evidence name.
REAL_MODEL_ACTION_SPECS = (
    ("admin-status", "admin", "server-status", None, "mcp", "honua_admin_server_status"),
    ("create-connection", "admin", "connection-create", None, "mcp", "honua_admin_connection_create"),
    ("test-connection", "admin", "connection-test", None, "mcp", "honua_admin_connection_test"),
    ("import-parcels", "admin", "import-upload-url", "parcels", "mcp", "honua_admin_import_upload_url"),
    ("import-zoning", "admin", "import-upload-url", "zoning", "mcp", "honua_admin_import_upload_url"),
    ("publish-parcels", "admin", "layer-publish", "parcels", "mcp", "honua_admin_layer_publish"),
    ("publish-zoning", "admin", "layer-publish", "zoning", "mcp", "honua_admin_layer_publish"),
    ("set-public-access", "admin", "service-access", None, "mcp", "honua_admin_service_set_access_policy"),
    ("create-scoped-key", "admin", "scoped-key-create", None, "mcp", "honua_admin_api_key_create"),
    ("list-esri-gp-tasks", "esriGp", "list-tasks", None, "mcp", "honua_esri_gp_list_tasks"),
    ("describe-esri-buffer", "esriGp", "describe-buffer", None, "mcp", "honua_esri_gp_describe_task"),
    ("buffer-esri-mcp", "esriGp", "execute-buffer", None, "mcp", "honua_esri_gp_execute_task"),
    ("wait-esri-mcp-buffer", "esriGp", "wait-buffer", None, "mcp-resource", "honua://jobs/{esriMcpJobId}"),
    ("read-esri-mcp-buffer-results", "esriGp", "read-buffer-results", None, "mcp-resource", "honua://jobs/{esriMcpJobId}/results"),
    ("buffer-esri-gpserver", "nativeAnalysis", "execute-buffer-gpserver", None, "gpserver", "GPServer/analysis/Buffer"),
    ("buffer-parcels", "nativeAnalysis", "execute-buffer", None, "mcp", "honua_buffer_features"),
    ("wait-direct-buffer", "nativeAnalysis", "wait-buffer", None, "mcp-resource", "honua://jobs/{directAnalysisJobId}"),
    ("read-direct-buffer-results", "nativeAnalysis", "read-buffer-results", None, "mcp-resource", "honua://jobs/{directAnalysisJobId}/results"),
    ("create-map-draft", "studioPublication", "create-draft", "map", "mcp", "honua_studio_create_draft"),
    ("add-map-parcels-layer", "studioPublication", "add-layer", "map", "mcp", "honua_studio_add_layer"),
    ("add-map-buffer-layer", "studioPublication", "add-layer", "map", "mcp", "honua_studio_add_layer"),
    ("style-map-buffer-layer", "studioPublication", "set-layer-style", "map", "mcp", "honua_studio_set_layer_style"),
    ("set-map-buffer-visibility", "studioPublication", "set-layer-visibility", "map", "mcp", "honua_studio_set_layer_visibility"),
    ("set-map-view", "studioPublication", "set-view", "map", "mcp", "honua_studio_set_view"),
    ("add-map-widget", "studioPublication", "add-widget", "map", "mcp", "honua_studio_add_widget"),
    ("add-map-control", "studioPublication", "add-control", "map", "mcp", "honua_studio_add_control"),
    ("validate-map-draft", "studioPublication", "validate-draft", "map", "mcp", "honua_studio_validate_draft"),
    ("save-map-version", "studioPublication", "save-version", "map", "mcp", "honua_studio_save_version"),
    ("get-map-version", "studioPublication", "get-version", "map", "mcp", "honua_studio_get_version"),
    ("reopen-map-version", "studioPublication", "reopen-version", "map", "mcp", "honua_studio_reopen_version"),
    ("create-app-draft", "studioPublication", "create-draft", "app", "mcp", "honua_studio_create_draft"),
    ("add-app-parcels-layer", "studioPublication", "add-layer", "app", "mcp", "honua_studio_add_layer"),
    ("add-app-buffer-layer", "studioPublication", "add-layer", "app", "mcp", "honua_studio_add_layer"),
    ("style-app-buffer-layer", "studioPublication", "set-layer-style", "app", "mcp", "honua_studio_set_layer_style"),
    ("set-app-view", "studioPublication", "set-view", "app", "mcp", "honua_studio_set_view"),
    ("add-app-chart", "studioPublication", "add-widget", "app", "mcp", "honua_studio_add_widget"),
    ("add-app-layer-control", "studioPublication", "add-control", "app", "mcp", "honua_studio_add_control"),
    ("bind-app-chart-interaction", "studioPublication", "bind-interaction", "app", "mcp", "honua_studio_bind_interaction"),
    ("validate-app-draft", "studioPublication", "validate-draft", "app", "mcp", "honua_studio_validate_draft"),
    ("save-app-version", "studioPublication", "save-version", "app", "mcp", "honua_studio_save_version"),
    ("get-app-version", "studioPublication", "get-version", "app", "mcp", "honua_studio_get_version"),
    ("reopen-app-version", "studioPublication", "reopen-version", "app", "mcp", "honua_studio_reopen_version"),
    ("create-dashboard-draft", "studioPublication", "create-draft", "dashboard", "mcp", "honua_studio_create_draft"),
    ("add-dashboard-buffer-layer", "studioPublication", "add-layer", "dashboard", "mcp", "honua_studio_add_layer"),
    ("style-dashboard-buffer-layer", "studioPublication", "set-layer-style", "dashboard", "mcp", "honua_studio_set_layer_style"),
    ("set-dashboard-view", "studioPublication", "set-view", "dashboard", "mcp", "honua_studio_set_view"),
    ("add-dashboard-chart", "studioPublication", "add-widget", "dashboard", "mcp", "honua_studio_add_widget"),
    ("add-dashboard-layer-control", "studioPublication", "add-control", "dashboard", "mcp", "honua_studio_add_control"),
    ("validate-dashboard-draft", "studioPublication", "validate-draft", "dashboard", "mcp", "honua_studio_validate_draft"),
    ("save-dashboard-version", "studioPublication", "save-version", "dashboard", "mcp", "honua_studio_save_version"),
    ("get-dashboard-version", "studioPublication", "get-version", "dashboard", "mcp", "honua_studio_get_version"),
    ("reopen-dashboard-version", "studioPublication", "reopen-version", "dashboard", "mcp", "honua_studio_reopen_version"),
    ("propose-map-publication", "studioPublication", "propose-publication", "map", "mcp", "honua_studio_propose_publication"),
    ("save-map-publication-version", "studioPublication", "save-version", "map", "mcp", "honua_studio_save_version"),
    ("propose-app-publication", "studioPublication", "propose-publication", "app", "mcp", "honua_studio_propose_publication"),
    ("save-app-publication-version", "studioPublication", "save-version", "app", "mcp", "honua_studio_save_version"),
    ("propose-dashboard-publication", "studioPublication", "propose-publication", "dashboard", "mcp", "honua_studio_propose_publication"),
    ("save-dashboard-publication-version", "studioPublication", "save-version", "dashboard", "mcp", "honua_studio_save_version"),
)


class ArcError(ValueError):
    """A fail-closed producer refusal with no secret values in its message."""


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    try:
        with path.open("rb") as stream:
            for block in iter(lambda: stream.read(1024 * 1024), b""):
                digest.update(block)
    except OSError as exc:
        raise ArcError(f"could not hash required evidence file: {path}") from exc
    return digest.hexdigest()


def read_json(path: Path, label: str) -> dict[str, Any]:
    try:
        value = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as exc:
        raise ArcError(f"{label} is missing or invalid JSON: {path}: {exc}") from exc
    if not isinstance(value, dict):
        raise ArcError(f"{label} must be a JSON object: {path}")
    return value


def canonical_sha256(value: Any) -> str:
    encoded = json.dumps(
        value, sort_keys=True, separators=(",", ":"), ensure_ascii=False
    ).encode("utf-8")
    return hashlib.sha256(encoded).hexdigest()


def reject_forbidden_serialization(value: Any, tokens: tuple[str, ...], message: str) -> None:
    lowered = tuple(token.lower() for token in tokens)

    def visit(item: Any, field_name: str | None = None) -> None:
        if isinstance(item, dict):
            for name, child in item.items():
                normalized = str(name).lower()
                if any(token in normalized for token in lowered):
                    raise ArcError(message)
                visit(child, normalized)
        elif isinstance(item, list):
            for child in item:
                visit(child, field_name)
        elif isinstance(item, str) and field_name != "name":
            normalized = item.lower()
            if any(token in normalized for token in lowered):
                raise ArcError(message)

    visit(value)


def write_json(path: Path, value: dict[str, Any]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, indent=2, sort_keys=True) + "\n", encoding="utf-8")


def read_manifest(path: Path) -> dict[str, Any]:
    if yaml is None:
        raise ArcError("PyYAML is required to read the candidate platform manifest")
    try:
        value = yaml.safe_load(path.read_text(encoding="utf-8"))
    except (OSError, yaml.YAMLError) as exc:
        raise ArcError(f"platform manifest is missing or invalid: {path}: {exc}") from exc
    if not isinstance(value, dict):
        raise ArcError("platform manifest must be an object")
    components = value.get("components")
    if not isinstance(components, dict):
        raise ArcError("platform manifest has no component map")
    for name in ARC_COMPONENTS:
        component = components.get(name)
        if not isinstance(component, dict) or not SHA.fullmatch(str(component.get("sha", ""))):
            raise ArcError(f"platform manifest component {name} has no exact 40-character SHA")
    server = components["honua-server"]
    if not str(server.get("image", "")).startswith("ghcr.io/honua-io/honua-server:"):
        raise ArcError("platform manifest has no exact Honua server image")
    if not re.fullmatch(r"sha256:[0-9a-f]{64}", str(server.get("digest", ""))):
        raise ArcError("platform manifest has no exact Honua server image digest")
    release_id = value.get("platformRelease")
    if not isinstance(release_id, str) or not release_id:
        raise ArcError("platform manifest has no platformRelease")
    return value


def candidate_id(manifest_path: Path) -> str:
    return f"manifest-sha256:{sha256_file(manifest_path)}"


def exact_component_shas(manifest: dict[str, Any], names: tuple[str, ...]) -> dict[str, str]:
    components = manifest["components"]
    return {name: str(components[name]["sha"]) for name in names}


def require_public_https(url: Any, label: str) -> str:
    if not isinstance(url, str):
        raise ArcError(f"{label} must be a public HTTPS URL")
    parsed = urlparse(url)
    if parsed.scheme != "https" or not parsed.hostname or parsed.username or parsed.password:
        raise ArcError(f"{label} must be a public HTTPS URL without embedded credentials")
    host = parsed.hostname.lower().rstrip(".")
    if host in {"localhost", "localhost.localdomain"} or host.endswith((".local", ".internal")):
        raise ArcError(f"{label} must not use a loopback or private hostname")
    try:
        address = ipaddress.ip_address(host)
    except ValueError:
        address = None
    if address and not address.is_global:
        raise ArcError(f"{label} must not use a non-public IP address")
    return url.rstrip("/")


MISSING = object()


def json_pointer(value: Any, pointer: str) -> Any:
    if pointer == "":
        return value
    if not pointer.startswith("/"):
        return MISSING
    current = value
    for part in pointer[1:].split("/"):
        key = part.replace("~1", "/").replace("~0", "~")
        if not isinstance(current, dict) or key not in current:
            return MISSING
        current = current[key]
    return current


def git_head(path: Path) -> str:
    try:
        result = subprocess.run(
            ["git", "-C", str(path), "rev-parse", "HEAD"],
            check=True,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
        )
    except (OSError, subprocess.CalledProcessError) as exc:
        raise ArcError(f"could not resolve checkout identity: {path}") from exc
    value = result.stdout.strip()
    if not SHA.fullmatch(value):
        raise ArcError(f"checkout at {path} is not at an exact commit")
    return value


def validate_handoff(path: Path) -> tuple[dict[str, Any], str, str, str]:
    handoff = read_json(path, "secretless install handoff")
    if handoff.get("schemaVersion") != HANDOFF_SCHEMA:
        raise ArcError("install handoff has an unsupported schemaVersion")
    env = handoff.get("env")
    refs = handoff.get("secretRefs")
    if not isinstance(env, dict) or not isinstance(refs, dict):
        raise ArcError("install handoff is missing env or secretRefs")
    base_url = require_public_https(env.get("HONUA_BASE_URL"), "handoff HONUA_BASE_URL")
    mcp_url = require_public_https(env.get("HONUA_MCP_REMOTE_URL"), "handoff HONUA_MCP_REMOTE_URL")
    if mcp_url != f"{base_url}/mcp":
        raise ArcError("handoff MCP URL is not the /mcp endpoint on HONUA_BASE_URL")
    admin_ref = refs.get("HONUA_ADMIN_KEY")
    if not isinstance(admin_ref, str) or not AWS_SECRET_ARN.fullmatch(admin_ref):
        raise ArcError("handoff HONUA_ADMIN_KEY must be a scoped AWS Secrets Manager ARN")
    if "HONUA_ADMIN_KEY" in env or "HONUA_API_KEY" in env:
        raise ArcError("handoff serializes credential material instead of a secret reference")
    return handoff, base_url, mcp_url, admin_ref


def validate_provision_binding(
    path: Path,
    manifest: dict[str, Any],
    expected_candidate_id: str,
    base_url: str,
    admin_ref: str,
) -> dict[str, Any]:
    binding = read_json(path, "AWS ECS provision binding")
    if binding.get("schemaVersion") != PROVISION_BINDING_SCHEMA:
        raise ArcError("AWS ECS provision binding has an unsupported schemaVersion")
    if binding.get("target") != "aws-ecs" or binding.get("status") != "ready":
        raise ArcError("AWS ECS provision binding must be target=aws-ecs and status=ready")
    if binding.get("candidateId") != expected_candidate_id:
        raise ArcError("AWS ECS provision binding is not bound to the exact manifest bytes")
    if binding.get("releaseId") != manifest["platformRelease"]:
        raise ArcError("AWS ECS provision binding has the wrong release id")
    if binding.get("endpoint") != base_url:
        raise ArcError("AWS ECS provision binding endpoint differs from the secretless handoff")
    if binding.get("adminKeySecretRef") != admin_ref:
        raise ArcError("AWS ECS provision binding admin secret reference differs from the handoff")
    expected = exact_component_shas(manifest, ("honua-server", "honua-devops", "honua-iac"))
    if binding.get("components") != expected:
        raise ArcError("AWS ECS provision binding component identities differ from the manifest")
    server = manifest["components"]["honua-server"]
    expected_image = f"{server['image']}@{server['digest']}"
    if binding.get("serverImage") != expected_image:
        raise ArcError("AWS ECS provision binding did not install the manifest-pinned server image")
    checks = binding.get("checks")
    for check in PROVISION_CHECKS[:-1]:
        if not isinstance(checks, dict) or checks.get(check) != "passed":
            raise ArcError(f"AWS ECS provision binding does not prove {check}=passed")
    evidence = binding.get("evidence")
    if not isinstance(evidence, dict) or not SHA256.fullmatch(str(evidence.get("sha256", ""))):
        raise ArcError("AWS ECS provision binding has no content-addressed evidence")
    require_public_https(evidence.get("url"), "provision evidence URL")
    return binding


def scalar_captures(value: Any) -> dict[str, str | int | float | bool]:
    if not isinstance(value, dict):
        return {}
    result: dict[str, str | int | float | bool] = {}
    for name, item in value.items():
        if not isinstance(name, str) or isinstance(item, (dict, list)) or item is None:
            continue
        if isinstance(item, (str, int, float, bool)) and (
            name in {"route", "serviceName"}
            or name.endswith(("Id", "Hash", "Name", "Generation"))
        ):
            result[name] = item
    return result


def receipt_captures(receipt: dict[str, Any]) -> dict[str, str | int | float | bool]:
    captures: dict[str, str | int | float | bool] = {}
    for action in action_map(receipt).values():
        captures.update(scalar_captures(action.get("captures")))
    return captures


def checkpoint_action_receipt_digests(checkpoint: dict[str, Any]) -> dict[str, str]:
    digests: dict[str, str] = {}
    resume = checkpoint.get("resume")
    for stage in (resume.get("completedStages") if isinstance(resume, dict) else None) or []:
        if not isinstance(stage, dict):
            continue
        for action in stage.get("actions") or []:
            if not isinstance(action, dict) or not isinstance(action.get("id"), str):
                continue
            action_id = action["id"]
            if action_id in digests:
                raise ArcError(f"SDK checkpoint duplicates action receipt {action_id}")
            digests[action_id] = canonical_sha256(action)
    return digests


def validate_real_model_lanes(
    lanes: Any,
    joins: dict[str, Any],
    action_receipt_digests: dict[str, str],
) -> None:
    if not isinstance(lanes, dict) or set(lanes) != set(REAL_MODEL_LANES):
        raise ArcError("AWS ECS real-model receipt does not cover the four required natural-language lanes")
    expected_by_lane: dict[str, list[tuple[str, str, str | None, str, str]]] = {
        lane: [] for lane in REAL_MODEL_LANES
    }
    for action_id, lane, role, family, kind, name in REAL_MODEL_ACTION_SPECS:
        expected_by_lane[lane].append(
            (
                action_id,
                role,
                family,
                kind,
                name.format(
                    esriMcpJobId=joins.get("esriMcpJobId", ""),
                    directAnalysisJobId=joins.get("directAnalysisJobId", ""),
                ),
            )
        )
    identity_keys: dict[str, set[str]] = {}
    observed_action_ids: set[str] = set()
    for lane_name in REAL_MODEL_LANES:
        lane = lanes[lane_name]
        if not isinstance(lane, dict):
            raise ArcError(f"AWS ECS real-model lane {lane_name} is invalid")
        if set(lane) != {"promptSha256", "transcriptSha256", "calls"}:
            raise ArcError(f"AWS ECS real-model lane {lane_name} has unexpected fields")
        if not SHA256.fullmatch(str(lane.get("promptSha256", ""))) or not SHA256.fullmatch(
            str(lane.get("transcriptSha256", ""))
        ):
            raise ArcError(f"AWS ECS real-model lane {lane_name} lacks prompt/transcript hashes")
        calls = lane.get("calls")
        if not isinstance(calls, list) or not calls:
            raise ArcError(f"AWS ECS real-model lane {lane_name} has no content-addressed call evidence")
        identity_keys[lane_name] = set()
        expected_calls = expected_by_lane[lane_name]
        if len(calls) != len(expected_calls):
            raise ArcError(
                f"AWS ECS real-model lane {lane_name} does not have the canonical action multiplicity"
            )
        for index, call in enumerate(calls):
            if not isinstance(call, dict) or call.get("status") != "passed":
                raise ArcError(f"AWS ECS real-model lane {lane_name} contains a non-passing call")
            allowed_call_fields = {
                "actionId", "actionReceiptSha256", "role", "kind", "name",
                "status", "responseSha256", "result",
            }
            if call.get("family") is not None:
                allowed_call_fields.add("family")
            if set(call) != allowed_call_fields:
                raise ArcError(f"AWS ECS real-model lane {lane_name} contains unexpected call fields")
            role = call.get("role")
            action_id = call.get("actionId")
            family = call.get("family")
            kind = call.get("kind")
            name = call.get("name")
            if (
                not isinstance(role, str)
                or not isinstance(action_id, str)
                or not action_id
                or family not in {None, "map", "app", "dashboard", "parcels", "zoning"}
                or kind not in {"mcp", "mcp-resource", "gpserver"}
                or not isinstance(name, str)
                or not name
            ):
                raise ArcError(f"AWS ECS real-model lane {lane_name} has malformed call identity")
            expected_action_id, expected_role, expected_family, expected_kind, expected_name = (
                expected_calls[index]
            )
            if (action_id, role, family, kind, name) != (
                expected_action_id,
                expected_role,
                expected_family,
                expected_kind,
                expected_name,
            ):
                raise ArcError(
                    f"AWS ECS real-model lane {lane_name} call {index} is not canonical action "
                    f"{expected_action_id}"
                )
            if action_id in observed_action_ids:
                raise ArcError(f"AWS ECS real-model evidence duplicates SDK action {action_id}")
            observed_action_ids.add(action_id)
            expected_action_digest = action_receipt_digests.get(action_id)
            if (
                expected_action_digest is None
                or call.get("actionReceiptSha256") != expected_action_digest
            ):
                raise ArcError(
                    f"AWS ECS real-model lane {lane_name} is not bound to SDK action receipt {action_id}"
                )
            if not SHA256.fullmatch(str(call.get("responseSha256", ""))):
                raise ArcError(f"AWS ECS real-model lane {lane_name} call {role} has no response hash")
            result = call.get("result")
            if not isinstance(result, dict) or result.get("status") != "reconciled":
                raise ArcError(f"AWS ECS real-model lane {lane_name} call {role} has no result status")
            if set(result) != {"status", "identities"}:
                raise ArcError(f"AWS ECS real-model lane {lane_name} call {role} has unexpected result fields")
            identities = result.get("identities")
            if not isinstance(identities, dict) or not identities:
                raise ArcError(f"AWS ECS real-model lane {lane_name} call {role} has no result identities")
            matched = False
            for identity_name, value in identities.items():
                if not isinstance(identity_name, str) or isinstance(value, (dict, list)) or value is None:
                    raise ArcError(f"AWS ECS real-model lane {lane_name} call {role} has invalid result identities")
                if identity_name in joins:
                    if joins[identity_name] != value:
                        raise ArcError(
                            f"AWS ECS real-model lane {lane_name} call {role} disagrees on {identity_name}"
                        )
                    matched = True
                    identity_keys[lane_name].add(identity_name)
            if not matched:
                raise ArcError(
                    f"AWS ECS real-model lane {lane_name} call {role} is not joined to a deterministic identity"
                )
            if action_id == "buffer-esri-gpserver" and "gpServerJobId" not in identities:
                raise ArcError(
                    "AWS ECS real-model action buffer-esri-gpserver omits gpServerJobId result evidence"
                )

    required_join_results = {
        "admin": {"connectionId", "parcelsLayerId", "zoningLayerId", "serviceName"},
        "esriGp": {"esriMcpJobId"},
        "nativeAnalysis": {"gpServerJobId", "directAnalysisJobId"},
        "studioPublication": {
            "mapProposalId", "appProposalId", "dashboardProposalId",
            "mapPublicationVersionId", "appPublicationVersionId", "dashboardPublicationVersionId",
        },
    }
    for lane_name, required in required_join_results.items():
        missing = sorted(required - identity_keys[lane_name])
        if missing:
            raise ArcError(f"AWS ECS real-model lane {lane_name} result evidence omits joins {missing}")


def validate_real_model_receipt(
    path: Path,
    *,
    evidence_path: Path,
    manifest: dict[str, Any],
    expected_candidate_id: str,
    base_url: str,
    checkpoint: dict[str, Any],
    console_receipt_path: Path,
    console_evidence_path: Path,
    journey: dict[str, Any] | None = None,
    console: dict[str, Any] | None = None,
) -> dict[str, Any]:
    receipt = read_json(path, "AWS ECS real-model AI arc receipt")
    expected_receipt_fields = {
        "schemaVersion", "id", "status", "target", "candidateId", "releaseId",
        "endpointSha256", "source", "components", "model", "promptVersion",
        "evalVersion", "transcriptSha256", "deterministic", "lanes", "joins",
        "checks", "evidence",
    }
    if set(receipt) != expected_receipt_fields:
        raise ArcError("AWS ECS real-model receipt has unexpected or missing top-level fields")
    if receipt.get("schemaVersion") != REAL_MODEL_RECEIPT_SCHEMA:
        raise ArcError("AWS ECS real-model receipt has the wrong schemaVersion")
    if receipt.get("id") != "aws-ecs-real-model-ai-arc" or receipt.get("status") != "passed":
        raise ArcError("AWS ECS real-model receipt must identify itself and have status=passed")
    if receipt.get("target") != "aws-ecs":
        raise ArcError("AWS ECS real-model receipt has the wrong target")
    if receipt.get("candidateId") != expected_candidate_id or receipt.get("releaseId") != manifest["platformRelease"]:
        raise ArcError("AWS ECS real-model receipt is not bound to the exact release candidate")
    endpoint_digest = hashlib.sha256(base_url.encode("utf-8")).hexdigest()
    if receipt.get("endpointSha256") != endpoint_digest:
        raise ArcError("AWS ECS real-model receipt was not run against the provisioned endpoint")
    source = receipt.get("source")
    expected_studio_sha = manifest["components"]["honua-studio"]["sha"]
    if source != {"repository": "honua-io/honua-studio", "sha": expected_studio_sha}:
        raise ArcError("AWS ECS real-model receipt is not from the manifest-pinned Studio runner")
    if receipt.get("components") != exact_component_shas(manifest, ARC_COMPONENTS):
        raise ArcError("AWS ECS real-model receipt component identities differ from the manifest")
    model = receipt.get("model")
    if not isinstance(model, dict) or model.get("provider") not in {"anthropic", "bedrock", "openai"}:
        raise ArcError("AWS ECS real-model receipt has no live model provider")
    if not isinstance(model.get("modelId"), str) or not model["modelId"]:
        raise ArcError("AWS ECS real-model receipt has no exact model id")
    if receipt.get("promptVersion") != "honua.aws-ecs.ai-arc.prompt/v1":
        raise ArcError("AWS ECS real-model receipt has the wrong prompt version")
    if receipt.get("evalVersion") != "honua.aws-ecs.ai-arc.eval/v1":
        raise ArcError("AWS ECS real-model receipt has the wrong eval version")
    if not SHA256.fullmatch(str(receipt.get("transcriptSha256", ""))):
        raise ArcError("AWS ECS real-model receipt has no transcript SHA-256")
    deterministic = receipt.get("deterministic")
    checkpoint_digest = (checkpoint.get("integrity") or {}).get("digest")
    console_aggregate_digest = sha256_file(console_receipt_path)
    console_evidence_digest = sha256_file(console_evidence_path)
    if (
        not isinstance(deterministic, dict)
        or set(deterministic) != {
            "target", "provisionReceiptSha256", "checkpointDigest",
            "consoleAggregateSha256", "consoleEvidenceSha256",
        }
        or deterministic.get("checkpointDigest") != checkpoint_digest
        or deterministic.get("target") != "aws-ecs"
        or deterministic.get("provisionReceiptSha256") != checkpoint.get("provisionReceiptSha256")
        or deterministic.get("consoleAggregateSha256") != console_aggregate_digest
        or deterministic.get("consoleEvidenceSha256") != console_evidence_digest
    ):
        raise ArcError("AWS ECS real-model receipt is not joined to the deterministic checkpoint")
    checks = receipt.get("checks")
    for check in REAL_MODEL_CHECKS:
        if not isinstance(checks, dict) or checks.get(check) != "passed":
            raise ArcError(f"AWS ECS real-model receipt does not prove {check}=passed")
    joins = receipt.get("joins")
    if not isinstance(joins, dict):
        raise ArcError("AWS ECS real-model receipt has no deterministic identity joins")
    resume = checkpoint.get("resume") or {}
    expected_joins = scalar_captures(resume.get("capturedVariables"))
    expected_joins.update(
        {"candidateId": expected_candidate_id, "releaseId": manifest["platformRelease"]}
    )
    if journey is not None:
        expected_joins.update(receipt_captures(journey))
    if console is not None:
        proposals = console.get("proposals") or {}
        publications = console.get("publications") or {}
        audits = console.get("audit") or {}
        for family in ("map", "app", "dashboard"):
            proposal = proposals.get(family) if isinstance(proposals, dict) else None
            publication = publications.get(family) if isinstance(publications, dict) else None
            audit = audits.get(family) if isinstance(audits, dict) else None
            if isinstance(proposal, dict):
                expected_joins[f"{family}ProposalId"] = proposal.get("proposalId")
                expected_joins[f"{family}ExecutionOperationId"] = proposal.get("executionOperationId")
            if isinstance(publication, dict):
                expected_joins[f"{family}PublicationRequestId"] = publication.get("requestId")
                expected_joins[f"{family}PublicationId"] = publication.get("publicationId")
                expected_joins[f"{family}PublicationStatus"] = publication.get("status")
                expected_joins[f"{family}PublicUrl"] = publication.get("publicUrl")
            if isinstance(audit, dict):
                expected_joins[f"{family}AuditCorrelationId"] = audit.get("correlationId")
    required_base = {
        "connectionId", "parcelsLayerId", "zoningLayerId", "esriMcpJobId",
        "gpServerJobId", "directAnalysisJobId", "mapVersionId", "appVersionId",
        "dashboardVersionId", "mapPublicationVersionId", "appPublicationVersionId",
        "dashboardPublicationVersionId", "mapPublicationContentHash",
        "appPublicationContentHash", "dashboardPublicationContentHash",
    }
    missing_base = sorted(required_base - set(expected_joins))
    if missing_base:
        raise ArcError(f"deterministic evidence omits real-model join identities {missing_base}")
    for name, expected in expected_joins.items():
        if joins.get(name) != expected:
            raise ArcError(f"AWS ECS real-model receipt does not join deterministic identity {name}")
    extra_joins = sorted(set(joins) - set(expected_joins))
    if extra_joins:
        raise ArcError(f"AWS ECS real-model receipt has non-deterministic joins {extra_joins}")
    for family in ("map", "app", "dashboard"):
        for suffix in ("ProposalId", "PublicationId"):
            name = f"{family}{suffix}"
            if not isinstance(joins.get(name), str) or not joins[name]:
                raise ArcError(f"AWS ECS real-model receipt is missing {name}")
    validate_real_model_lanes(
        receipt.get("lanes"), joins, checkpoint_action_receipt_digests(checkpoint)
    )
    evidence = receipt.get("evidence")
    if not isinstance(evidence, dict) or not SHA256.fullmatch(str(evidence.get("sha256", ""))):
        raise ArcError("AWS ECS real-model receipt has no content-addressed evidence")
    require_public_https(evidence.get("url"), "AWS ECS real-model evidence URL")
    if sha256_file(evidence_path) != evidence["sha256"]:
        raise ArcError("AWS ECS real-model evidence bytes do not match the receipt")
    evidence_document = read_json(evidence_path, "AWS ECS real-model call evidence")
    expected_evidence_bindings = {
        "schemaVersion": REAL_MODEL_EVIDENCE_SCHEMA,
        "candidateId": expected_candidate_id,
        "releaseId": manifest["platformRelease"],
        "endpointSha256": endpoint_digest,
        "source": source,
        "model": model,
        "promptVersion": receipt["promptVersion"],
        "evalVersion": receipt["evalVersion"],
        "transcriptSha256": receipt["transcriptSha256"],
        "target": "aws-ecs",
        "provisionReceiptSha256": checkpoint.get("provisionReceiptSha256"),
        "checkpointDigest": checkpoint_digest,
        "consoleAggregateSha256": console_aggregate_digest,
        "consoleEvidenceSha256": console_evidence_digest,
        "lanes": receipt["lanes"],
        "joins": joins,
    }
    for name, expected in expected_evidence_bindings.items():
        if evidence_document.get(name) != expected:
            raise ArcError(f"AWS ECS real-model call evidence disagrees on {name}")
    if set(evidence_document) != set(expected_evidence_bindings):
        raise ArcError("AWS ECS real-model call evidence has unexpected or missing fields")
    reject_forbidden_serialization(
        {"receipt": receipt, "evidence": evidence_document},
        ("password", "authorization", "api_key", "apikey", "secretstring", "fixture"),
        "AWS ECS real-model receipt contains forbidden secret/fixture material",
    )
    return receipt


def validate_console_receipt(
    path: Path,
    manifest: dict[str, Any],
    expected_candidate_id: str,
    checkpoint: dict[str, Any] | None = None,
) -> dict[str, Any]:
    receipt = read_json(path, "Console approval receipt")
    if receipt.get("schemaVersion") != "honua.zero-to-map.console-receipt/v1":
        raise ArcError("Console approval receipt has the wrong schemaVersion")
    if receipt.get("journeyId") != JOURNEY_ID or receipt.get("releaseContract") != RELEASE_CONTRACT:
        raise ArcError("Console approval receipt has the wrong journey identity")
    if receipt.get("status") != "passed":
        raise ArcError("Console approval receipt did not pass")
    candidate = receipt.get("candidate")
    if not isinstance(candidate, dict) or candidate.get("candidateId") != expected_candidate_id:
        raise ArcError("Console approval receipt is not bound to the exact manifest")
    if candidate.get("releaseId") != manifest["platformRelease"]:
        raise ArcError("Console approval receipt has the wrong release id")
    checks = receipt.get("checks")
    for check in ("health", "audit", "recovery"):
        if not isinstance(checks, dict) or checks.get(check) != "passed":
            raise ArcError(f"Console approval receipt does not prove {check}=passed")
    share_url = require_public_https(receipt.get("shareUrl"), "approved Console share URL")
    proposals = receipt.get("proposals")
    publications = receipt.get("publications")
    audits = receipt.get("audit")
    if not isinstance(proposals, dict) or not isinstance(publications, dict) or not isinstance(audits, dict):
        raise ArcError("Console receipt has no map/app/dashboard proposal, publication, and audit identities")
    for family in ("map", "app", "dashboard"):
        proposal = proposals.get(family)
        publication = publications.get(family)
        audit = audits.get(family)
        if not isinstance(proposal, dict) or not isinstance(publication, dict) or not isinstance(audit, dict):
            raise ArcError(f"Console receipt is missing the {family} governance identity")
        for field in ("draftId", "route", "proposalId", "executionOperationId"):
            if not isinstance(proposal.get(field), str) or not proposal[field]:
                raise ArcError(f"Console {family} proposal is missing {field}")
        if not isinstance(proposal.get("generation"), int) or proposal["generation"] < 1:
            raise ArcError(f"Console {family} proposal generation is invalid")
        for field in ("requestId", "itemId", "versionId", "publicationId", "publicUrl"):
            if not isinstance(publication.get(field), str) or not publication[field]:
                raise ArcError(f"Console {family} publication is missing {field}")
        if publication.get("status") != "published":
            raise ArcError(f"Console {family} publication is not published")
        require_public_https(publication["publicUrl"], f"Console {family} publication URL")
        for field in ("correlationId", "operationId"):
            if not isinstance(audit.get(field), str) or not audit[field]:
                raise ArcError(f"Console {family} audit is missing {field}")
        if proposal["proposalId"] != publication["requestId"]:
            raise ArcError(f"Console {family} publication request is not its exact proposal")
        if proposal["executionOperationId"] != audit["operationId"]:
            raise ArcError(f"Console {family} approval operation is not joined to audit")
        if checkpoint is not None:
            captures = ((checkpoint.get("resume") or {}).get("capturedVariables") or {})
            expected = {
                "draftId": captures.get(f"{family}ReopenedDraftId"),
                "generation": captures.get(f"{family}ProposalGeneration"),
                "itemId": captures.get(f"{family}ItemId"),
                "versionId": captures.get(f"{family}PublicationVersionId"),
            }
            for field, expected_value in expected.items():
                actual = proposal.get(field) if field in {"draftId", "generation"} else publication.get(field)
                if expected_value is None or actual != expected_value:
                    raise ArcError(f"Console {family} {field} is not joined to the deterministic checkpoint")
    if publications["app"].get("publicUrl") != share_url:
        raise ArcError("final share URL is not the approved app publication URL")
    if checkpoint is not None:
        request = checkpoint.get("consoleReceiptRequest")
        if not isinstance(request, dict):
            raise ArcError("SDK checkpoint has no resolved Console receipt request")
        if (
            request.get("schemaVersion") != "honua.zero-to-map.console-receipt-request/v1"
            or request.get("actionId") != "console-approval"
            or request.get("receiptSchema") != "honua.zero-to-map.console-receipt/v1"
        ):
            raise ArcError("SDK checkpoint has the wrong Console request identity")
        matches = request.get("matches")
        required_pointers = request.get("requiredPointers")
        equal_pointers = request.get("equalPointers")
        if not isinstance(matches, dict) or not isinstance(required_pointers, list) or not isinstance(equal_pointers, list):
            raise ArcError("SDK checkpoint Console request is malformed")
        for pointer, expected_value in matches.items():
            if not isinstance(pointer, str) or json_pointer(receipt, pointer) != expected_value:
                raise ArcError(f"Console receipt disagrees with resolved request at {pointer}")
        for pointer in required_pointers:
            if not isinstance(pointer, str) or json_pointer(receipt, pointer) is MISSING:
                raise ArcError(f"Console receipt omits resolved required pointer {pointer}")
        for pair in equal_pointers:
            if not isinstance(pair, list) or len(pair) != 2 or not all(isinstance(pointer, str) for pointer in pair):
                raise ArcError("SDK checkpoint Console equality request is malformed")
            left, right = (json_pointer(receipt, pointer) for pointer in pair)
            if left is MISSING or right is MISSING or left != right:
                raise ArcError(f"Console receipt violates resolved equality {pair}")
    return {**receipt, "shareUrl": share_url}


def validate_real_model_handoff(
    path: Path,
    *,
    manifest: dict[str, Any],
    expected_candidate_id: str,
    base_url: str,
    checkpoint: dict[str, Any],
) -> dict[str, Any]:
    handoff = read_json(path, "immutable Studio model handoff")
    expected_fields = {
        "schemaVersion", "status", "target", "candidateId", "releaseId",
        "endpointSha256", "source", "components", "model", "promptVersion",
        "evalVersion", "transcriptSha256", "deterministic", "lanes", "joins",
        "consoleReceiptRequest", "integrity",
    }
    if set(handoff) != expected_fields:
        raise ArcError("immutable Studio model handoff has unexpected or missing fields")
    if (
        handoff.get("schemaVersion") != "honua.studio.real-model-ai-arc-handoff/v1"
        or handoff.get("status") != "paused"
        or handoff.get("target") != "aws-ecs"
    ):
        raise ArcError("immutable Studio model handoff has the wrong schema, status, or target")
    if (
        handoff.get("candidateId") != expected_candidate_id
        or handoff.get("releaseId") != manifest["platformRelease"]
        or handoff.get("endpointSha256")
        != hashlib.sha256(base_url.encode("utf-8")).hexdigest()
    ):
        raise ArcError("immutable Studio model handoff is not bound to this candidate endpoint")
    expected_source = {
        "repository": "honua-io/honua-studio",
        "sha": manifest["components"]["honua-studio"]["sha"],
    }
    if handoff.get("source") != expected_source:
        raise ArcError("immutable Studio model handoff source differs from the manifest")
    if handoff.get("components") != exact_component_shas(manifest, ARC_COMPONENTS):
        raise ArcError("immutable Studio model handoff component identities differ from the manifest")
    model = handoff.get("model")
    if (
        not isinstance(model, dict)
        or set(model) != {"provider", "modelId"}
        or model.get("provider") not in {"anthropic", "bedrock", "openai"}
        or not isinstance(model.get("modelId"), str)
        or not model["modelId"]
    ):
        raise ArcError("immutable Studio model handoff has no exact live model identity")
    if (
        handoff.get("promptVersion") != "honua.aws-ecs.ai-arc.prompt/v1"
        or handoff.get("evalVersion") != "honua.aws-ecs.ai-arc.eval/v1"
        or not SHA256.fullmatch(str(handoff.get("transcriptSha256", "")))
    ):
        raise ArcError("immutable Studio model handoff has the wrong prompt/eval/transcript binding")
    checkpoint_digest = (checkpoint.get("integrity") or {}).get("digest")
    if handoff.get("deterministic") != {
        "target": "aws-ecs",
        "provisionReceiptSha256": checkpoint.get("provisionReceiptSha256"),
        "checkpointDigest": checkpoint_digest,
    }:
        raise ArcError("immutable Studio model handoff deterministic binding changed")
    expected_joins = scalar_captures((checkpoint.get("resume") or {}).get("capturedVariables"))
    expected_joins.update(
        {"candidateId": expected_candidate_id, "releaseId": manifest["platformRelease"]}
    )
    if handoff.get("joins") != expected_joins:
        raise ArcError("immutable Studio model handoff joins are not checkpoint-owned")
    if handoff.get("consoleReceiptRequest") != checkpoint.get("consoleReceiptRequest"):
        raise ArcError("immutable Studio model handoff Console request changed")
    validate_real_model_lanes(
        handoff.get("lanes"), expected_joins, checkpoint_action_receipt_digests(checkpoint)
    )
    expected_transcript = canonical_sha256(
        [handoff["lanes"][lane]["transcriptSha256"] for lane in REAL_MODEL_LANES]
    )
    if handoff["transcriptSha256"] != expected_transcript:
        raise ArcError("immutable Studio model handoff transcript does not bind its lane transcripts")
    integrity = handoff.get("integrity")
    if (
        not isinstance(integrity, dict)
        or set(integrity) != {"algorithm", "digest"}
        or integrity.get("algorithm") != "sha256"
        or not SHA256.fullmatch(str(integrity.get("digest", "")))
    ):
        raise ArcError("immutable Studio model handoff has no canonical integrity digest")
    unsigned = dict(handoff)
    unsigned.pop("integrity", None)
    if not hmac.compare_digest(str(integrity["digest"]), canonical_sha256(unsigned)):
        raise ArcError("immutable Studio model handoff canonical integrity digest does not match")
    reject_forbidden_serialization(
        handoff,
        ("password", "authorization", "api_key", "apikey", "secretstring"),
        "immutable Studio model handoff contains forbidden credential material",
    )
    return handoff


def validate_console_evidence(
    path: Path,
    *,
    aggregate_path: Path,
    real_model_handoff_path: Path,
    console: dict[str, Any],
    manifest: dict[str, Any],
    expected_candidate_id: str,
    base_url: str,
    checkpoint: dict[str, Any],
) -> dict[str, Any]:
    evidence = read_json(path, "Console browser evidence sidecar")
    expected_fields = {
        "schemaVersion", "status", "target", "candidate", "endpointSha256",
        "components", "handoffDigest", "checkpointDigest", "aggregateSha256",
        "runtime", "publications", "checks", "integrity",
    }
    if set(evidence) != expected_fields:
        raise ArcError("Console browser evidence has unexpected or missing fields")
    if evidence.get("schemaVersion") != CONSOLE_EVIDENCE_SCHEMA or evidence.get("status") != "passed":
        raise ArcError("Console browser evidence has the wrong schema or status")
    if evidence.get("target") != "aws-ecs":
        raise ArcError("Console browser evidence is not bound to AWS ECS")
    if evidence.get("candidate") != {
        "candidateId": expected_candidate_id,
        "releaseId": manifest["platformRelease"],
    }:
        raise ArcError("Console browser evidence is not bound to the exact candidate")
    if evidence.get("endpointSha256") != hashlib.sha256(base_url.encode("utf-8")).hexdigest():
        raise ArcError("Console browser evidence was not observed against the provisioned endpoint")
    if evidence.get("components") != exact_component_shas(manifest, ARC_COMPONENTS):
        raise ArcError("Console browser evidence component identities differ from the manifest")
    real_model_handoff = validate_real_model_handoff(
        real_model_handoff_path,
        manifest=manifest,
        expected_candidate_id=expected_candidate_id,
        base_url=base_url,
        checkpoint=checkpoint,
    )
    handoff_integrity = real_model_handoff.get("integrity")
    if (
        not isinstance(handoff_integrity, dict)
        or not SHA256.fullmatch(str(handoff_integrity.get("digest", "")))
        or evidence.get("handoffDigest") != handoff_integrity.get("digest")
    ):
        raise ArcError("Console browser evidence is not joined to the immutable Studio handoff")
    if evidence.get("checkpointDigest") != (checkpoint.get("integrity") or {}).get("digest"):
        raise ArcError("Console browser evidence is not joined to the SDK checkpoint")
    if evidence.get("aggregateSha256") != sha256_file(aggregate_path):
        raise ArcError("Console browser evidence is not joined to the aggregate receipt bytes")

    runtime = evidence.get("runtime")
    if runtime != {
        "consoleCommit": manifest["components"]["honua-console"]["sha"],
        "serverSourceRevision": manifest["components"]["honua-server"]["sha"],
    }:
        raise ArcError("Console browser evidence did not observe the manifest-pinned runtime revisions")
    checks = evidence.get("checks")
    expected_checks = {
        "browser": "passed", "approval": "passed", "publication": "passed",
        "audit": "passed", "recovery": "passed",
    }
    if checks != expected_checks:
        raise ArcError("Console browser evidence did not pass every required UI/governance check")
    publications = evidence.get("publications")
    if not isinstance(publications, dict) or set(publications) != {"map", "app", "dashboard"}:
        raise ArcError("Console browser evidence lacks exact map/app/dashboard observations")
    observed_recovery: dict[str, Any] | None = None
    for family in ("map", "app", "dashboard"):
        item = publications.get(family)
        proposal = console["proposals"][family]
        publication = console["publications"][family]
        audit = console["audit"][family]
        if not isinstance(item, dict) or set(item) != {
            "proposalId", "executionOperationId", "publicationId", "publicUrl",
            "auditCorrelationId", "recovery",
        }:
            raise ArcError(f"Console browser evidence has malformed {family} publication facts")
        expected = {
            "proposalId": proposal["proposalId"],
            "executionOperationId": proposal["executionOperationId"],
            "publicationId": publication["publicationId"],
            "publicUrl": publication["publicUrl"],
            "auditCorrelationId": audit["correlationId"],
        }
        for name, expected_value in expected.items():
            if item.get(name) != expected_value:
                raise ArcError(f"Console browser evidence {family} disagrees on {name}")
        recovery = item.get("recovery")
        if not isinstance(recovery, dict) or set(recovery) != {
            "status", "deliberateFailureJobId", "resumedJobId", "actionableDiagnostics",
        }:
            raise ArcError(f"Console browser evidence has malformed {family} recovery proof")
        if (
            recovery.get("status") != "passed"
            or recovery.get("actionableDiagnostics") is not True
            or not isinstance(recovery.get("deliberateFailureJobId"), str)
            or not recovery["deliberateFailureJobId"]
            or not isinstance(recovery.get("resumedJobId"), str)
            or not recovery["resumedJobId"]
            or recovery["deliberateFailureJobId"] == recovery["resumedJobId"]
        ):
            raise ArcError(f"Console browser evidence did not prove {family} failure recovery")
        if observed_recovery is not None and recovery != observed_recovery:
            raise ArcError("Console browser evidence uses inconsistent recovery observations")
        observed_recovery = recovery

    integrity = evidence.get("integrity")
    if (
        not isinstance(integrity, dict)
        or integrity.get("algorithm") != "sha256"
        or not SHA256.fullmatch(str(integrity.get("digest", "")))
    ):
        raise ArcError("Console browser evidence has no canonical integrity digest")
    unsigned = dict(evidence)
    unsigned.pop("integrity", None)
    if not hmac.compare_digest(str(integrity["digest"]), canonical_sha256(unsigned)):
        raise ArcError("Console browser evidence canonical integrity digest does not match")
    reject_forbidden_serialization(
        evidence,
        ("password", "authorization", "api_key", "apikey", "secretstring"),
        "Console browser evidence contains forbidden credential material",
    )
    return evidence


def fetch_admin_json(base_url: str, path: str, admin_secret: str, label: str) -> dict[str, Any]:
    request = Request(
        f"{base_url}{path}",
        headers={"Accept": "application/json", "x-api-key": admin_secret},
        method="GET",
    )
    try:
        with urlopen(request, timeout=15) as response:
            raw = response.read(1024 * 1024 + 1)
    except (HTTPError, URLError, OSError, TimeoutError) as exc:
        raise ArcError(f"could not read {label} from the candidate") from exc
    if len(raw) > 1024 * 1024:
        raise ArcError(f"candidate {label} response exceeds the 1 MiB evidence limit")
    try:
        value = json.loads(raw.decode("utf-8"))
    except (UnicodeDecodeError, json.JSONDecodeError) as exc:
        raise ArcError(f"candidate {label} response is not valid JSON") from exc
    if not isinstance(value, dict):
        raise ArcError(f"candidate {label} response must be a JSON object")
    return value


def verify_privileged_console_audit(
    base_url: str,
    console: dict[str, Any],
    admin_secret: str,
) -> None:
    for family in ("map", "app", "dashboard"):
        proposal = console["proposals"][family]
        audit = console["audit"][family]
        proposal_id = proposal["proposalId"]
        proposal_state = fetch_admin_json(
            base_url,
            f"/api/v1/admin/proposals/{quote(proposal_id, safe='')}",
            admin_secret,
            f"{family} proposal state",
        )
        if (
            proposal_state.get("proposalId") != proposal_id
            or proposal_state.get("executionOperationId") != proposal["executionOperationId"]
            or proposal_state.get("status") not in {"Submitted", "Reconciling", "Succeeded"}
        ):
            raise ArcError(f"candidate {family} proposal is not durably applied")
        query = urlencode(
            {
                "resourceType": "operation_proposal",
                "resourceId": proposal_id,
                "action": "operation.applied",
                "pageSize": "25",
            }
        )
        response = fetch_admin_json(
            base_url,
            f"/api/v1/admin/observability/audit?{query}",
            admin_secret,
            f"{family} proposal audit",
        )
        items = response.get("items")
        if not isinstance(items, list):
            raise ArcError(f"candidate {family} proposal audit has no item roster")
        matches = [
            row
            for row in items
            if isinstance(row, dict)
            and row.get("resourceType") == "operation_proposal"
            and row.get("resourceId") == proposal_id
            and row.get("action") == "operation.applied"
            and str(row.get("outcome", "")).lower() == "success"
        ]
        if len(matches) != 1:
            raise ArcError(f"candidate audit does not contain one exact {family} proposal application")
        row = matches[0]
        if (
            row.get("correlationId") != audit["correlationId"]
            or audit.get("operationId") != proposal["executionOperationId"]
        ):
            raise ArcError(f"candidate {family} audit identities differ from the Console receipt")

    verification = fetch_admin_json(
        base_url,
        "/api/v1/admin/observability/audit/verify",
        admin_secret,
        "audit chain verification",
    )
    if verification.get("verified") is not True:
        raise ArcError("candidate audit chain verification did not pass")


def resolve_aws_secret(secret_ref: str, label: str = "admin secret") -> str:
    try:
        result = subprocess.run(
            [
                "aws",
                "secretsmanager",
                "get-secret-value",
                "--secret-id",
                secret_ref,
                "--query",
                "SecretString",
                "--output",
                "text",
                "--no-cli-pager",
            ],
            check=True,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
        )
    except FileNotFoundError as exc:
        raise ArcError(f"AWS CLI is required to resolve the scoped {label}") from exc
    except subprocess.CalledProcessError as exc:
        # Never relay subprocess output: providers occasionally include request
        # material in diagnostics.
        raise ArcError(f"AWS Secrets Manager refused the scoped {label} lookup") from exc
    secret = result.stdout.rstrip("\r\n")
    if not secret or secret == "None":
        raise ArcError(f"AWS Secrets Manager returned no {label} material")
    return secret


def resolve_database_password(args: argparse.Namespace) -> str:
    secret_ref = args.db_connection_secret_ref
    if secret_ref:
        if not AWS_SECRET_ARN.fullmatch(secret_ref):
            raise ArcError("database connection secret reference must be an AWS Secrets Manager ARN")
        connection = resolve_aws_secret(secret_ref, "database connection secret")
        fields: dict[str, str] = {}
        for part in connection.split(";"):
            if not part:
                continue
            name, separator, value = part.partition("=")
            if not separator or not name or name.lower() in fields:
                raise ArcError("database connection secret has an invalid connection-string shape")
            fields[name.lower()] = value
        expected = {
            "host": str(args.db_host),
            "port": str(args.db_port),
            "database": str(args.db_name),
            "username": str(args.db_user),
        }
        for name, expected_value in expected.items():
            if fields.get(name) != expected_value:
                raise ArcError(f"database connection secret does not match the provisioned {name}")
        password = fields.get("password")
        if not password:
            raise ArcError("database connection secret contains no password")
        return password
    password = os.environ.get(args.db_password_env)
    if not password:
        raise ArcError(
            f"database password environment variable {args.db_password_env} is not set and no secret ref was supplied"
        )
    return password


def sdk_command(
    args: argparse.Namespace,
    *,
    manifest: dict[str, Any],
    expected_candidate_id: str,
    mcp_url: str,
    console_receipt: Path | None,
) -> list[str]:
    mcp_root = args.sdk_root / "mcp"
    if not (mcp_root / "package.json").is_file():
        raise ArcError("manifest-pinned SDK checkout has no MCP package")
    command = [
        "npm",
        "--prefix",
        str(mcp_root),
        "run",
        "release:zero-to-map",
        "--",
        "--execute",
        "--yes",
        "--mcp-url",
        mcp_url,
        "--target",
        "aws-ecs",
        "--provision-receipt",
        str(args.provision_binding),
        "--checkpoint",
        str(args.checkpoint),
        "--var",
        f"fixtureBaseUrl={args.fixture_base_url}",
        "--var",
        f"dbHost={args.db_host}",
        "--var",
        f"dbPort={args.db_port}",
        "--var",
        f"dbName={args.db_name}",
        "--var",
        f"dbUser={args.db_user}",
        "--var-env",
        f"dbPassword={args.db_password_env}",
        "--var",
        f"candidateId={expected_candidate_id}",
        "--var",
        f"releaseId={manifest['platformRelease']}",
        "--output",
        str(args.sdk_receipt),
    ]
    if console_receipt is not None:
        checkpoint = read_json(args.checkpoint, "SDK pause/resume checkpoint")
        integrity = checkpoint.get("integrity")
        digest = integrity.get("digest") if isinstance(integrity, dict) else None
        if not isinstance(digest, str) or not SHA256.fullmatch(digest):
            raise ArcError("SDK checkpoint has no resume digest")
        command.extend(
            [
                "--checkpoint-digest",
                digest,
                "--console-receipt",
                str(console_receipt),
            ]
        )
    return command


def child_environment(
    args: argparse.Namespace,
    *,
    admin_secret: str,
    base_url: str,
    mcp_url: str,
    sdk_source_sha: str,
) -> dict[str, str]:
    db_password = resolve_database_password(args)
    if not ENVIRONMENT_NAME.fullmatch(str(args.db_password_env)):
        raise ArcError("database password environment variable name is invalid")
    env = {
        name: value
        for name, value in os.environ.items()
        if name.upper() in SDK_CHILD_ENVIRONMENT_KEYS
    }
    env.update(
        {
            "HONUA_ADMIN_KEY": admin_secret,
            "HONUA_API_KEY": admin_secret,
            "HONUA_BASE_URL": base_url,
            "HONUA_MCP_REMOTE_URL": mcp_url,
            "HONUA_SOURCE_REVISION": sdk_source_sha,
            args.db_password_env: db_password,
        }
    )
    return env


def run_sdk(command: list[str], env: dict[str, str]) -> int:
    try:
        return subprocess.run(command, env=env, check=False).returncode
    except FileNotFoundError as exc:
        raise ArcError("npm is required to execute the manifest-pinned SDK journey") from exc


def action_map(receipt: dict[str, Any]) -> dict[str, dict[str, Any]]:
    result: dict[str, dict[str, Any]] = {}
    for stage in receipt.get("stages") or []:
        if not isinstance(stage, dict):
            continue
        for action in stage.get("actions") or []:
            if isinstance(action, dict) and isinstance(action.get("id"), str):
                result[action["id"]] = action
    return result


def sdk_failure_attribution(receipt_path: Path, sdk_root: Path) -> str:
    try:
        receipt = read_json(receipt_path, "SDK journey receipt")
        plan = read_json(
            sdk_root / "mcp" / "release" / "zero-to-map" / "journey.v1.json",
            "SDK journey plan",
        )
    except ArcError:
        return "SDK receipt/plan did not preserve a readable failure location"
    tools: dict[str, str] = {}
    for stage in plan.get("stages") or []:
        if not isinstance(stage, dict):
            continue
        for action in stage.get("actions") or []:
            if isinstance(action, dict) and isinstance(action.get("id"), str):
                tools[action["id"]] = str(action.get("tool") or action.get("kind") or "unknown")
    for stage in receipt.get("stages") or []:
        if not isinstance(stage, dict):
            continue
        for action in stage.get("actions") or []:
            if not isinstance(action, dict) or action.get("status") in {"passed", "skipped"}:
                continue
            action_id = str(action.get("id") or "unknown")
            return (
                f"stage={stage.get('id') or stage.get('number') or 'unknown'} "
                f"action={action_id} tool={tools.get(action_id, 'unknown')} "
                f"code={action.get('code') or action.get('status') or 'unknown'}"
            )
    return "SDK receipt contains no attributable non-passing action"


def validate_checkpoint(
    path: Path,
    expected_candidate_id: str,
    release_id: str,
    sdk_sha: str | None = None,
    mcp_url: str | None = None,
    provision_binding: Path | None = None,
    plan_path: Path | None = None,
) -> dict[str, Any]:
    checkpoint = read_json(path, "SDK pause/resume checkpoint")
    if checkpoint.get("schemaVersion") != CHECKPOINT_SCHEMA:
        raise ArcError("SDK checkpoint has an unsupported schemaVersion")
    if checkpoint.get("journeyId") != JOURNEY_ID or checkpoint.get("releaseContract") != RELEASE_CONTRACT:
        raise ArcError("SDK checkpoint has the wrong journey identity")
    if checkpoint.get("candidateId") != expected_candidate_id or checkpoint.get("releaseId") != release_id:
        raise ArcError("SDK checkpoint is not bound to the exact release candidate")
    if not SHA256.fullmatch(str(checkpoint.get("planSha256", ""))):
        raise ArcError("SDK checkpoint has no exact journey plan digest")
    if plan_path is not None and checkpoint.get("planSha256") != sha256_file(plan_path):
        raise ArcError("SDK checkpoint is not bound to the manifest-pinned journey plan bytes")
    if not SHA.fullmatch(str(checkpoint.get("sourceRevision", ""))):
        raise ArcError("SDK checkpoint has no exact SDK source revision")
    if not SHA256.fullmatch(str(checkpoint.get("mcpEndpointSha256", ""))):
        raise ArcError("SDK checkpoint has no MCP endpoint digest")
    if checkpoint.get("state") != "paused":
        raise ArcError("SDK checkpoint is not in the paused state")
    if checkpoint.get("target") != "aws-ecs":
        raise ArcError("SDK checkpoint is not bound to the AWS ECS target")
    provision_digest = checkpoint.get("provisionReceiptSha256")
    if not SHA256.fullmatch(str(provision_digest or "")):
        raise ArcError("SDK checkpoint has no exact provision-binding digest")
    if provision_binding is not None and provision_digest != sha256_file(provision_binding):
        raise ArcError("SDK checkpoint is not bound to the exact provision-binding bytes")
    if sdk_sha is not None and checkpoint.get("sourceRevision") != sdk_sha:
        raise ArcError("SDK checkpoint source is not the manifest-pinned SDK commit")
    if mcp_url is not None:
        normalized_mcp_url = mcp_url.rstrip("/")
        expected_endpoint_digest = hashlib.sha256(normalized_mcp_url.encode("utf-8")).hexdigest()
        if checkpoint.get("mcpEndpointSha256") != expected_endpoint_digest:
            raise ArcError("SDK checkpoint is not bound to the provisioned MCP endpoint")
    resume = checkpoint.get("resume")
    if not isinstance(resume, dict) or resume.get("resumeAt") != {
        "stageId": "console",
        "actionId": "console-approval",
    }:
        raise ArcError("SDK checkpoint is not paused at the Console approval boundary")
    if not isinstance(resume.get("capturedVariables"), dict):
        raise ArcError("SDK checkpoint has no captured runtime identities")
    request = checkpoint.get("consoleReceiptRequest")
    if not isinstance(request, dict):
        raise ArcError("SDK checkpoint has no resolved Console receipt request")
    if (
        request.get("schemaVersion") != "honua.zero-to-map.console-receipt-request/v1"
        or request.get("actionId") != "console-approval"
        or request.get("receiptSchema") != "honua.zero-to-map.console-receipt/v1"
    ):
        raise ArcError("SDK checkpoint has the wrong Console request identity")
    reject_forbidden_serialization(
        checkpoint,
        ("dbpassword", "honua_admin_key", "honua_api_key", "authorization", "secretstring"),
        "SDK checkpoint contains a secret-shaped field",
    )
    integrity = checkpoint.get("integrity")
    if (
        not isinstance(integrity, dict)
        or integrity.get("algorithm") != "sha256"
        or not SHA256.fullmatch(str(integrity.get("digest", "")))
    ):
        raise ArcError("SDK checkpoint has no canonical integrity digest")
    unsigned = dict(checkpoint)
    unsigned.pop("integrity", None)
    actual = hashlib.sha256(
        json.dumps(unsigned, sort_keys=True, separators=(",", ":"), ensure_ascii=False).encode("utf-8")
    ).hexdigest()
    if not hmac.compare_digest(str(integrity["digest"]), actual):
        raise ArcError("SDK checkpoint canonical integrity digest does not match its content")
    return checkpoint


def validate_paused_receipt(path: Path) -> dict[str, Any]:
    receipt = read_json(path, "paused SDK journey receipt")
    if receipt.get("schemaVersion") != JOURNEY_RECEIPT_SCHEMA or receipt.get("mode") != "live":
        raise ArcError("paused SDK receipt is not a live zero-to-map receipt")
    if receipt.get("status") != "blocked":
        raise ArcError("first SDK pass must stop blocked at the Console boundary")
    actions = action_map(receipt)
    console = actions.get("console-approval")
    if not console or console.get("status") != "blocked" or console.get("code") != "external-receipt-missing":
        raise ArcError("first SDK pass did not pause only for its Console receipt")
    for action_id, action in actions.items():
        if action_id == "console-approval" or action.get("status") == "skipped":
            continue
        if action.get("status") != "passed" or not isinstance(action.get("evidence"), dict):
            raise ArcError(f"pre-Console SDK action {action_id} did not retain passing live evidence")
    return receipt


def validate_split_console_receipt_paths(aggregate: Path, sdk_alias: Path) -> None:
    if aggregate.resolve() == sdk_alias.resolve():
        raise ArcError("aggregate and SDK Console receipts must be distinct files")
    if not aggregate.is_file():
        raise ArcError("aggregate Console receipt does not exist")
    if not sdk_alias.is_file():
        raise ArcError("SDK Console receipt alias does not exist")
    if aggregate.read_bytes() != sdk_alias.read_bytes():
        raise ArcError("aggregate and SDK Console receipt alias must be byte-identical")


def validate_passed_journey(path: Path) -> dict[str, Any]:
    receipt = read_json(path, "completed SDK journey receipt")
    if receipt.get("schemaVersion") != JOURNEY_RECEIPT_SCHEMA or receipt.get("mode") != "live":
        raise ArcError("completed SDK receipt is not a live zero-to-map receipt")
    if receipt.get("status") != "passed" or receipt.get("blockers"):
        raise ArcError("completed SDK journey did not pass without blockers")
    actions = action_map(receipt)
    if not actions:
        raise ArcError("completed SDK journey has no actions")
    for action_id, action in actions.items():
        if action.get("status") != "passed" or not isinstance(action.get("evidence"), dict):
            raise ArcError(f"SDK action {action_id} lacks passing live evidence")
    required_groups = {
        "candidate-image-install": ("install-local", "install-status"),
        "admin-configure-and-publish": (
            "admin-status", "create-connection", "test-connection", "import-parcels", "import-zoning",
            "publish-parcels", "publish-zoning", "set-public-access", "create-scoped-key",
        ),
        "esri-gp-mcp": (
            "list-esri-gp-tasks", "describe-esri-buffer", "buffer-esri-mcp",
            "wait-esri-mcp-buffer", "read-esri-mcp-buffer-results",
        ),
        "esri-gpserver": ("buffer-esri-gpserver",),
        "native-analysis-artifact": ("buffer-parcels", "wait-direct-buffer", "read-direct-buffer-results"),
        "studio-map-app-dashboard-save-reopen": (
            "save-map-version", "get-map-version", "reopen-map-version",
            "save-app-version", "get-app-version", "reopen-app-version",
            "save-dashboard-version", "get-dashboard-version", "reopen-dashboard-version",
        ),
        "governed-publication-approval": (
            "propose-map-publication", "save-map-publication-version",
            "propose-app-publication", "save-app-publication-version",
            "propose-dashboard-publication", "save-dashboard-publication-version",
            "console-approval",
        ),
        "console-audit-recovery": ("console-approval",),
        "public-share-http-200": (
            "verify-map-public-url", "verify-share-url", "verify-dashboard-public-url",
        ),
    }
    for check, action_ids in required_groups.items():
        missing = [action_id for action_id in action_ids if action_id not in actions]
        if missing:
            raise ArcError(f"SDK journey cannot prove {check}; missing actions {missing}")
    return receipt


def prepare(args: argparse.Namespace) -> None:
    manifest = read_manifest(args.manifest)
    expected_candidate_id = candidate_id(args.manifest)
    _, base_url, mcp_url, admin_ref = validate_handoff(args.handoff)
    require_public_https(args.fixture_base_url, "fixture base URL")
    validate_provision_binding(
        args.provision_binding, manifest, expected_candidate_id, base_url, admin_ref
    )
    if args.source_sha != manifest["components"]["honua-devops"]["sha"]:
        raise ArcError("producer action SHA is not the manifest-pinned honua-devops commit")
    if git_head(args.sdk_root) != manifest["components"]["honua-sdk-js"]["sha"]:
        raise ArcError("SDK checkout is not the manifest-pinned honua-sdk-js commit")
    admin_secret = resolve_aws_secret(admin_ref)
    command = sdk_command(
        args,
        manifest=manifest,
        expected_candidate_id=expected_candidate_id,
        mcp_url=mcp_url,
        console_receipt=None,
    )
    code = run_sdk(
        command,
        child_environment(
            args,
            admin_secret=admin_secret,
            base_url=base_url,
            mcp_url=mcp_url,
            sdk_source_sha=manifest["components"]["honua-sdk-js"]["sha"],
        ),
    )
    # Drop the only in-process references before parsing or writing evidence.
    admin_secret = ""
    if code != 2:
        detail = sdk_failure_attribution(args.sdk_receipt, args.sdk_root)
        raise ArcError(
            f"first SDK pass exited {code}; expected the fail-closed Console pause exit 2; {detail}"
        )
    validate_paused_receipt(args.sdk_receipt)
    validate_checkpoint(
        args.checkpoint,
        expected_candidate_id,
        manifest["platformRelease"],
        manifest["components"]["honua-sdk-js"]["sha"],
        mcp_url,
        args.provision_binding,
        args.sdk_root / "mcp" / "release" / "zero-to-map" / "journey.v1.json",
    )
    print(f"AWS ECS AI delivery arc paused at Console approval; checkpoint={args.checkpoint}")


def resume(args: argparse.Namespace) -> None:
    manifest = read_manifest(args.manifest)
    expected_candidate_id = candidate_id(args.manifest)
    _, base_url, mcp_url, admin_ref = validate_handoff(args.handoff)
    require_public_https(args.fixture_base_url, "fixture base URL")
    validate_provision_binding(
        args.provision_binding, manifest, expected_candidate_id, base_url, admin_ref
    )
    if args.source_sha != manifest["components"]["honua-devops"]["sha"]:
        raise ArcError("producer action SHA is not the manifest-pinned honua-devops commit")
    if git_head(args.sdk_root) != manifest["components"]["honua-sdk-js"]["sha"]:
        raise ArcError("SDK checkout is not the manifest-pinned honua-sdk-js commit")
    checkpoint = validate_checkpoint(
        args.checkpoint,
        expected_candidate_id,
        manifest["platformRelease"],
        manifest["components"]["honua-sdk-js"]["sha"],
        mcp_url,
        args.provision_binding,
        args.sdk_root / "mcp" / "release" / "zero-to-map" / "journey.v1.json",
    )
    validate_split_console_receipt_paths(args.console_receipt, args.sdk_console_receipt)
    console = validate_console_receipt(
        args.console_receipt,
        manifest,
        expected_candidate_id,
        checkpoint,
    )
    validate_console_evidence(
        args.console_evidence,
        aggregate_path=args.console_receipt,
        real_model_handoff_path=args.real_model_handoff,
        console=console,
        manifest=manifest,
        expected_candidate_id=expected_candidate_id,
        base_url=base_url,
        checkpoint=checkpoint,
    )
    validate_real_model_receipt(
        args.real_model_receipt,
        evidence_path=args.real_model_evidence,
        manifest=manifest,
        expected_candidate_id=expected_candidate_id,
        base_url=base_url,
        checkpoint=checkpoint,
        console_receipt_path=args.console_receipt,
        console_evidence_path=args.console_evidence,
        console=console,
    )
    admin_secret = resolve_aws_secret(admin_ref)
    try:
        verify_privileged_console_audit(base_url, console, admin_secret)
        command = sdk_command(
            args,
            manifest=manifest,
            expected_candidate_id=expected_candidate_id,
            mcp_url=mcp_url,
            console_receipt=args.sdk_console_receipt,
        )
        code = run_sdk(
            command,
            child_environment(
                args,
                admin_secret=admin_secret,
                base_url=base_url,
                mcp_url=mcp_url,
                sdk_source_sha=manifest["components"]["honua-sdk-js"]["sha"],
            ),
        )
    finally:
        admin_secret = ""
    if code != 0:
        detail = sdk_failure_attribution(args.sdk_receipt, args.sdk_root)
        raise ArcError(f"resumed SDK journey exited {code}; {detail}")
    journey = validate_passed_journey(args.sdk_receipt)
    validate_real_model_receipt(
        args.real_model_receipt,
        evidence_path=args.real_model_evidence,
        manifest=manifest,
        expected_candidate_id=expected_candidate_id,
        base_url=base_url,
        checkpoint=checkpoint,
        console_receipt_path=args.console_receipt,
        console_evidence_path=args.console_evidence,
        journey=journey,
        console=console,
    )
    evidence = {
        "schemaVersion": "honua.aws-ecs.ai-delivery-arc-evidence/v1",
        "status": "awaiting-teardown",
        "target": "aws-ecs",
        "candidateId": expected_candidate_id,
        "releaseId": manifest["platformRelease"],
        "source": {"repository": "honua-io/honua-devops", "sha": args.source_sha},
        "components": exact_component_shas(manifest, ARC_COMPONENTS),
        "endpoint": base_url,
        "checks": {check: "passed" for check in ARC_CHECKS},
        "artifacts": {
            "platformManifest": sha256_file(args.manifest),
            "provisionBinding": sha256_file(args.provision_binding),
            "secretlessHandoff": sha256_file(args.handoff),
            "sdkJourneyReceipt": sha256_file(args.sdk_receipt),
            "sdkCheckpoint": checkpoint["integrity"]["digest"],
            "consoleReceipt": sha256_file(args.console_receipt),
            "sdkConsoleReceipt": sha256_file(args.sdk_console_receipt),
            "consoleEvidence": sha256_file(args.console_evidence),
            "studioRealModelHandoff": sha256_file(args.real_model_handoff),
            "awsEcsRealModelReceipt": sha256_file(args.real_model_receipt),
            "awsEcsRealModelEvidence": sha256_file(args.real_model_evidence),
        },
        "journey": {
            "journeyId": journey["journeyId"],
            "releaseContract": journey["releaseContract"],
            "shareUrl": console["shareUrl"],
            "actionCount": len(action_map(journey)),
        },
    }
    write_json(args.pre_teardown_evidence, evidence)
    print(f"AWS ECS AI delivery arc passed before teardown; evidence={args.pre_teardown_evidence}")


def validate_teardown(
    path: Path, manifest: dict[str, Any], expected_candidate_id: str
) -> dict[str, Any]:
    evidence = read_json(path, "AWS ECS teardown evidence")
    if evidence.get("schemaVersion") != TEARDOWN_SCHEMA:
        raise ArcError("AWS ECS teardown evidence has an unsupported schemaVersion")
    if evidence.get("target") != "aws-ecs" or evidence.get("status") != "passed":
        raise ArcError("AWS ECS teardown evidence must be target=aws-ecs and status=passed")
    if evidence.get("candidateId") != expected_candidate_id or evidence.get("releaseId") != manifest["platformRelease"]:
        raise ArcError("AWS ECS teardown evidence is not bound to the exact release candidate")
    expected = exact_component_shas(manifest, ("honua-devops", "honua-iac"))
    if evidence.get("components") != expected:
        raise ArcError("AWS ECS teardown evidence component identities differ from the manifest")
    checks = evidence.get("checks")
    for check in ("terraform-destroy", "cleanup-verified"):
        if not isinstance(checks, dict) or checks.get(check) != "passed":
            raise ArcError(f"AWS ECS teardown evidence does not prove {check}=passed")
    nested = evidence.get("evidence")
    if not isinstance(nested, dict) or not SHA256.fullmatch(str(nested.get("sha256", ""))):
        raise ArcError("AWS ECS teardown evidence has no content-addressed proof")
    require_public_https(nested.get("url"), "teardown evidence URL")
    return evidence


def make_receipt(
    *,
    receipt_id: str,
    manifest: dict[str, Any],
    expected_candidate_id: str,
    source_sha: str,
    components: tuple[str, ...],
    checks: tuple[str, ...],
    evidence_url: str,
    evidence_sha: str,
    include_journey: bool,
) -> dict[str, Any]:
    claims: dict[str, Any] = {
        "target": "aws-ecs",
        "checks": {check: "passed" for check in checks},
    }
    if include_journey:
        claims.update({"journeyId": JOURNEY_ID, "releaseContract": RELEASE_CONTRACT})
    return {
        "schemaVersion": RECEIPT_SCHEMA,
        "id": receipt_id,
        "status": "passed",
        "candidateId": expected_candidate_id,
        "releaseId": manifest["platformRelease"],
        "source": {"repository": "honua-io/honua-devops", "sha": source_sha},
        "components": exact_component_shas(manifest, components),
        "evidence": {"url": evidence_url, "sha256": evidence_sha},
        "claims": claims,
    }


def finalize(args: argparse.Namespace) -> None:
    manifest = read_manifest(args.manifest)
    expected_candidate_id = candidate_id(args.manifest)
    evidence_url = require_public_https(args.evidence_url, "final evidence URL")
    pre = read_json(args.pre_teardown_evidence, "pre-teardown AI delivery-arc evidence")
    if pre.get("schemaVersion") != "honua.aws-ecs.ai-delivery-arc-evidence/v1":
        raise ArcError("pre-teardown evidence has an unsupported schemaVersion")
    if pre.get("status") != "awaiting-teardown":
        raise ArcError("pre-teardown evidence is not awaiting teardown")
    if pre.get("candidateId") != expected_candidate_id or pre.get("releaseId") != manifest["platformRelease"]:
        raise ArcError("pre-teardown evidence is not bound to the exact release candidate")
    if pre.get("components") != exact_component_shas(manifest, ARC_COMPONENTS):
        raise ArcError("pre-teardown component identities differ from the manifest")
    for check in ARC_CHECKS:
        if (pre.get("checks") or {}).get(check) != "passed":
            raise ArcError(f"pre-teardown evidence does not prove {check}=passed")
    teardown = validate_teardown(args.teardown_evidence, manifest, expected_candidate_id)
    final_evidence = {
        **pre,
        "status": "passed",
        "teardown": teardown,
        "artifacts": {
            **pre.get("artifacts", {}),
            "teardownEvidence": sha256_file(args.teardown_evidence),
        },
    }
    write_json(args.final_evidence, final_evidence)
    evidence_sha = sha256_file(args.final_evidence)
    source_sha = manifest["components"]["honua-devops"]["sha"]
    provision = make_receipt(
        receipt_id="aws-ecs-provision",
        manifest=manifest,
        expected_candidate_id=expected_candidate_id,
        source_sha=source_sha,
        components=PROVISION_COMPONENTS,
        checks=PROVISION_CHECKS,
        evidence_url=evidence_url,
        evidence_sha=evidence_sha,
        include_journey=False,
    )
    arc = make_receipt(
        receipt_id="aws-ecs-ai-delivery-arc",
        manifest=manifest,
        expected_candidate_id=expected_candidate_id,
        source_sha=source_sha,
        components=ARC_COMPONENTS,
        checks=ARC_CHECKS,
        evidence_url=evidence_url,
        evidence_sha=evidence_sha,
        include_journey=True,
    )
    write_json(args.provision_receipt, provision)
    write_json(args.arc_receipt, arc)
    print(f"AWS ECS candidate-bound receipts sealed; evidence_sha256={evidence_sha}")


def add_run_args(parser: argparse.ArgumentParser) -> None:
    parser.add_argument("--manifest", required=True, type=Path)
    parser.add_argument("--sdk-root", required=True, type=Path)
    parser.add_argument("--handoff", required=True, type=Path)
    parser.add_argument("--provision-binding", required=True, type=Path)
    parser.add_argument("--fixture-base-url", required=True)
    parser.add_argument("--db-host", required=True)
    parser.add_argument("--db-port", type=int, default=5432)
    parser.add_argument("--db-name", default="honua")
    parser.add_argument("--db-user", default="honua")
    parser.add_argument("--db-password-env", default="HONUA_ZERO_TO_MAP_DB_PASSWORD")
    parser.add_argument("--db-connection-secret-ref")
    parser.add_argument("--checkpoint", required=True, type=Path)
    parser.add_argument("--sdk-receipt", required=True, type=Path)
    parser.add_argument("--source-sha", required=True)


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    prepare_parser = commands.add_parser("prepare", help="run through the Console approval pause")
    add_run_args(prepare_parser)
    resume_parser = commands.add_parser("resume", help="resume from the exact approved Console receipt")
    add_run_args(resume_parser)
    resume_parser.add_argument("--console-receipt", required=True, type=Path)
    resume_parser.add_argument("--sdk-console-receipt", required=True, type=Path)
    resume_parser.add_argument("--console-evidence", required=True, type=Path)
    resume_parser.add_argument("--real-model-handoff", required=True, type=Path)
    resume_parser.add_argument("--real-model-receipt", required=True, type=Path)
    resume_parser.add_argument("--real-model-evidence", required=True, type=Path)
    resume_parser.add_argument("--pre-teardown-evidence", required=True, type=Path)
    finalize_parser = commands.add_parser("finalize", help="bind teardown and emit release receipts")
    finalize_parser.add_argument("--manifest", required=True, type=Path)
    finalize_parser.add_argument("--pre-teardown-evidence", required=True, type=Path)
    finalize_parser.add_argument("--teardown-evidence", required=True, type=Path)
    finalize_parser.add_argument("--evidence-url", required=True)
    finalize_parser.add_argument("--final-evidence", required=True, type=Path)
    finalize_parser.add_argument("--provision-receipt", required=True, type=Path)
    finalize_parser.add_argument("--arc-receipt", required=True, type=Path)
    return parser


def main(argv: list[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    try:
        if not SHA.fullmatch(str(getattr(args, "source_sha", "0" * 40))):
            raise ArcError("producer source SHA must be an exact 40-character commit")
        if args.command == "prepare":
            prepare(args)
        elif args.command == "resume":
            resume(args)
        else:
            finalize(args)
        return 0
    except ArcError as exc:
        print(f"REFUSED: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
