"""Challenge the receipt gate with incomplete or failed copies of the live fixture."""
import copy
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import xml.etree.ElementTree as ET

HERE = Path(__file__).resolve().parent
TAG = "nightly-80e23be"
SPEC = importlib.util.spec_from_file_location("summarize", HERE / "summarize.py")
SUMMARY = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(SUMMARY)


class ReceiptGateTests(unittest.TestCase):
    def setUp(self):
        self.receipt = json.loads((HERE / f"receipt-{TAG}.json").read_text())
        self.part = {"cells": copy.deepcopy(self.receipt["cells"]),
                     "server": self.receipt["observedServers"][0]}

    def run_summary(self, failed_test=False, empty_transcript=False):
        with tempfile.TemporaryDirectory() as directory:
            work = Path(directory)
            (work / "part.json").write_text(json.dumps(self.part))
            transcript = (HERE / f"devops-live-{TAG}.jsonl").read_text()
            (work / "live.jsonl").write_text("" if empty_transcript else transcript)
            trx = ET.parse(HERE / f"devops-live-{TAG}.trx")
            if failed_test:
                trx.find(".//{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}UnitTestResult").set("outcome", "Failed")
            trx.write(work / "live.trx")
            candidate = self.receipt["candidate"]
            args = ["summarize.py", "--image", candidate["image"], "--revision", candidate["revision"],
                    "--index", candidate["indexDigest"], "--parts", str(work / "part.json"),
                    "--devops", str(work / "live.jsonl"), "--devops-trx", str(work / "live.trx"),
                    "--out-dir", directory, "--tag", "test"]
            with patch("sys.argv", args):
                SUMMARY.main()
            return json.loads((work / "receipt-test.json").read_text())

    def test_complete_installed_fixture_is_accepted(self):
        result = self.run_summary()
        self.assertEqual(11, len(result["cells"]))

    def test_cross_tenant_class_cannot_be_omitted(self):
        self.part["cells"] = [cell for cell in self.part["cells"] if cell["name"] != "platform-admin-cross-tenant"]
        with self.assertRaisesRegex(SystemExit, "every recovery class"):
            self.run_summary()

    def test_failed_assertion_cannot_be_hidden_by_pass_label(self):
        self.part["cells"][0]["assertions"][0]["held"] = False
        with self.assertRaisesRegex(SystemExit, "passing assertions"):
            self.run_summary()

    def test_duplicate_class_is_refused(self):
        self.part["cells"].append(copy.deepcopy(self.part["cells"][0]))
        with self.assertRaisesRegex(SystemExit, "every recovery class once"):
            self.run_summary()

    def test_missing_server_identity_is_refused(self):
        del self.part["server"]
        with self.assertRaisesRegex(SystemExit, "Every run part requires inspected server"):
            self.run_summary()

    def test_wrong_server_revision_is_refused(self):
        self.part["server"]["revision"] = "wrong-server-revision"
        with self.assertRaisesRegex(SystemExit, "inspected server image"):
            self.run_summary()

    def test_failed_live_test_cannot_be_hidden_by_disposal_transcript(self):
        with self.assertRaisesRegex(SystemExit, "matching passed test results"):
            self.run_summary(failed_test=True)

    def test_disabled_live_tests_do_not_qualify(self):
        with self.assertRaisesRegex(SystemExit, "five distinct live transcripts"):
            self.run_summary(empty_transcript=True)


if __name__ == "__main__":
    unittest.main()
