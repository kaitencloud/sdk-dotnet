# Copyright 2026 KAITEN INC. SPDX-License-Identifier: Apache-2.0
"""Standard-library tests for standalone snapshot and coverage policy guards."""
import contextlib
import io
import json
from pathlib import Path
import shutil
import sys
import tempfile
import unittest

sys.dont_write_bytecode = True
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "scripts"))
import check_contracts


class ContractChecks(unittest.TestCase):
    def setUp(self):
        self.original_root = check_contracts.ROOT
        self.temporary = tempfile.TemporaryDirectory(prefix="contract-check-")
        self.root = Path(self.temporary.name)
        shutil.copytree(self.original_root / "contracts", self.root / "contracts")
        check_contracts.ROOT = self.root

    def tearDown(self):
        check_contracts.ROOT = self.original_root
        self.temporary.cleanup()

    def test_pinned_snapshots_are_standalone_and_webhooks_are_not_rest_operations(self):
        documents = check_contracts.load_contracts()
        self.assertEqual(239, len(check_contracts.coverage_rows(documents)))
        self.assertEqual(117, len(documents["core"]["webhooks"]))
        with contextlib.redirect_stdout(io.StringIO()):
            check_contracts.main()

    def test_unreviewed_schema_drift_fails_checksum(self):
        path = self.root / "contracts/core.openapi.json"
        path.write_text(path.read_text() + " ")
        with self.assertRaisesRegex(ValueError, "checksum"):
            check_contracts.load_contracts()

    def test_coverage_does_not_claim_experiment_as_implemented_facade(self):
        path = self.root / "contracts/coverage.json"
        coverage = json.loads(path.read_text())
        self.assertEqual(172, sum(row["status"] == "pending-parity" for row in coverage["operations"]))
        self.assertEqual(64, sum(row["status"] == "management-facade" for row in coverage["operations"]))
        self.assertEqual(1, sum(row["status"] == "existing-client" for row in coverage["operations"]))
        coverage["operations"][0]["status"] = "generated-means-supported"
        path.write_text(json.dumps(coverage))
        with self.assertRaisesRegex(ValueError, "Unknown coverage"):
            check_contracts.main()

    def test_excluded_ofrep_can_never_be_marked_as_sdk_coverage(self):
        path = self.root / "contracts/coverage.json"
        coverage = json.loads(path.read_text())
        row = next(row for row in coverage["operations"] if check_contracts.forbidden_path(row["path"]))
        row["status"] = "existing-client"
        path.write_text(json.dumps(coverage))
        with self.assertRaisesRegex(ValueError, "OFREP"):
            check_contracts.main()

    def test_duplicate_or_missing_coverage_entry_fails(self):
        path = self.root / "contracts/coverage.json"
        coverage = json.loads(path.read_text())
        coverage["operations"].append(coverage["operations"][0])
        path.write_text(json.dumps(coverage))
        with self.assertRaisesRegex(ValueError, "exactly one"):
            check_contracts.main()

    def test_policy_filter_covers_entire_ofrep_subtree(self):
        self.assertTrue(check_contracts.forbidden_path("/ofrep/v1"))
        self.assertTrue(check_contracts.forbidden_path("/ofrep/v1/evaluate/flags/future"))
        self.assertFalse(check_contracts.forbidden_path("/feature-flags/tracker"))
        self.assertFalse(check_contracts.forbidden_path("/openfeature/v0/manifest"))


if __name__ == "__main__":
    unittest.main()
