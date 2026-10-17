"""Reconciler tests for the M7 release evidence gate (ReleaseQualification.py).

Every test drives the real CLI as a subprocess against synthetic
manifest/matrix/record fixtures written under a temp directory. The gate's
contract (plan "Shared contracts"):

- ``--manifest <release-manifest.json> --matrix <acceptance-matrix.json>
  --evidence <new-directory> --records <record.json>...``
- writes ``<evidence>/result.json`` with verdict ``passed|failed|partial``,
  per-row reasons, and a ``notVerified`` row list;
- exit 0 only when every required row passed; any fail/gap exits nonzero;
- never edits the candidate and never fills absent evidence with success;
- fixture evidence can never satisfy a production row, and development-build
  evidence can never satisfy an ordinary-package row.

Dishonesty mutants named by the suite: a missing required row, a fixture
record presented to a production row, a measured artifact hash that
contradicts the manifest, development evidence on an ordinary row, a
tampered evidence file, an unknown or duplicated row id, and a human-review
row without a named reviewer.
"""

import hashlib
import json
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

TOOLS = Path(__file__).resolve().parent
GATE = TOOLS / "ReleaseQualification.py"
COMMIT = "a" * 40


def sha256_file(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def write_json(path, obj):
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(obj, indent=2) + "\n", encoding="utf-8")
    return path


def write_manifest(directory, with_windows=True, with_android=False,
                   version_code=2, previous_code=1):
    """A schema-1 release manifest over real fixture files in *directory*."""
    directory = Path(directory)
    files = []
    windows = None
    if with_windows:
        pkg = directory / "FamilyRacing-2.0.0-Windows-x64.zip"
        pkg.write_bytes(b"fixture-windows-player")
        files.append({"path": pkg.name, "sha256": sha256_file(pkg),
                      "sizeBytes": pkg.stat().st_size})
        windows = {"package": pkg.name, "sha256": sha256_file(pkg),
                   "sizeBytes": pkg.stat().st_size, "arch": "x64",
                   "backend": "Mono", "authenticodeSigned": False}
    android = {"package": None, "sha256": None, "sizeBytes": None,
               "applicationId": "com.daniel.familyracing",
               "minSdk": 25, "targetSdk": 36, "abi": "arm64-v8a",
               "signed": False, "certificateSha256": None, "signer": "none"}
    if with_android:
        apk = directory / "FamilyRacing-2.0.0-Android.apk"
        apk.write_bytes(b"fixture-android-apk")
        files.append({"path": apk.name, "sha256": sha256_file(apk),
                      "sizeBytes": apk.stat().st_size})
        android.update({"package": apk.name, "sha256": sha256_file(apk),
                        "sizeBytes": apk.stat().st_size,
                        "signed": True, "certificateSha256": "c" * 64,
                        "signer": "test-key"})
    manifest = {
        "schemaVersion": 1,
        "generatedUtc": "2026-10-17T00:00:00Z",
        "packageKind": "candidate",
        "releaseCandidate": True,
        "acceptanceStatus": "unqualified",
        "source": {"commit": COMMIT, "cleanSource": True, "dirtyAreas": []},
        "tools": {"unityEditor": "6000.3.26f1", "powershell": "5.1",
                  "git": "2.55.0"},
        "identity": {"versionName": "2.0.0",
                     "androidVersionCode": version_code,
                     "previousAndroidVersionCode": previous_code,
                     "applicationId": "com.daniel.familyracing",
                     "companyName": "Family Racing",
                     "productName": "Family Racing"},
        "gameplay": {"gameplayVersion": "gameplay-4",
                     "contentVersion": "full-content-1",
                     "lanProtocol": 3, "observationSchema": 7,
                     "profileSchema": 3, "backend": "arcade"},
        "contentStage": "complete",
        "windows": windows,
        "packages": {"android": android},
        "files": files,
        "blockers": []}
    return write_json(directory / "release-manifest.json", manifest)


