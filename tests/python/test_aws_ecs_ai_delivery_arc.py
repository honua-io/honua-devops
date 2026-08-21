from __future__ import annotations

import hashlib
import importlib.util
import json
import subprocess
import tempfile
import unittest
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
        self.secret_ref = "arn:aws:secretsmanager:us-west-2:123456789012:secret:honua-admin-AbCd"
        self.handoff_path = write_json(
            self.root / "handoff.json",
            {
                "schemaVersion": arc.HANDOFF_SCHEMA,
                "env": {
                    "HONUA_BASE_URL": self.endpoint,
                    "HONUA_MCP_REMOTE_URL": self.endpoint + "/mcp",
                },
                "secretRefs": {"HONUA_ADMIN_KEY": self.secret_ref},
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
                "adminKeySecretRef": self.secret_ref,
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

    def checkpoint(self) -> dict:
        captures = {
            "candidateId": self.candidate_id,
            "releaseId": self.manifest["platformRelease"],
            "connectionId": "connection-1",
            "serviceName": "zero-to-map",
            "parcelsLayerId": 1,
            "zoningLayerId": 2,
            "esriMcpJobId": "esri-mcp-job-1",
            "gpServerJobId": "gpserver-job-1",
            "directAnalysisJobId": "native-job-1",
            "mapItemId": "map-item-1",
            "mapVersionId": "map-version-1",
            "appItemId": "app-item-1",
            "appVersionId": "app-version-1",
            "dashboardItemId": "dashboard-item-1",
            "dashboardVersionId": "dashboard-version-1",
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
        }
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
                "completedStages": [],
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

    def real_model_receipt(self, checkpoint: dict, console: dict) -> tuple[dict, Path]:
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
        lane_identities = {
            "admin": {
                "candidateId": self.candidate_id,
                "connectionId": "connection-1",
                "parcelsLayerId": 1,
                "zoningLayerId": 2,
                "serviceName": "zero-to-map",
            },
            "esriGp": {"candidateId": self.candidate_id, "esriMcpJobId": "esri-mcp-job-1"},
            "nativeAnalysis": {
                "candidateId": self.candidate_id,
                "directAnalysisJobId": "native-job-1",
            },
            "studioPublication": {
                "candidateId": self.candidate_id,
                "mapProposalId": "map-proposal-1",
                "appProposalId": "app-proposal-1",
                "dashboardProposalId": "dashboard-proposal-1",
                "mapPublicationVersionId": "map-publication-version-1",
                "appPublicationVersionId": "app-publication-version-1",
                "dashboardPublicationVersionId": "dashboard-publication-version-1",
            },
        }
        rosters = {lane: set(values) for lane, values in arc.REAL_MODEL_ROSTER.items()}
        rosters["esriGp"] = {
            (role, family, kind, name.format(esriMcpJobId="esri-mcp-job-1"))
            for role, family, kind, name in rosters["esriGp"]
        }
        rosters["nativeAnalysis"] = {
            (role, family, kind, name.format(directAnalysisJobId="native-job-1"))
            for role, family, kind, name in rosters["nativeAnalysis"]
        }
        rosters["studioPublication"] = {
            (role, family, "mcp", tool)
            for family in ("map", "app", "dashboard")
            for role, tool in arc.STUDIO_REAL_MODEL_ROLES
        }
        lanes = {}
        for index, lane_name in enumerate(arc.REAL_MODEL_LANES, start=1):
            calls = []
            for role, family, kind, name in sorted(
                rosters[lane_name], key=lambda value: tuple("" if item is None else item for item in value)
            ):
                calls.append(
                    {
                        "role": role,
                        **({"family": family} if family is not None else {}),
                        "kind": kind,
                        "name": name,
                        "status": "passed",
                        "responseSha256": f"{index:x}" * 64,
                        "result": {
                            "status": "completed",
                            "identities": lane_identities[lane_name],
                        },
                    }
                )
            lanes[lane_name] = {
                "promptSha256": f"{index + 4:x}" * 64,
                "transcriptSha256": f"{index + 8:x}" * 64,
                "calls": calls,
            }
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
            "transcriptSha256": "a" * 64,
            "deterministic": {
                "target": "aws-ecs",
                "provisionReceiptSha256": checkpoint["provisionReceiptSha256"],
                "checkpointDigest": checkpoint["integrity"]["digest"],
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
            "lanes": lanes,
            "joins": joins,
        }
        evidence_path = write_json(self.root / "real-model-evidence.json", evidence)
        receipt["evidence"]["sha256"] = arc.sha256_file(evidence_path)
        return receipt, evidence_path

    @mock.patch.object(arc.subprocess, "run")
    def test_admin_secret_resolver_uses_argv_and_never_shell(self, run: mock.Mock) -> None:
        run.return_value = subprocess.CompletedProcess([], 0, stdout="resolved-admin-key\n", stderr="")
        self.assertEqual("resolved-admin-key", arc.resolve_aws_secret(self.secret_ref))
        command = run.call_args.args[0]
        self.assertEqual("aws", command[0])
        self.assertIn(self.secret_ref, command)
        self.assertFalse(run.call_args.kwargs.get("shell", False))

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
        write_json(path, checkpoint)
        with self.assertRaisesRegex(arc.ArcError, "secret-shaped"):
            arc.validate_checkpoint(path, self.candidate_id, self.manifest["platformRelease"])

    def test_real_model_receipt_requires_same_endpoint_and_exact_deterministic_ids(self) -> None:
        checkpoint = self.checkpoint()
        console = self.console_receipt()
        console_path = write_json(self.root / "console.json", console)
        arc.validate_console_receipt(console_path, self.manifest, self.candidate_id, checkpoint)
        receipt, evidence_path = self.real_model_receipt(checkpoint, console)
        receipt_path = write_json(self.root / "real-model.json", receipt)
        arc.validate_real_model_receipt(
            receipt_path,
            evidence_path=evidence_path,
            manifest=self.manifest,
            expected_candidate_id=self.candidate_id,
            base_url=self.endpoint,
            checkpoint=checkpoint,
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
                console=console,
            )

    def test_real_model_receipt_rejects_secret_shaped_serialization(self) -> None:
        checkpoint = self.checkpoint()
        console = self.console_receipt()
        receipt, evidence_path = self.real_model_receipt(checkpoint, console)
        receipt["model"]["modelId"] = "secretstring-must-not-be-serialized"
        evidence = json.loads(evidence_path.read_text(encoding="utf-8"))
        evidence["model"] = receipt["model"]
        write_json(evidence_path, evidence)
        receipt["evidence"]["sha256"] = arc.sha256_file(evidence_path)
        receipt_path = write_json(self.root / "real-model.json", receipt)
        with self.assertRaisesRegex(arc.ArcError, "forbidden secret"):
            arc.validate_real_model_receipt(
                receipt_path,
                evidence_path=evidence_path,
                manifest=self.manifest,
                expected_candidate_id=self.candidate_id,
                base_url=self.endpoint,
                checkpoint=checkpoint,
                console=console,
            )

    def test_real_model_receipt_rejects_missing_required_call_evidence(self) -> None:
        checkpoint = self.checkpoint()
        console = self.console_receipt()
        receipt, evidence_path = self.real_model_receipt(checkpoint, console)
        receipt["lanes"]["admin"]["calls"] = [
            call
            for call in receipt["lanes"]["admin"]["calls"]
            if call["role"] != "service-access"
        ]
        receipt_path = write_json(self.root / "real-model.json", receipt)
        with self.assertRaisesRegex(arc.ArcError, "lacks required calls"):
            arc.validate_real_model_receipt(
                receipt_path,
                evidence_path=evidence_path,
                manifest=self.manifest,
                expected_candidate_id=self.candidate_id,
                base_url=self.endpoint,
                checkpoint=checkpoint,
                console=console,
            )

    def test_finalize_emits_both_exact_release_receipts_after_teardown(self) -> None:
        pre_path = write_json(
            self.root / "pre.json",
            {
                "schemaVersion": "honua.aws-ecs.ai-delivery-arc-evidence/v1",
                "status": "awaiting-teardown",
                "target": "aws-ecs",
                "candidateId": self.candidate_id,
                "releaseId": self.manifest["platformRelease"],
                "source": {"repository": "honua-io/honua-devops", "sha": self.shas["honua-devops"]},
                "components": {name: self.shas[name] for name in arc.ARC_COMPONENTS},
                "checks": {check: "passed" for check in arc.ARC_CHECKS},
                "artifacts": {"sdkJourneyReceipt": "c" * 64},
                "journey": {"journeyId": arc.JOURNEY_ID, "releaseContract": arc.RELEASE_CONTRACT},
            },
        )
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


if __name__ == "__main__":
    unittest.main()
