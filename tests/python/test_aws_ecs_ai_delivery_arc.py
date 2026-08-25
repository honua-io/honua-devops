from __future__ import annotations

import copy
import hashlib
import importlib.util
import json
import subprocess
import tempfile
import unittest
import uuid
from pathlib import Path
from types import SimpleNamespace
from unittest import mock


ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location(
    "aws_ecs_ai_delivery_arc", ROOT / "scripts" / "aws_ecs_ai_delivery_arc.py"
)
assert SPEC and SPEC.loader
arc = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(arc)


def write_json(path: Path, value: dict) -> Path:
    path.write_text(json.dumps(value, indent=2) + "\n", encoding="utf-8")
    return path


class AwsEcsAiDeliveryArcTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        self.shas = {
            name: f"{index:x}" * 40
            for index, name in enumerate(arc.ARC_COMPONENTS, start=1)
        }
        self.manifest = {
            "platformRelease": "2026.1-rc.test",
            "components": {
                **{name: {"sha": sha, "version": "test"} for name, sha in self.shas.items()},
                "honua-server": {
                    "sha": self.shas["honua-server"],
                    "version": "test",
                    "image": "ghcr.io/honua-io/honua-server:candidate-test",
                    "digest": "sha256:" + "a" * 64,
                },
            },
        }
        self.manifest_path = self.root / "platform-manifest.yaml"
        assert arc.yaml is not None
        self.manifest_path.write_text(arc.yaml.safe_dump(self.manifest, sort_keys=False), encoding="utf-8")
        self.candidate_id = arc.candidate_id(self.manifest_path)
        self.endpoint = "https://ecs-test.honua.example"
        self.admin_reference_arn = "arn:aws:secretsmanager:us-west-2:123456789012:secret:honua-admin-AbCd"
        self.handoff_path = write_json(
            self.root / "handoff.json",
            {
                "schemaVersion": arc.HANDOFF_SCHEMA,
                "env": {
                    "HONUA_BASE_URL": self.endpoint,
                    "HONUA_MCP_REMOTE_URL": self.endpoint + "/mcp",
                },
                "secretRefs": {"HONUA_ADMIN_KEY": self.admin_reference_arn},
            },
        )
        self.provision_path = write_json(
            self.root / "provision.json",
            {
                "schemaVersion": arc.PROVISION_BINDING_SCHEMA,
                "target": "aws-ecs",
                "status": "ready",
                "candidateId": self.candidate_id,
                "releaseId": self.manifest["platformRelease"],
                "endpoint": self.endpoint,
                "adminKeySecretRef": self.admin_reference_arn,
                "serverImage": "ghcr.io/honua-io/honua-server:candidate-test@sha256:" + "a" * 64,
                "components": {
                    name: self.shas[name] for name in ("honua-server", "honua-devops", "honua-iac")
                },
                "checks": {
                    "terraform-plan": "passed",
                    "terraform-apply": "passed",
                    "readiness": "passed",
                    "admin-mcp-handoff": "passed",
                },
                "evidence": {"url": "https://github.com/honua-io/honua-release/actions/runs/1", "sha256": "b" * 64},
            },
        )

    def tearDown(self) -> None:
        self.temp.cleanup()

    @staticmethod
    def model_rosters() -> dict[str, list[tuple[str, str, str | None, str, str]]]:
        rosters = {lane: [] for lane in arc.REAL_MODEL_LANES}
        for action_id, lane, role, family, kind, name in arc.REAL_MODEL_ACTION_SPECS:
            rosters[lane].append(
                (
                    action_id,
                    role,
                    family,
                    kind,
                    name.format(
                        esriMcpJobId="esri-mcp-job-1",
                        directAnalysisJobId="native-job-1",
                    ),
                )
            )
        return rosters

    def test_handoff_and_provision_binding_join_exact_candidate(self) -> None:
        _, base_url, _, secret_ref = arc.validate_handoff(self.handoff_path)
        binding = arc.validate_provision_binding(
            self.provision_path, self.manifest, self.candidate_id, base_url, secret_ref
        )
        self.assertEqual(self.endpoint, binding["endpoint"])
        altered = json.loads(self.provision_path.read_text(encoding="utf-8"))
        altered["components"]["honua-iac"] = "f" * 40
        write_json(self.provision_path, altered)
        with self.assertRaisesRegex(arc.ArcError, "component identities"):
            arc.validate_provision_binding(
                self.provision_path, self.manifest, self.candidate_id, base_url, secret_ref
            )

    def test_sdk_command_keeps_database_password_out_of_argv(self) -> None:
        sdk = self.root / "sdk"
        (sdk / "mcp").mkdir(parents=True)
        (sdk / "mcp" / "package.json").write_text("{}", encoding="utf-8")
        args = SimpleNamespace(
            sdk_root=sdk,
            checkpoint=self.root / "checkpoint.json",
            provision_binding=self.provision_path,
            fixture_base_url="https://fixtures.honua.example/2026.1",
            db_host="db.example",
            db_port=5432,
            db_name="honua",
            db_user="honua",
            db_password_env="TEST_DB_PASSWORD",
            sdk_receipt=self.root / "sdk-receipt.json",
        )
        command = arc.sdk_command(
            args,
            manifest=self.manifest,
            expected_candidate_id=self.candidate_id,
            mcp_url=self.endpoint + "/mcp",
            console_receipt=None,
        )
        rendered = " ".join(str(part) for part in command)
        self.assertIn("--target aws-ecs", rendered)
        self.assertIn("--provision-receipt", command)
        self.assertIn("--var-env dbPassword=TEST_DB_PASSWORD", rendered)
        self.assertNotIn("actual-db-password", rendered)

    def test_sdk_resume_consumes_only_the_console_projection(self) -> None:
        sdk = self.root / "sdk"
        (sdk / "mcp").mkdir(parents=True)
        (sdk / "mcp" / "package.json").write_text("{}", encoding="utf-8")
        checkpoint_path = write_json(self.root / "checkpoint.json", self.checkpoint())
        aggregate_receipt = self.root / "console-aggregate.json"
        sdk_receipt = self.root / "console-sdk-alias.json"
        args = SimpleNamespace(
            sdk_root=sdk,
            checkpoint=checkpoint_path,
            provision_binding=self.provision_path,
            fixture_base_url="https://fixtures.honua.example/2026.1",
            db_host="db.example",
            db_port=5432,
            db_name="honua",
            db_user="honua",
            db_password_env="TEST_DB_PASSWORD",
            sdk_receipt=self.root / "sdk-receipt.json",
        )

        command = arc.sdk_command(
            args,
            manifest=self.manifest,
            expected_candidate_id=self.candidate_id,
            mcp_url=self.endpoint + "/mcp",
            console_receipt=sdk_receipt,
        )

        receipt_index = command.index("--console-receipt") + 1
        self.assertEqual(str(sdk_receipt), command[receipt_index])
        self.assertNotIn(str(aggregate_receipt), command)
        self.assertIn("--checkpoint-digest", command)

    def test_console_receipt_aliases_must_exist_at_distinct_paths_with_identical_bytes(self) -> None:
        aggregate_receipt = write_json(self.root / "console-aggregate.json", {})
        sdk_receipt = write_json(self.root / "console-sdk-alias.json", {})

        arc.validate_split_console_receipt_paths(aggregate_receipt, sdk_receipt)
        with self.assertRaisesRegex(arc.ArcError, "distinct files"):
            arc.validate_split_console_receipt_paths(aggregate_receipt, aggregate_receipt)
        with self.assertRaisesRegex(arc.ArcError, "does not exist"):
            arc.validate_split_console_receipt_paths(
                aggregate_receipt,
                self.root / "missing-sdk-alias.json",
            )
        different_receipt = write_json(self.root / "different-sdk-alias.json", {"different": True})
        with self.assertRaisesRegex(arc.ArcError, "byte-identical"):
            arc.validate_split_console_receipt_paths(aggregate_receipt, different_receipt)

    def checkpoint(self) -> dict:
        captures = {
            "candidateId": self.candidate_id,
            "releaseId": self.manifest["platformRelease"],
            "connectionId": "connection-1",
            "serviceName": "zero-to-map",
            "parcelsLayerId": 1,
            "zoningLayerId": 2,
            "esriMcpJobId": "esri-mcp-job-1",
            "esriMcpResultPackageId": "esri-result-package-1",
            "esriMcpArtifactId": "esri-artifact-1",
            "gpServerJobId": "gpserver-job-1",
            "directAnalysisJobId": "native-job-1",
            "bufferArtifactId": "native-artifact-1",
            "mapItemId": "map-item-1",
            "mapVersionId": "map-version-1",
            "mapContentHash": "map-content-hash-1",
            "appItemId": "app-item-1",
            "appVersionId": "app-version-1",
            "appContentHash": "app-content-hash-1",
            "dashboardItemId": "dashboard-item-1",
            "dashboardVersionId": "dashboard-version-1",
            "dashboardContentHash": "dashboard-content-hash-1",
            "mapReopenedDraftId": "map-draft-1",
            "appReopenedDraftId": "app-draft-1",
            "dashboardReopenedDraftId": "dashboard-draft-1",
            "mapProposalGeneration": 2,
            "appProposalGeneration": 3,
            "dashboardProposalGeneration": 4,
            "mapPublicationVersionId": "map-publication-version-1",
            "appPublicationVersionId": "app-publication-version-1",
            "dashboardPublicationVersionId": "dashboard-publication-version-1",
            "mapPublicationContentHash": "map-publication-hash-1",
            "appPublicationContentHash": "app-publication-hash-1",
            "dashboardPublicationContentHash": "dashboard-publication-hash-1",
            "mapProposalId": "map-proposal-1",
            "appProposalId": "app-proposal-1",
            "dashboardProposalId": "dashboard-proposal-1",
        }
        model_actions = []
        for lane, roster in self.model_rosters().items():
            for action_id, _role, _family, kind, _name in roster:
                model_actions.append(
                    {
                        "id": action_id,
                        "kind": kind,
                        "status": "passed",
                        "startedAt": "2026-08-20T12:00:00.000Z",
                        "finishedAt": "2026-08-20T12:00:01.000Z",
                        "captures": {},
                    }
                )
        checkpoint = {
            "schemaVersion": arc.CHECKPOINT_SCHEMA,
            "state": "paused",
            "target": "aws-ecs",
            "provisionReceiptSha256": arc.sha256_file(self.provision_path),
            "createdAt": "2026-08-20T12:00:00.000Z",
            "journeyId": arc.JOURNEY_ID,
            "releaseContract": arc.RELEASE_CONTRACT,
            "planSha256": "9" * 64,
            "sourceRevision": self.shas["honua-sdk-js"],
            "mcpEndpointSha256": hashlib.sha256((self.endpoint + "/mcp").encode()).hexdigest(),
            "candidateId": self.candidate_id,
            "releaseId": self.manifest["platformRelease"],
            "resume": {
                "startedAt": "2026-08-20T12:00:00.000Z",
                "capturedVariables": captures,
                "completedStages": [
                    {
                        "id": "real-model-evidence",
                        "number": 4,
                        "actions": model_actions,
                    }
                ],
                "resumeAt": {"stageId": "console", "actionId": "console-approval"},
            },
            "consoleReceiptRequest": {
                "schemaVersion": "honua.zero-to-map.console-receipt-request/v1",
                "actionId": "console-approval",
                "receiptSchema": "honua.zero-to-map.console-receipt/v1",
                "matches": {"/resources/connectionId": "connection-1"},
                "requiredPointers": ["/shareUrl"],
                "equalPointers": [["/shareUrl", "/publications/app/publicUrl"]],
            },
        }
        canonical = json.dumps(checkpoint, sort_keys=True, separators=(",", ":"), ensure_ascii=False)
        checkpoint["integrity"] = {
            "algorithm": "sha256",
            "digest": hashlib.sha256(canonical.encode()).hexdigest(),
        }
        return checkpoint

    def console_receipt(self) -> dict:
        proposals = {}
        publications = {}
        audits = {}
        for family in ("map", "app", "dashboard"):
            proposal_id = f"{family}-proposal-1"
            operation_id = f"{family}-operation-1"
            proposals[family] = {
                "draftId": f"{family}-draft-1",
                "generation": {"map": 2, "app": 3, "dashboard": 4}[family],
                "route": f"/{family}/public",
                "proposalId": proposal_id,
                "executionOperationId": operation_id,
            }
            publications[family] = {
                "requestId": proposal_id,
                "itemId": f"{family}-item-1",
                "versionId": f"{family}-publication-version-1",
                "status": "published",
                "publicationId": f"{family}-publication-1",
                "publicUrl": f"https://public.honua.example/{family}/1",
            }
            audits[family] = {
                "correlationId": f"{family}-correlation-1",
                "operationId": operation_id,
            }
        return {
            "schemaVersion": "honua.zero-to-map.console-receipt/v1",
            "journeyId": arc.JOURNEY_ID,
            "releaseContract": arc.RELEASE_CONTRACT,
            "status": "passed",
            "candidate": {
                "candidateId": self.candidate_id,
                "releaseId": self.manifest["platformRelease"],
            },
            "proposals": proposals,
            "publications": publications,
            "audit": audits,
            "resources": {
                "connectionId": "connection-1",
                "serviceId": "zero-to-map",
                "layerIds": {"parcels": 1, "zoning": 2},
                "jobs": {
                    "esriMcp": "esri-mcp-job-1",
                    "gpServer": "gpserver-job-1",
                    "directAnalysis": "native-job-1",
                },
                "gp": {
                    "jobId": "esri-mcp-job-1",
                    "serviceId": "analysis",
                    "taskName": "Buffer",
                    "processId": "geometry.buffer",
                    "resultPackageId": "esri-result-package-1",
                    "artifactId": "esri-artifact-1",
                },
                "artifactId": "native-artifact-1",
                "studio": {
                    family: {
                        "draftId": f"{family}-original-draft-1",
                        "itemId": f"{family}-item-1",
                        "versionId": f"{family}-version-1",
                        "contentHash": f"{family}-content-hash-1",
                        "reopenedDraftId": f"{family}-draft-1",
                    }
                    for family in ("map", "app", "dashboard")
                },
            },
            "checks": {"health": "passed", "audit": "passed", "recovery": "passed"},
            "shareUrl": publications["app"]["publicUrl"],
        }

    def model_lanes(self, checkpoint: dict, joins: dict) -> dict:
        lane_identities = {
            "admin": {
                "candidateId": self.candidate_id,
                "connectionId": joins["connectionId"],
                "parcelsLayerId": joins["parcelsLayerId"],
                "zoningLayerId": joins["zoningLayerId"],
                "serviceName": joins["serviceName"],
            },
            "esriGp": {
                "candidateId": self.candidate_id,
                "esriMcpJobId": joins["esriMcpJobId"],
                "esriMcpResultPackageId": joins["esriMcpResultPackageId"],
                "esriMcpArtifactId": joins["esriMcpArtifactId"],
            },
            "nativeAnalysis": {
                "candidateId": self.candidate_id,
                "gpServerJobId": joins["gpServerJobId"],
                "directAnalysisJobId": joins["directAnalysisJobId"],
                "bufferArtifactId": joins["bufferArtifactId"],
            },
            "studioPublication": {
                "candidateId": self.candidate_id,
                "mapProposalId": joins["mapProposalId"],
                "appProposalId": joins["appProposalId"],
                "dashboardProposalId": joins["dashboardProposalId"],
                "mapPublicationVersionId": joins["mapPublicationVersionId"],
                "appPublicationVersionId": joins["appPublicationVersionId"],
                "dashboardPublicationVersionId": joins["dashboardPublicationVersionId"],
            },
        }
        action_digests = arc.checkpoint_action_receipt_digests(checkpoint)
        lanes = {}
        for index, lane_name in enumerate(arc.REAL_MODEL_LANES, start=1):
            calls = []
            for action_id, role, family, kind, name in self.model_rosters()[lane_name]:
                calls.append(
                    {
                        "actionId": action_id,
                        "actionReceiptSha256": action_digests[action_id],
                        "role": role,
                        **({"family": family} if family is not None else {}),
                        "kind": kind,
                        "name": name,
                        "status": "passed",
                        "responseSha256": f"{index:x}" * 64,
                        "result": {
                            "status": "reconciled",
                            "identities": lane_identities[lane_name],
                        },
                    }
                )
            lanes[lane_name] = {
                "promptSha256": f"{index + 4:x}" * 64,
                "transcriptSha256": f"{index + 8:x}" * 64,
                "calls": calls,
            }
        return lanes

    def real_model_handoff(self, checkpoint: dict) -> Path:
        joins = arc.scalar_captures(checkpoint["resume"]["capturedVariables"])
        joins.update(
            {
                "candidateId": self.candidate_id,
                "releaseId": self.manifest["platformRelease"],
            }
        )
        lanes = self.model_lanes(checkpoint, joins)
        handoff = {
            "schemaVersion": "honua.studio.real-model-ai-arc-handoff/v1",
            "status": "paused",
            "target": "aws-ecs",
            "candidateId": self.candidate_id,
            "releaseId": self.manifest["platformRelease"],
            "endpointSha256": hashlib.sha256(self.endpoint.encode()).hexdigest(),
            "source": {
                "repository": "honua-io/honua-studio",
                "sha": self.shas["honua-studio"],
            },
            "components": {name: self.shas[name] for name in arc.ARC_COMPONENTS},
            "model": {"provider": "anthropic", "modelId": "claude-release-eval"},
            "promptVersion": "honua.aws-ecs.ai-arc.prompt/v1",
            "evalVersion": "honua.aws-ecs.ai-arc.eval/v1",
            "transcriptSha256": arc.canonical_sha256(
                [lanes[lane]["transcriptSha256"] for lane in arc.REAL_MODEL_LANES]
            ),
            "deterministic": {
                "target": "aws-ecs",
                "provisionReceiptSha256": checkpoint["provisionReceiptSha256"],
                "checkpointDigest": checkpoint["integrity"]["digest"],
            },
            "lanes": lanes,
            "joins": joins,
            "consoleReceiptRequest": checkpoint["consoleReceiptRequest"],
        }
        handoff["integrity"] = {
            "algorithm": "sha256",
            "digest": arc.canonical_sha256(handoff),
        }
        return write_json(self.root / "real-model-handoff.json", handoff)

    def console_evidence(
        self,
        checkpoint: dict,
        console: dict,
        console_path: Path,
        handoff_path: Path,
    ) -> Path:
        handoff = json.loads(handoff_path.read_text(encoding="utf-8"))
        recovery = {
            "status": "passed",
            "deliberateFailureJobId": "failed-job-1",
            "resumedJobId": "resumed-job-1",
            "actionableDiagnostics": True,
        }
        evidence = {
            "schemaVersion": arc.CONSOLE_EVIDENCE_SCHEMA,
            "status": "passed",
            "target": "aws-ecs",
            "candidate": {
                "candidateId": self.candidate_id,
                "releaseId": self.manifest["platformRelease"],
            },
            "endpointSha256": hashlib.sha256(self.endpoint.encode()).hexdigest(),
            "components": {name: self.shas[name] for name in arc.ARC_COMPONENTS},
            "handoffDigest": handoff["integrity"]["digest"],
            "checkpointDigest": checkpoint["integrity"]["digest"],
            "aggregateSha256": arc.sha256_file(console_path),
            "runtime": {
                "consoleCommit": self.shas["honua-console"],
                "serverSourceRevision": self.shas["honua-server"],
            },
            "publications": {
                family: {
                    "proposalId": console["proposals"][family]["proposalId"],
                    "executionOperationId": console["proposals"][family]["executionOperationId"],
                    "publicationId": console["publications"][family]["publicationId"],
                    "publicUrl": console["publications"][family]["publicUrl"],
                    "auditCorrelationId": console["audit"][family]["correlationId"],
                    "recovery": recovery,
                }
                for family in ("map", "app", "dashboard")
            },
            "checks": {
                "browser": "passed",
                "approval": "passed",
                "publication": "passed",
                "audit": "passed",
                "recovery": "passed",
            },
        }
        evidence["integrity"] = {
            "algorithm": "sha256",
            "digest": arc.canonical_sha256(evidence),
        }
        return write_json(self.root / "console-evidence.json", evidence)

    def real_model_receipt(
        self,
        checkpoint: dict,
        console: dict,
        console_path: Path,
        console_evidence_path: Path,
    ) -> tuple[dict, Path]:
        joins = arc.scalar_captures(checkpoint["resume"]["capturedVariables"])
        for family in ("map", "app", "dashboard"):
            proposal = console["proposals"][family]
            publication = console["publications"][family]
            audit = console["audit"][family]
            joins.update(
                {
                    f"{family}ProposalId": proposal["proposalId"],
                    f"{family}ExecutionOperationId": proposal["executionOperationId"],
                    f"{family}PublicationRequestId": publication["requestId"],
                    f"{family}PublicationId": publication["publicationId"],
                    f"{family}PublicationStatus": publication["status"],
                    f"{family}PublicUrl": publication["publicUrl"],
                    f"{family}AuditCorrelationId": audit["correlationId"],
                }
            )
        lanes = self.model_lanes(checkpoint, joins)
        receipt = {
            "schemaVersion": arc.REAL_MODEL_RECEIPT_SCHEMA,
            "id": "aws-ecs-real-model-ai-arc",
            "status": "passed",
            "target": "aws-ecs",
            "candidateId": self.candidate_id,
            "releaseId": self.manifest["platformRelease"],
            "endpointSha256": hashlib.sha256(self.endpoint.encode()).hexdigest(),
            "source": {
                "repository": "honua-io/honua-studio",
                "sha": self.shas["honua-studio"],
            },
            "components": {name: self.shas[name] for name in arc.ARC_COMPONENTS},
            "model": {"provider": "anthropic", "modelId": "claude-release-eval"},
            "promptVersion": "honua.aws-ecs.ai-arc.prompt/v1",
            "evalVersion": "honua.aws-ecs.ai-arc.eval/v1",
            "transcriptSha256": arc.canonical_sha256(
                [lanes[lane]["transcriptSha256"] for lane in arc.REAL_MODEL_LANES]
            ),
            "deterministic": {
                "target": "aws-ecs",
                "provisionReceiptSha256": checkpoint["provisionReceiptSha256"],
                "checkpointDigest": checkpoint["integrity"]["digest"],
                "consoleAggregateSha256": arc.sha256_file(console_path),
                "consoleEvidenceSha256": arc.sha256_file(console_evidence_path),
            },
            "lanes": lanes,
            "joins": joins,
            "checks": {check: "passed" for check in arc.REAL_MODEL_CHECKS},
            "evidence": {
                "url": "https://github.com/honua-io/honua-release/actions/runs/1",
                "sha256": "0" * 64,
            },
        }
        evidence = {
            "schemaVersion": arc.REAL_MODEL_EVIDENCE_SCHEMA,
            "candidateId": receipt["candidateId"],
            "releaseId": receipt["releaseId"],
            "endpointSha256": receipt["endpointSha256"],
            "source": receipt["source"],
            "model": receipt["model"],
            "promptVersion": receipt["promptVersion"],
            "evalVersion": receipt["evalVersion"],
            "transcriptSha256": receipt["transcriptSha256"],
            "target": "aws-ecs",
            "provisionReceiptSha256": checkpoint["provisionReceiptSha256"],
            "checkpointDigest": checkpoint["integrity"]["digest"],
            "consoleAggregateSha256": arc.sha256_file(console_path),
            "consoleEvidenceSha256": arc.sha256_file(console_evidence_path),
            "lanes": lanes,
            "joins": joins,
        }
        evidence_path = write_json(self.root / "real-model-evidence.json", evidence)
        receipt["evidence"]["sha256"] = arc.sha256_file(evidence_path)
        return receipt, evidence_path

    @mock.patch.object(arc.subprocess, "run")
    def test_admin_secret_resolver_uses_argv_and_never_shell(self, run: mock.Mock) -> None:
        run.return_value = subprocess.CompletedProcess([], 0, stdout="resolved-admin-key\n", stderr="")
        self.assertEqual("resolved-admin-key", arc.resolve_aws_secret(self.admin_reference_arn))
        command = run.call_args.args[0]
        self.assertEqual("aws", command[0])
        self.assertIn(self.admin_reference_arn, command)
        self.assertFalse(run.call_args.kwargs.get("shell", False))

    @mock.patch.object(arc, "resolve_aws_secret")
    def test_database_secret_is_checked_and_injected_only_into_child_env(self, resolve: mock.Mock) -> None:
        db_ref = "arn:aws:secretsmanager:us-west-2:123456789012:secret:honua-db-AbCd"
        resolve.return_value = (
            "Host=db.example;Port=5432;Database=honua;Username=honua;"
            "Password=resolved-db-value;SSL Mode=Require"
        )
        args = SimpleNamespace(
            db_connection_secret_ref=db_ref,
            db_host="db.example",
            db_port=5432,
            db_name="honua",
            db_user="honua",
            db_password_env="TEST_DB_PASSWORD",
        )
        hostile = {
            "AWS_ACCESS_KEY_ID": "sentinel-aws-access",
            "AWS_SECRET_ACCESS_KEY": "sentinel-aws-secret",
            "AWS_SESSION_TOKEN": "sentinel-aws-session",
            "HONUA_AI_ARC_CONSOLE_TOKEN": "sentinel-console",
            "OPENAI_API_KEY": "sentinel-provider",
            "UNRELATED_PRIVATE_VALUE": "sentinel-private",
        }
        with mock.patch.dict(arc.os.environ, hostile, clear=False):
            env = arc.child_environment(
                args,
                admin_secret="resolved-admin-value",
                base_url=self.endpoint,
                mcp_url=self.endpoint + "/mcp",
                sdk_source_sha=self.shas["honua-sdk-js"],
            )
        resolve.assert_called_once_with(db_ref, "database connection secret")
        self.assertEqual("resolved-db-value", env["TEST_DB_PASSWORD"])
        self.assertEqual(self.shas["honua-sdk-js"], env["HONUA_SOURCE_REVISION"])
        for name in hostile:
            self.assertNotIn(name, env)

    def test_checkpoint_integrity_and_secret_redaction_fail_closed(self) -> None:
        checkpoint = self.checkpoint()
        path = write_json(self.root / "checkpoint.json", checkpoint)
        arc.validate_checkpoint(
            path,
            self.candidate_id,
            self.manifest["platformRelease"],
            self.shas["honua-sdk-js"],
            self.endpoint + "/mcp",
            self.provision_path,
        )
        checkpoint["resume"]["capturedVariables"]["dbPassword"] = "must-not-persist"
        with self.assertRaisesRegex(arc.ArcError, "secret-shaped"):
            arc.reject_forbidden_serialization(
                checkpoint,
                ("dbpassword", "honua_admin_key", "honua_api_key", "authorization", "secretstring"),
                "SDK checkpoint contains a secret-shaped field",
            )

    def test_real_model_receipt_requires_same_endpoint_and_exact_deterministic_ids(self) -> None:
        checkpoint = self.checkpoint()
        console = self.console_receipt()
        console_path = write_json(self.root / "console.json", console)
        arc.validate_console_receipt(console_path, self.manifest, self.candidate_id, checkpoint)
        handoff_path = self.real_model_handoff(checkpoint)
        console_evidence_path = self.console_evidence(
            checkpoint, console, console_path, handoff_path
        )
        arc.validate_console_evidence(
            console_evidence_path,
            aggregate_path=console_path,
            real_model_handoff_path=handoff_path,
            console=console,
            manifest=self.manifest,
            expected_candidate_id=self.candidate_id,
            base_url=self.endpoint,
            checkpoint=checkpoint,
        )
        receipt, evidence_path = self.real_model_receipt(
            checkpoint, console, console_path, console_evidence_path
        )
        receipt_path = write_json(self.root / "real-model.json", receipt)
        arc.validate_real_model_receipt(
            receipt_path,
            evidence_path=evidence_path,
            manifest=self.manifest,
            expected_candidate_id=self.candidate_id,
            base_url=self.endpoint,
            checkpoint=checkpoint,
            console_receipt_path=console_path,
            console_evidence_path=console_evidence_path,
            console=console,
        )

        receipt["joins"]["esriMcpJobId"] = "different-job"
        write_json(receipt_path, receipt)
        with self.assertRaisesRegex(arc.ArcError, "esriMcpJobId"):
            arc.validate_real_model_receipt(
                receipt_path,
                evidence_path=evidence_path,
                manifest=self.manifest,
                expected_candidate_id=self.candidate_id,
                base_url=self.endpoint,
                checkpoint=checkpoint,
                console_receipt_path=console_path,
                console_evidence_path=console_evidence_path,
                console=console,
            )

    def test_real_model_receipt_rejects_secret_shaped_serialization(self) -> None:
        checkpoint = self.checkpoint()
        console = self.console_receipt()
        console_path = write_json(self.root / "console.json", console)
        handoff_path = self.real_model_handoff(checkpoint)
        console_evidence_path = self.console_evidence(
            checkpoint, console, console_path, handoff_path
        )
        receipt, evidence_path = self.real_model_receipt(
            checkpoint, console, console_path, console_evidence_path
        )
        receipt["model"]["apiKey"] = "must-not-be-serialized"
        evidence = json.loads(evidence_path.read_text(encoding="utf-8"))
        evidence["model"] = receipt["model"]
        with self.assertRaisesRegex(arc.ArcError, "forbidden secret"):
            arc.reject_forbidden_serialization(
                {"receipt": receipt, "evidence": evidence},
                ("password", "authorization", "api_key", "apikey", "secretstring", "fixture"),
                "AWS ECS real-model receipt contains forbidden secret/fixture material",
            )

    def test_real_model_receipt_rejects_missing_required_call_evidence(self) -> None:
        checkpoint = self.checkpoint()
        console = self.console_receipt()
        console_path = write_json(self.root / "console.json", console)
        handoff_path = self.real_model_handoff(checkpoint)
        console_evidence_path = self.console_evidence(
            checkpoint, console, console_path, handoff_path
        )
        receipt, evidence_path = self.real_model_receipt(
            checkpoint, console, console_path, console_evidence_path
        )
        receipt["lanes"]["admin"]["calls"] = [
            call
            for call in receipt["lanes"]["admin"]["calls"]
            if call["role"] != "service-access"
        ]
        receipt_path = write_json(self.root / "real-model.json", receipt)
        with self.assertRaisesRegex(arc.ArcError, "canonical action multiplicity"):
            arc.validate_real_model_receipt(
                receipt_path,
                evidence_path=evidence_path,
                manifest=self.manifest,
                expected_candidate_id=self.candidate_id,
                base_url=self.endpoint,
                checkpoint=checkpoint,
                console_receipt_path=console_path,
                console_evidence_path=console_evidence_path,
                console=console,
            )

    def test_real_model_roster_rejects_omission_extra_reorder_and_action_swap(self) -> None:
        checkpoint = self.checkpoint()
        joins = arc.scalar_captures(checkpoint["resume"]["capturedVariables"])
        joins.update(
            {
                "candidateId": self.candidate_id,
                "releaseId": self.manifest["platformRelease"],
            }
        )
        baseline = self.model_lanes(checkpoint, joins)
        action_digests = arc.checkpoint_action_receipt_digests(checkpoint)

        omission = copy.deepcopy(baseline)
        omission["admin"]["calls"] = [
            call
            for call in omission["admin"]["calls"]
            if call["actionId"] != "create-scoped-key"
        ]
        extra = copy.deepcopy(baseline)
        extra["admin"]["calls"].append(copy.deepcopy(extra["admin"]["calls"][0]))
        reordered = copy.deepcopy(baseline)
        reordered["admin"]["calls"][0], reordered["admin"]["calls"][1] = (
            reordered["admin"]["calls"][1],
            reordered["admin"]["calls"][0],
        )
        swapped = copy.deepcopy(baseline)
        first, second = swapped["admin"]["calls"][:2]
        first["actionId"], second["actionId"] = second["actionId"], first["actionId"]
        first["actionReceiptSha256"], second["actionReceiptSha256"] = (
            second["actionReceiptSha256"],
            first["actionReceiptSha256"],
        )

        for name, lanes in {
            "omission": omission,
            "extra": extra,
            "reordered": reordered,
            "swapped": swapped,
        }.items():
            with self.subTest(name=name):
                with self.assertRaisesRegex(arc.ArcError, "canonical action"):
                    arc.validate_real_model_lanes(lanes, joins, action_digests)

    def test_gpserver_action_requires_its_exact_job_identity(self) -> None:
        checkpoint = self.checkpoint()
        joins = arc.scalar_captures(checkpoint["resume"]["capturedVariables"])
        joins.update({"candidateId": self.candidate_id, "releaseId": self.manifest["platformRelease"]})
        lanes = self.model_lanes(checkpoint, joins)
        gpserver = next(
            call
            for call in lanes["nativeAnalysis"]["calls"]
            if call["actionId"] == "buffer-esri-gpserver"
        )
        gpserver["result"]["identities"].pop("gpServerJobId")

        with self.assertRaisesRegex(arc.ArcError, "buffer-esri-gpserver omits gpServerJobId"):
            arc.validate_real_model_lanes(
                lanes,
                joins,
                arc.checkpoint_action_receipt_digests(checkpoint),
            )

    def test_real_model_handoff_rejects_resealed_candidate_and_join_mutations(self) -> None:
        checkpoint = self.checkpoint()
        for name, mutate in {
            "component": lambda handoff: handoff["components"].__setitem__(
                "honua-server", "f" * 40
            ),
            "join": lambda handoff: handoff["joins"].__setitem__(
                "connectionId", "lookalike-connection"
            ),
        }.items():
            with self.subTest(name=name):
                handoff_path = self.real_model_handoff(checkpoint)
                handoff = json.loads(handoff_path.read_text(encoding="utf-8"))
                mutate(handoff)
                unsigned = dict(handoff)
                unsigned.pop("integrity")
                handoff["integrity"] = {
                    "algorithm": "sha256",
                    "digest": arc.canonical_sha256(unsigned),
                }
                write_json(handoff_path, handoff)
                with self.assertRaises(arc.ArcError):
                    arc.validate_real_model_handoff(
                        handoff_path,
                        manifest=self.manifest,
                        expected_candidate_id=self.candidate_id,
                        base_url=self.endpoint,
                        checkpoint=checkpoint,
                    )

    def test_real_model_receipt_rejects_unbound_sdk_action_receipt(self) -> None:
        checkpoint = self.checkpoint()
        console = self.console_receipt()
        console_path = write_json(self.root / "console.json", console)
        handoff_path = self.real_model_handoff(checkpoint)
        console_evidence_path = self.console_evidence(
            checkpoint, console, console_path, handoff_path
        )
        receipt, evidence_path = self.real_model_receipt(
            checkpoint, console, console_path, console_evidence_path
        )
        receipt["lanes"]["admin"]["calls"][0]["actionReceiptSha256"] = "f" * 64
        receipt_path = write_json(self.root / "real-model.json", receipt)
        with self.assertRaisesRegex(arc.ArcError, "not bound to SDK action receipt"):
            arc.validate_real_model_receipt(
                receipt_path,
                evidence_path=evidence_path,
                manifest=self.manifest,
                expected_candidate_id=self.candidate_id,
                base_url=self.endpoint,
                checkpoint=checkpoint,
                console_receipt_path=console_path,
                console_evidence_path=console_evidence_path,
                console=console,
            )

    def test_privileged_audit_reread_requires_exact_operations_and_verified_chain(self) -> None:
        console = self.console_receipt()
        observed_console = self.console_receipt()

        def fetch(_base_url: str, path: str, _secret: str, _label: str) -> dict:
            if path.endswith("/verify"):
                return {"verified": True, "rowsChecked": 3}
            family = next(
                name
                for name in ("map", "app", "dashboard")
                if observed_console["proposals"][name]["proposalId"] in path
            )
            proposal = observed_console["proposals"][family]
            audit = observed_console["audit"][family]
            if "/admin/proposals/" in path:
                return {
                    "proposalId": proposal["proposalId"],
                    "status": "Submitted",
                    "executionOperationId": proposal["executionOperationId"],
                }
            return {
                "items": [
                    {
                        "resourceType": "operation_proposal",
                        "resourceId": proposal["proposalId"],
                        "action": "operation.applied",
                        "outcome": "Success",
                        "correlationId": audit["correlationId"],
                    }
                ]
            }

        with mock.patch.object(arc, "fetch_admin_json", side_effect=fetch):
            arc.verify_privileged_console_audit(self.endpoint, console, "scoped-secret")

        console["proposals"]["map"]["executionOperationId"] = "wrong-operation"
        with mock.patch.object(arc, "fetch_admin_json", side_effect=fetch):
            with self.assertRaisesRegex(arc.ArcError, "map proposal is not durably applied"):
                arc.verify_privileged_console_audit(self.endpoint, console, "scoped-secret")

        console["proposals"]["map"]["executionOperationId"] = observed_console["proposals"]["map"][
            "executionOperationId"
        ]
        console["audit"]["map"]["correlationId"] = "wrong-correlation"
        with mock.patch.object(arc, "fetch_admin_json", side_effect=fetch):
            with self.assertRaisesRegex(arc.ArcError, "map audit identities"):
                arc.verify_privileged_console_audit(self.endpoint, console, "scoped-secret")

    def _complete_pre_teardown(self) -> dict:
        """The exact pre-teardown shape `finalize` is contractually allowed to seal."""
        return {
            "schemaVersion": "honua.aws-ecs.ai-delivery-arc-evidence/v1",
            "status": "awaiting-teardown",
            "target": "aws-ecs",
            "candidateId": self.candidate_id,
            "releaseId": self.manifest["platformRelease"],
            "source": {"repository": "honua-io/honua-devops", "sha": self.shas["honua-devops"]},
            "components": {name: self.shas[name] for name in arc.ARC_COMPONENTS},
            "endpoint": self.endpoint,
            "checks": {check: "passed" for check in arc.ARC_CHECKS},
            "artifacts": {name: "c" * 64 for name in arc.PRE_TEARDOWN_ARTIFACTS},
            "journey": {
                "journeyId": arc.JOURNEY_ID,
                "releaseContract": arc.RELEASE_CONTRACT,
                "shareUrl": "https://console.honua.io/share/abc",
                "actionCount": 12,
            },
        }

    def test_finalize_emits_both_exact_release_receipts_after_teardown(self) -> None:
        pre_path = write_json(self.root / "pre.json", self._complete_pre_teardown())
        teardown_path = write_json(
            self.root / "teardown.json",
            {
                "schemaVersion": arc.TEARDOWN_SCHEMA,
                "target": "aws-ecs",
                "status": "passed",
                "candidateId": self.candidate_id,
                "releaseId": self.manifest["platformRelease"],
                "components": {name: self.shas[name] for name in ("honua-devops", "honua-iac")},
                "checks": {"terraform-destroy": "passed", "cleanup-verified": "passed"},
                "evidence": {"url": "https://github.com/honua-io/honua-release/actions/runs/1", "sha256": "d" * 64},
            },
        )
        final_path = self.root / "final.json"
        provision_receipt = self.root / "aws-ecs-provision.json"
        arc_receipt = self.root / "aws-ecs-ai-delivery-arc.json"
        args = SimpleNamespace(
            manifest=self.manifest_path,
            pre_teardown_evidence=pre_path,
            teardown_evidence=teardown_path,
            evidence_url="https://github.com/honua-io/honua-release/actions/runs/1",
            final_evidence=final_path,
            provision_receipt=provision_receipt,
            arc_receipt=arc_receipt,
        )
        arc.finalize(args)
        provision = json.loads(provision_receipt.read_text(encoding="utf-8"))
        journey = json.loads(arc_receipt.read_text(encoding="utf-8"))
        self.assertEqual(set(arc.PROVISION_CHECKS), set(provision["claims"]["checks"]))
        self.assertEqual(set(arc.ARC_CHECKS), set(journey["claims"]["checks"]))
        self.assertEqual(self.candidate_id, journey["candidateId"])
        self.assertEqual(
            hashlib.sha256(final_path.read_bytes()).hexdigest(), journey["evidence"]["sha256"]
        )
        self.assertEqual(set(arc.ARC_COMPONENTS), set(journey["components"]))

    def test_finalize_rejects_a_truncated_pre_teardown_artifact_roster(self) -> None:
        """A pre-teardown file whose candidate/component/check fields still look correct must
        not be sealable when its artifact roster has been truncated or replaced. Previously a
        file carrying one digest was enough to emit both passed release receipts, letting
        missing SDK, Console, handoff, checkpoint, and real-model evidence be sealed as passed.
        """
        truncated = self._complete_pre_teardown()
        truncated["artifacts"] = {"sdkJourneyReceipt": "c" * 64}
        with self.assertRaisesRegex(arc.ArcError, "artifact roster is incomplete"):
            arc.finalize(self._finalize_args(truncated))

    def test_finalize_rejects_a_non_digest_artifact_entry(self) -> None:
        replaced = self._complete_pre_teardown()
        replaced["artifacts"]["sdkCheckpoint"] = "not-a-digest"
        with self.assertRaisesRegex(arc.ArcError, "sdkCheckpoint is not a SHA-256 digest"):
            arc.finalize(self._finalize_args(replaced))

    def test_finalize_rejects_pre_teardown_with_a_foreign_source_identity(self) -> None:
        foreign = self._complete_pre_teardown()
        foreign["source"] = {"repository": "attacker/repo", "sha": self.shas["honua-devops"]}
        with self.assertRaisesRegex(arc.ArcError, "source identity differs"):
            arc.finalize(self._finalize_args(foreign))

    def test_finalize_rejects_pre_teardown_with_a_foreign_target(self) -> None:
        foreign = self._complete_pre_teardown()
        foreign["target"] = "aws-serverless"
        with self.assertRaisesRegex(arc.ArcError, "not bound to the aws-ecs target"):
            arc.finalize(self._finalize_args(foreign))

    def test_finalize_rejects_pre_teardown_without_journey_identity(self) -> None:
        for mutate, expected in (
            (lambda pre: pre.pop("journey"), "carries no journey identity"),
            (lambda pre: pre["journey"].__setitem__("journeyId", "other"), "journey identity does not match"),
            (lambda pre: pre["journey"].__setitem__("actionCount", 0), "records no completed actions"),
            (lambda pre: pre["journey"].__setitem__("shareUrl", "http://console.local"), "share URL"),
        ):
            with self.subTest(expected=expected):
                pre = self._complete_pre_teardown()
                mutate(pre)
                with self.assertRaisesRegex(arc.ArcError, expected):
                    arc.finalize(self._finalize_args(pre))

    def test_action_map_rejects_duplicate_action_ids(self) -> None:
        """A later passing duplicate must not be able to hide an earlier failed action.

        `validate_passed_journey` evaluates the collapsed map, so a silent last-one-wins
        assignment would let malformed runtime evidence pass every required-group check.
        """
        receipt = {
            "stages": [
                {"id": "s1", "actions": [{"id": "create-map", "status": "failed"}]},
                {"id": "s2", "actions": [{"id": "create-map", "status": "passed"}]},
            ]
        }
        with self.assertRaisesRegex(arc.ArcError, "duplicates action create-map"):
            arc.action_map(receipt)

    def test_action_map_accepts_distinct_action_ids(self) -> None:
        receipt = {
            "stages": [
                {"id": "s1", "actions": [{"id": "create-map", "status": "passed"}]},
                {"id": "s2", "actions": [{"id": "publish-map", "status": "passed"}]},
            ]
        }
        self.assertEqual({"create-map", "publish-map"}, set(arc.action_map(receipt)))

    def test_privileged_admin_read_never_follows_a_redirect(self) -> None:
        """urllib's default redirect handler copies `x-api-key` onto the redirected request,
        so a compromised or misconfigured candidate could redirect a privileged audit read to
        another origin and receive the resolved admin secret.
        """
        handler = arc._NoRedirectHandler()
        with self.assertRaisesRegex(arc.ArcError, "redirect"):
            handler.redirect_request(
                None,
                None,
                302,
                "Found",
                {},
                "https://attacker.example/collect",
            )

    def _finalize_args(self, pre: dict) -> SimpleNamespace:
        suffix = uuid.uuid4().hex
        pre_path = write_json(self.root / f"pre-{suffix}.json", pre)
        teardown_path = write_json(
            self.root / f"teardown-{suffix}.json",
            {
                "schemaVersion": arc.TEARDOWN_SCHEMA,
                "target": "aws-ecs",
                "status": "passed",
                "candidateId": self.candidate_id,
                "releaseId": self.manifest["platformRelease"],
                "components": {name: self.shas[name] for name in ("honua-devops", "honua-iac")},
                "checks": {"terraform-destroy": "passed", "cleanup-verified": "passed"},
                "evidence": {
                    "url": "https://github.com/honua-io/honua-release/actions/runs/1",
                    "sha256": "d" * 64,
                },
            },
        )
        return SimpleNamespace(
            manifest=self.manifest_path,
            pre_teardown_evidence=pre_path,
            teardown_evidence=teardown_path,
            evidence_url="https://github.com/honua-io/honua-release/actions/runs/1",
            final_evidence=self.root / f"final-{suffix}.json",
            provision_receipt=self.root / f"provision-{suffix}.json",
            arc_receipt=self.root / f"arc-{suffix}.json",
        )


if __name__ == "__main__":
    unittest.main()