def make_row(row_id, evidence_class="production", build_kind="ordinary",
             artifact="none", human=False, physical=False, required=True):
    return {"id": row_id, "required": required, "evidenceClass": evidence_class,
            "buildKind": build_kind, "artifact": artifact,
            "requiresHumanReview": human, "physical": physical,
            "description": "synthetic test row " + row_id,
            "source": "test"}


def write_matrix(directory, rows):
    matrix = {"schemaVersion": 1, "matrixId": "test-matrix",
              "description": "synthetic test matrix", "rows": rows}
    return write_json(directory / "acceptance-matrix.json", matrix)


def make_record(directory, row_id, name, outcome="passed",
                evidence_class="production", build_kind="ordinary",
                artifact=None, commit=COMMIT, reviewer=None,
                device=None, evidence_files=None):
    record = {
        "schemaVersion": 1,
        "rowId": row_id,
        "outcome": outcome,
        "evidenceClass": evidence_class,
        "buildKind": build_kind,
        "source": {"commit": commit, "contentVersion": "full-content-1"},
        "method": "synthetic test observation",
        "observedUtc": "2026-10-17T00:00:00Z",
        "notes": ""}
    if artifact is not None:
        record["artifact"] = artifact
    if reviewer is not None:
        record["humanReviewer"] = reviewer
    if device is not None:
        record["device"] = device
    if evidence_files is not None:
        record["evidenceFiles"] = evidence_files
    return write_json(Path(directory) / name, record)


def run_gate(manifest, matrix, records, evidence_dir):
    """Invoke the real CLI; returns (exit_code, result_json_or_None, proc)."""
    argv = [sys.executable, str(GATE),
            "--manifest", str(manifest), "--matrix", str(matrix),
            "--evidence", str(evidence_dir)]
    if records:
        argv.append("--records")
        argv.extend(str(r) for r in records)
    proc = subprocess.run(argv, capture_output=True, text=True)
    result_path = Path(evidence_dir) / "result.json"
    result = json.loads(result_path.read_text(encoding="utf-8")) \
        if result_path.exists() else None
    return proc.returncode, result, proc


def row_status(result, row_id):
    for row in result["rows"]:
        if row["id"] == row_id:
            return row["status"], row["reasons"]
    raise AssertionError("row missing from result: " + row_id)


class MissingRequiredRowIsPartial(unittest.TestCase):
    """A required row with no evidence is a gap, never a silent pass:
    the verdict is ``partial`` and the row lands in ``notVerified``."""

    def test_absent_required_row_blocks_release_as_partial(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            manifest = write_manifest(root)
            matrix = write_matrix(root, [make_row("package.identity.windows",
                                                  artifact="windows")])
            code, result, proc = run_gate(manifest, matrix, [],
                                          root / "evidence")
            self.assertEqual(code, 1, proc.stderr)
            self.assertIsNotNone(result)
            self.assertEqual(result["verdict"], "partial")
            status, _ = row_status(result, "package.identity.windows")
            self.assertEqual(status, "notVerified")
            self.assertIn("package.identity.windows", result["notVerified"])


class FixtureCannotPassProduction(unittest.TestCase):
    """Fixture evidence satisfies fixture rows only. A complete fixture
    matrix may pass (that is the tooling self-test); the same fixture
    record on a production row can never produce a release pass."""

    def test_fixture_record_refused_for_production_row(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            manifest = write_manifest(root)
            matrix = write_matrix(root, [make_row(
                "physical.updates-recovery.windows", physical=True)])
            record = make_record(
                root, "physical.updates-recovery.windows",
                "rec-fixture.json", evidence_class="fixture",
                build_kind="fixture",
                device={"label": "fixture-install", "os": "fixture"})
            code, result, proc = run_gate(manifest, matrix, [record],
                                          root / "evidence")
            self.assertNotEqual(code, 0, proc.stderr)
            self.assertEqual(result["verdict"], "partial")
            status, reasons = row_status(
                result, "physical.updates-recovery.windows")
            self.assertEqual(status, "notVerified")
            self.assertTrue(any("fixture" in r for r in reasons), reasons)

    def test_complete_fixture_matrix_passes_only_as_tooling(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            manifest = write_manifest(root)
            rows = [make_row("tooling.update.alpha", evidence_class="fixture",
                             build_kind="any"),
                    make_row("tooling.update.beta", evidence_class="fixture",
                             build_kind="any")]
            matrix = write_matrix(root, rows)
            evidence_file = root / "rehearsal-log.txt"
            evidence_file.write_text("fixture rehearsal log", encoding="utf-8")
            records = [
                make_record(root, row["id"], "rec-%d.json" % i,
                            evidence_class="fixture", build_kind="fixture",
                            evidence_files=[{"path": evidence_file.name,
                                             "sha256": sha256_file(evidence_file)}])
                for i, row in enumerate(rows)]
            code, result, proc = run_gate(manifest, matrix, records,
                                          root / "evidence")
            self.assertEqual(code, 0, proc.stderr + json.dumps(result))
            self.assertEqual(result["verdict"], "passed")


class ArtifactHashMismatchFails(unittest.TestCase):
    """A record whose measured package bytes contradict the manifest is a
    hard integrity failure, not a soft gap."""

    def test_wrong_measured_hash_fails_the_row(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            manifest = write_manifest(root, with_windows=True)
            matrix = write_matrix(root, [make_row(
                "package.identity.windows", artifact="windows")])
            record = make_record(
                root, "package.identity.windows", "rec.json",
                artifact={"path": "FamilyRacing-2.0.0-Windows-x64.zip",
                          "sha256": "0" * 64})
            code, result, proc = run_gate(manifest, matrix, [record],
                                          root / "evidence")
            self.assertEqual(code, 1, proc.stderr)
            self.assertEqual(result["verdict"], "failed")
            status, reasons = row_status(result, "package.identity.windows")
            self.assertEqual(status, "failed")
            self.assertTrue(any("sha256" in r or "hash" in r
                                for r in reasons), reasons)


class DevelopmentCannotSatisfyOrdinaryRow(unittest.TestCase):
    """Development-build evidence identifies its own artifact kind; it can
    never satisfy a row that requires the ordinary family package."""

    def test_development_record_leaves_ordinary_row_open(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            manifest = write_manifest(root)
            matrix = write_matrix(root, [make_row(
                "physical.fresh-install-offline.windows",
                build_kind="ordinary", physical=True)])
            record = make_record(
                root, "physical.fresh-install-offline.windows", "rec.json",
                build_kind="development",
                device={"label": "dev-pc", "os": "Windows"})
            code, result, proc = run_gate(manifest, matrix, [record],
                                          root / "evidence")
            self.assertEqual(code, 1, proc.stderr)
            self.assertEqual(result["verdict"], "partial")
            status, reasons = row_status(
                result, "physical.fresh-install-offline.windows")
            self.assertEqual(status, "notVerified")
            self.assertTrue(any("development" in r for r in reasons), reasons)


class TamperedEvidenceFails(unittest.TestCase):
    """Evidence files are hash-pinned inside the record; mutating a file
    after the fact is an integrity failure, not a soft mismatch."""

    def test_modified_evidence_file_fails(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            manifest = write_manifest(root)
            matrix = write_matrix(root, [make_row("tooling.probe",
                                                  evidence_class="fixture",
                                                  build_kind="any")])
            evidence = root / "observation.log"
            evidence.write_text("honest bytes", encoding="utf-8")
            record = make_record(
                root, "tooling.probe", "rec.json",
                evidence_class="fixture", build_kind="fixture",
                evidence_files=[{"path": "observation.log",
                                 "sha256": sha256_file(evidence)}])
            evidence.write_text("tampered bytes", encoding="utf-8")
            code, result, proc = run_gate(manifest, matrix, [record],
                                          root / "evidence")
            self.assertEqual(code, 1, proc.stderr)
            self.assertEqual(result["verdict"], "failed")
            status, reasons = row_status(result, "tooling.probe")
            self.assertEqual(status, "failed")
            self.assertTrue(any("evidence" in r for r in reasons), reasons)


class UnknownOrDuplicateRowFails(unittest.TestCase):
    """Records must land on declared matrix rows exactly once: an unknown
    row id or a second record for the same row is an integrity failure."""

    def test_unknown_row_id_fails(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            manifest = write_manifest(root)
            matrix = write_matrix(root, [make_row("real.row")])
            record = make_record(root, "invented.row", "rec.json")
            code, result, proc = run_gate(manifest, matrix, [record],
                                          root / "evidence")
            self.assertEqual(code, 1, proc.stderr)
            self.assertEqual(result["verdict"], "failed")
            self.assertTrue(result["integrityFailures"])

    def test_duplicate_row_id_fails(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            manifest = write_manifest(root)
            matrix = write_matrix(root, [make_row("real.row")])
            records = [make_record(root, "real.row", "a.json"),
                       make_record(root, "real.row", "b.json")]
            code, result, proc = run_gate(manifest, matrix, records,
                                          root / "evidence")
            self.assertEqual(code, 1, proc.stderr)
            self.assertEqual(result["verdict"], "failed")
            self.assertTrue(any("duplicate" in f.lower()
                                for f in result["integrityFailures"]),
                            result["integrityFailures"])


class HumanRowNeedsNamedReview(unittest.TestCase):
    """A row flagged for human review passes only when the record names
    the reviewer; an anonymous pass is still a gap."""

    def test_missing_reviewer_is_not_verified(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            manifest = write_manifest(root)
            matrix = write_matrix(root, [make_row("family.child-playtest",
                                                  human=True)])
            record = make_record(root, "family.child-playtest", "rec.json")
            code, result, proc = run_gate(manifest, matrix, [record],
                                          root / "evidence")
            self.assertEqual(code, 1, proc.stderr)
            self.assertEqual(result["verdict"], "partial")
            status, reasons = row_status(result, "family.child-playtest")
            self.assertEqual(status, "notVerified")
            self.assertTrue(any("reviewer" in r for r in reasons), reasons)

    def test_named_reviewer_passes(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            manifest = write_manifest(root)
            matrix = write_matrix(root, [make_row("family.child-playtest",
                                                  human=True)])
            session = root / "session-notes.txt"
            session.write_text("playtest notes", encoding="utf-8")
            record = make_record(
                root, "family.child-playtest", "rec.json",
                reviewer="Daniel",
                evidence_files=[{"path": "session-notes.txt",
                                 "sha256": sha256_file(session)}])
            code, result, proc = run_gate(manifest, matrix, [record],
                                          root / "evidence")
            self.assertEqual(code, 0, proc.stderr + json.dumps(result))
            self.assertEqual(result["verdict"], "passed")


class OutcomeSemantics(unittest.TestCase):
    """Outcome vocabulary: a failed observation fails the row; a skipped
    observation is a gap (notVerified), never a pass."""

    def test_failed_observation_fails_row(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            manifest = write_manifest(root)
            matrix = write_matrix(root, [make_row("physical.probe")])
            record = make_record(root, "physical.probe", "rec.json",
                                 outcome="failed")
            code, result, _ = run_gate(manifest, matrix, [record],
                                       root / "evidence")
            self.assertEqual(code, 1)
            self.assertEqual(result["verdict"], "failed")
            status, _ = row_status(result, "physical.probe")
            self.assertEqual(status, "failed")

    def test_skipped_observation_is_not_verified(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            manifest = write_manifest(root)
            matrix = write_matrix(root, [make_row("physical.probe")])
            record = make_record(root, "physical.probe", "rec.json",
                                 outcome="skipped")
            code, result, _ = run_gate(manifest, matrix, [record],
                                       root / "evidence")
            self.assertEqual(code, 1)
            self.assertEqual(result["verdict"], "partial")
            status, _ = row_status(result, "physical.probe")
            self.assertEqual(status, "notVerified")


if __name__ == "__main__":
    unittest.main()
