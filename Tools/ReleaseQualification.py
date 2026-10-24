"""M7 release evidence gate (Task 5).

Reconciles the required-row inventory (``Release/acceptance-matrix.json``)
against immutable evidence records for one candidate described by a
``release-manifest.json`` (schema 1), then writes a verdict.

    python Tools/ReleaseQualification.py \
        --manifest <release-manifest.json> \
        --matrix <acceptance-matrix.json> \
        --evidence <new-directory> \
        --records <record-or-directory> [<record-or-directory> ...]

Writes ``<evidence>/result.json`` and exits:

- 0 — every required row passed (verdict ``passed``);
- 1 — any required row failed or lacks qualifying evidence
  (verdict ``failed`` / ``partial``);
- 2 — usage or input I/O error (no result written).

The gate has no success override: a row passes only when one admissible
record with outcome ``passed`` satisfies every binding the row declares.
It never edits the candidate, the matrix, or any record, and it never
fills absent evidence with success.

Row fields (matrix schema 1)
----------------------------
id                  unique required row id
required            optional rows report but never block (default true)
evidenceClass       ``production`` — only production-class records count;
                    ``fixture`` — tooling-level row, fixture records allowed
buildKind           ``ordinary`` | ``development`` | ``any`` — the artifact
                    kind the row must be observed on; on a production row
                    ``any`` admits only ``ordinary``/``development``
                    records — ``fixture`` is never production evidence
artifact            ``android`` | ``windows`` | ``none`` — the manifest
                    package the record must bind by path + SHA-256
requiresHumanReview record must name ``humanReviewer``
physical            record must identify ``device.label``

Evidence record fields (schema 1)
---------------------------------
rowId, outcome (``passed``|``failed``|``skipped``|``notVerified``),
evidenceClass (``production``|``fixture``),
buildKind (``ordinary``|``development``|``fixture``),
source {commit, contentVersion}, method, observedUtc, plus optional
artifact {path, sha256}, device {label, os, configuration},
humanReviewer, evidenceFiles [{path, sha256}], label, notes.

Admission rules (in order; the first decisive problem settles the row):

1. A malformed record, a hash that contradicts the manifest, or a
   missing/tampered evidence file FAILS the row — integrity violations are
   never softened into gaps.
2. A record that is inadmissible for the row (fixture class on a
   production row, a fixture build kind on a production row — even one
   whose buildKind is ``any`` — a wrong build kind, source commit or
   content identity that is not this candidate's, missing required
   reviewer or device) leaves the row ``notVerified`` — evidence exists
   but does not qualify.
3. Otherwise the record's own outcome applies: ``passed`` passes,
   ``failed`` fails, ``skipped``/``notVerified`` stay open.

Records naming an unknown row id, or a second record for the same row,
are run-level integrity failures — the verdict is ``failed`` regardless.
"""

import argparse
import hashlib
import json
import sys
from datetime import datetime, timezone
from pathlib import Path

SCHEMA_VERSION = 1
EVIDENCE_CLASSES = ("production", "fixture")
ROW_BUILD_KINDS = ("ordinary", "development", "any")
RECORD_BUILD_KINDS = ("ordinary", "development", "fixture")
ROW_ARTIFACTS = ("android", "windows", "none")
OUTCOMES = ("passed", "failed", "skipped", "notVerified")
HEX64 = set("0123456789abcdef")


def sha256_file(path):
    digest = hashlib.sha256()
    with open(path, "rb") as handle:
        for chunk in iter(lambda: handle.read(1 << 20), b""):
            digest.update(chunk)
    return digest.hexdigest()


def is_sha256(value):
    return isinstance(value, str) and len(value) == 64 \
        and all(c in HEX64 for c in value.lower())


def load_json(path):
    with open(path, "r", encoding="utf-8-sig") as handle:
        return json.load(handle)


def utc_now():
    return datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%S.%fZ")


class InputProblem(Exception):
    """A required input could not be read at all (usage-level error)."""


# ---------------------------------------------------------------------------
# Manifest
# ---------------------------------------------------------------------------

def load_manifest(path):
    """Returns (manifest_info, errors). manifest_info carries the artifact
    and identity fields the reconciler binds records against."""
    errors = []
    try:
        manifest = load_json(path)
    except Exception as exc:
        return None, ["manifest is not readable JSON: %s" % exc]
    if not isinstance(manifest, dict):
        return None, ["manifest root is not a JSON object"]
    if manifest.get("schemaVersion") != SCHEMA_VERSION:
        errors.append("manifest schemaVersion must be 1, got %r"
                      % manifest.get("schemaVersion"))

    identity = manifest.get("identity") or {}
    code = identity.get("androidVersionCode")
    previous = identity.get("previousAndroidVersionCode")
    if not isinstance(code, int) or code <= 0:
        errors.append("manifest identity.androidVersionCode must be a "
                      "positive integer, got %r" % code)
    if not isinstance(previous, int) or previous < 0:
        errors.append("manifest identity.previousAndroidVersionCode must be "
                      "a nonnegative integer, got %r" % previous)
    elif isinstance(code, int) and code <= previous:
        # A release candidate whose code is not strictly above its declared
        # predecessor can never install as an update; the pipeline must
        # refuse it here as well as in FamilyReleaseSettings.Validate.
        errors.append("manifest identity carries a non-increasing version "
                      "code: androidVersionCode %r is not strictly above "
                      "previousAndroidVersionCode %r" % (code, previous))

    source = manifest.get("source") or {}
    commit = source.get("commit")
    if not isinstance(commit, str) or not commit:
        errors.append("manifest source.commit is missing")
    gameplay = manifest.get("gameplay") or {}
    content_version = gameplay.get("contentVersion")

    files = manifest.get("files")
    if not isinstance(files, list):
        errors.append("manifest files must be a list")
        files = []
    file_hashes = {}
    manifest_dir = Path(path).resolve().parent
    for entry in files:
        if not isinstance(entry, dict) or not isinstance(entry.get("path"), str) \
                or not is_sha256(entry.get("sha256")):
            errors.append("manifest files[] entry lacks a valid "
                          "path/sha256: %r" % (entry,))
            continue
        file_hashes[entry["path"]] = entry["sha256"]

    # Every file that exists next to the manifest must hash as recorded;
    # an existing-but-different file means the manifest is stale or the
    # package was touched after assembly. Absent files are reported, not
    # failed: the evidence run may see only the manifest.
    absent_files = []
    for rel, expected in file_hashes.items():
        candidate = manifest_dir / rel
        if not candidate.is_file():
            absent_files.append(rel)
        elif sha256_file(candidate) != expected:
            errors.append("manifest file present but its SHA-256 "
                          "contradicts the manifest: %s" % rel)

    artifacts = {"android": None, "windows": None}
    android = (manifest.get("packages") or {}).get("android") or {}
    if android.get("package") and is_sha256(android.get("sha256")):
        artifacts["android"] = {"path": android["package"],
                                "sha256": android["sha256"].lower()}
    windows = manifest.get("windows") or {}
    if windows.get("package") and is_sha256(windows.get("sha256")):
        artifacts["windows"] = {"path": windows["package"],
                                "sha256": windows["sha256"].lower()}

    info = {
        "path": str(path),
        "sha256": sha256_file(path),
        "commit": commit,
        "contentVersion": content_version,
        "contentStage": manifest.get("contentStage"),
        "acceptanceStatus": manifest.get("acceptanceStatus"),
        "artifacts": artifacts,
        "fileHashes": file_hashes,
        "absentFiles": absent_files,
    }
    return info, errors


# ---------------------------------------------------------------------------
# Matrix
# ---------------------------------------------------------------------------

def load_matrix(path):
    """Returns (rows, errors, info). Each row is normalized with defaults."""
    info = {"path": str(path), "sha256": None, "matrixId": None}
    try:
        info["sha256"] = sha256_file(path)
    except OSError:
        pass
    try:
        matrix = load_json(path)
    except Exception as exc:
        return [], ["matrix is not readable JSON: %s" % exc], info
    errors = []
    if not isinstance(matrix, dict):
        return [], ["matrix root is not a JSON object"], info
    info["matrixId"] = matrix.get("matrixId")
    if matrix.get("schemaVersion") != SCHEMA_VERSION:
        errors.append("matrix schemaVersion must be 1, got %r"
                      % matrix.get("schemaVersion"))
    raw_rows = matrix.get("rows")
    if not isinstance(raw_rows, list):
        return [], ["matrix rows must be a list"], info

    rows = []
    seen = set()
    for index, raw in enumerate(raw_rows):
        where = "matrix row %d" % index
        if not isinstance(raw, dict) or not isinstance(raw.get("id"), str) \
                or not raw["id"]:
            errors.append(where + " lacks a string id")
            continue
        row_id = raw["id"]
        if row_id in seen:
            errors.append("duplicate matrix row id: " + row_id)
            continue
        seen.add(row_id)
        evidence_class = raw.get("evidenceClass", "production")
        build_kind = raw.get("buildKind", "ordinary")
        artifact = raw.get("artifact", "none")
        if evidence_class not in EVIDENCE_CLASSES:
            errors.append("%s: bad evidenceClass %r" % (row_id, evidence_class))
            continue
        if build_kind not in ROW_BUILD_KINDS:
            errors.append("%s: bad buildKind %r" % (row_id, build_kind))
            continue
        if artifact not in ROW_ARTIFACTS:
            errors.append("%s: bad artifact %r" % (row_id, artifact))
            continue
        rows.append({
            "id": row_id,
            "required": raw.get("required", True) is not False,
            "evidenceClass": evidence_class,
            "buildKind": build_kind,
            "artifact": artifact,
            "requiresHumanReview": raw.get("requiresHumanReview", False) is True,
            "physical": raw.get("physical", False) is True,
            "description": raw.get("description", ""),
            "source": raw.get("source", ""),
        })
    return rows, errors, info


# ---------------------------------------------------------------------------
# Evidence records
# ---------------------------------------------------------------------------

def expand_record_paths(paths):
    files = []
    for raw in paths:
        path = Path(raw)
        if path.is_dir():
            files.extend(sorted(path.glob("*.json")))
        else:
            files.append(path)
    return files


def load_records(paths):
    """Returns (records, integrity_failures). A record is
    {path, sha256, rowId, problems:[...], data}."""
    records = []
    failures = []
    for path in expand_record_paths(paths):
        entry = {"path": str(path), "sha256": None, "rowId": None,
                 "problems": [], "data": None}
        try:
            entry["sha256"] = sha256_file(path)
        except OSError as exc:
            failures.append("record unreadable: %s (%s)" % (path, exc))
            records.append(entry)
            continue
        try:
            data = load_json(path)
        except Exception as exc:
            failures.append("record is not readable JSON: %s (%s)"
                            % (path, exc))
            records.append(entry)
            continue
        entry["data"] = data
        problems = entry["problems"]
        if not isinstance(data, dict):
            problems.append("record root is not a JSON object")
        else:
            if data.get("schemaVersion") not in (None, SCHEMA_VERSION):
                problems.append("unsupported record schemaVersion %r"
                                % data.get("schemaVersion"))
            row_id = data.get("rowId")
            if not isinstance(row_id, str) or not row_id:
                problems.append("record lacks a nonempty rowId")
            else:
                entry["rowId"] = row_id
            if data.get("outcome") not in OUTCOMES:
                problems.append("bad outcome %r" % data.get("outcome"))
            if data.get("evidenceClass") not in EVIDENCE_CLASSES:
                problems.append("bad evidenceClass %r"
                                % data.get("evidenceClass"))
            if data.get("buildKind") not in RECORD_BUILD_KINDS:
                problems.append("bad buildKind %r" % data.get("buildKind"))
            if not isinstance(data.get("method"), str) \
                    or not data.get("method"):
                problems.append("record lacks a method description")
            if not isinstance(data.get("observedUtc"), str) \
                    or not data.get("observedUtc"):
                problems.append("record lacks observedUtc")
            artifact = data.get("artifact")
            if artifact is not None:
                if not isinstance(artifact, dict) \
                        or not isinstance(artifact.get("path"), str) \
                        or not is_sha256(artifact.get("sha256")):
                    problems.append("artifact must carry path + 64-hex sha256")
            files = data.get("evidenceFiles")
            if files is not None:
                if not isinstance(files, list) or any(
                        not isinstance(f, dict)
                        or not isinstance(f.get("path"), str)
                        or not is_sha256(f.get("sha256")) for f in files):
                    problems.append("evidenceFiles entries must carry path "
                                    "+ 64-hex sha256")
        if problems:
            failures.append("record malformed: %s (%s)"
                            % (path, "; ".join(problems)))
        records.append(entry)
    return records, failures


def resolve_evidence_path(record_path, rel):
    """Evidence file paths resolve relative to the record's directory;
    absolute paths are honored as written."""
    candidate = Path(rel)
    if candidate.is_absolute():
        return candidate
    return Path(record_path).resolve().parent / rel


# ---------------------------------------------------------------------------
# Row evaluation
# ---------------------------------------------------------------------------

def evaluate_row(row, record, manifest):
    """Returns (status, reasons). status in {passed, failed, notVerified}."""
    if record is None:
        return "notVerified", ["no evidence record submitted"]
    if record["data"] is None or record["problems"]:
        reasons = ["record malformed or unreadable: %s"
                   % (record["path"],)]
        reasons.extend(record["problems"])
        return "failed", reasons
    data = record["data"]
    reasons = []

    # --- Integrity: artifact binding --------------------------------------
    artifact = data.get("artifact")
    if row["artifact"] != "none":
        expected = manifest["artifacts"].get(row["artifact"])
        if artifact is None:
            reasons.append("row requires the %s package; record binds no "
                           "artifact" % row["artifact"])
            return "notVerified", reasons
        if expected is None:
            reasons.append("manifest carries no %s artifact; an artifact-bound "
                           "row cannot be verified" % row["artifact"])
            return "notVerified", reasons
        if artifact["path"] != expected["path"]:
            return "failed", ["record artifact path %r is not the candidate's "
                              "%s package %r"
                              % (artifact["path"], row["artifact"],
                                 expected["path"])]
        if artifact["sha256"].lower() != expected["sha256"]:
            return "failed", ["record measured sha256 %s does not match the "
                              "manifest's %s package hash %s"
                              % (artifact["sha256"], row["artifact"],
                                 expected["sha256"])]
    elif artifact is not None:
        if data.get("evidenceClass") == "fixture":
            # Fixture artifacts are not candidate files; the hash must match
            # the actual fixture bytes on disk.
            fixture_path = resolve_evidence_path(record["path"],
                                                 artifact["path"])
            if not fixture_path.is_file():
                return "failed", ["fixture artifact missing: %s"
                                  % artifact["path"]]
            if sha256_file(fixture_path) != artifact["sha256"].lower():
                return "failed", ["fixture artifact sha256 does not match "
                                  "the file on disk: %s" % artifact["path"]]
        else:
            known = dict(manifest["fileHashes"])
            for entry in manifest["artifacts"].values():
                if entry is not None:
                    known.setdefault(entry["path"], entry["sha256"])
            expected = known.get(artifact["path"])
            if expected is None:
                reasons.append("record artifact %r is not part of this "
                               "candidate's manifest" % artifact["path"])
                return "notVerified", reasons
            if artifact["sha256"].lower() != expected:
                return "failed", ["record measured sha256 %s does not match "
                                  "the manifest hash %s for %s"
                                  % (artifact["sha256"], expected,
                                     artifact["path"])]

    # --- Integrity: referenced evidence files ------------------------------
    for ref in data.get("evidenceFiles") or []:
        evidence_path = resolve_evidence_path(record["path"], ref["path"])
        if not evidence_path.is_file():
            return "failed", ["evidence file missing: %s (record %s)"
                              % (ref["path"], record["path"])]
        if sha256_file(evidence_path) != ref["sha256"].lower():
            return "failed", ["evidence file %s hash does not match the "
                              "record (tampered or stale evidence)"
                              % ref["path"]]

    # --- Admission: the record must qualify for this row's class ----------
    if row["evidenceClass"] == "production" \
            and data.get("evidenceClass") != "production":
        reasons.append("record evidenceClass %r cannot satisfy a production "
                       "row; fixture/rehearsal evidence never qualifies a "
                       "release row" % data.get("evidenceClass"))
    if row["buildKind"] == "any":
        # "any" is not a wildcard for fixture observations on production
        # rows: it admits only real build kinds (ordinary/development).
        # A fixture-build record on a production row is inadmissible no
        # matter what evidenceClass it claims.
        if row["evidenceClass"] == "production" \
                and data.get("buildKind") == "fixture":
            reasons.append("record buildKind 'fixture' cannot satisfy a "
                           "production row; 'any' admits only ordinary or "
                           "development builds")
    elif data.get("buildKind") != row["buildKind"]:
        reasons.append("record buildKind %r cannot satisfy a row requiring "
                       "%r packages" % (data.get("buildKind"),
                                        row["buildKind"]))
    if row["evidenceClass"] == "production":
        source = data.get("source") or {}
        if source.get("commit") != manifest["commit"]:
            reasons.append("record source commit %r is not this candidate's "
                           "commit %r" % (source.get("commit"),
                                          manifest["commit"]))
        # The contract requires every record to name its source/content
        # identity: when the candidate declares a contentVersion, a
        # production record must declare it too and it must match.
        content = source.get("contentVersion")
        if manifest["contentVersion"] is not None:
            if not isinstance(content, str) or not content:
                reasons.append("record declares no source.contentVersion; "
                               "the candidate's content identity %r must be "
                               "named" % manifest["contentVersion"])
            elif content != manifest["contentVersion"]:
                reasons.append("record contentVersion %r is not this "
                               "candidate's %r"
                               % (content, manifest["contentVersion"]))
    if row["requiresHumanReview"]:
        reviewer = data.get("humanReviewer")
        if not isinstance(reviewer, str) or not reviewer.strip():
            reasons.append("row requires a named human reviewer; record "
                           "carries none")
    if row["physical"]:
        device = data.get("device") or {}
        missing = [field for field in ("label", "os", "configuration")
                   if not isinstance(device.get(field), str)
                   or not device[field].strip()]
        if missing:
            reasons.append("physical row requires device/OS/configuration "
                           "identification; record device lacks %s"
                           % ", ".join("device." + f for f in missing))
    if data.get("outcome") == "passed" and not data.get("evidenceFiles"):
        reasons.append("a passed claim must name at least one evidence "
                       "file (evidenceFiles); a bare pass is not evidence")
    if reasons:
        return "notVerified", reasons

    # --- The record's own outcome ------------------------------------------
    outcome = data["outcome"]
    if outcome == "passed":
        detail = data.get("notes") or "recorded pass"
        return "passed", [detail] if detail else ["recorded pass"]
    if outcome == "failed":
        return "failed", ["recorded failure: %s"
                          % (data.get("notes") or "no detail")]
    return "notVerified", ["record outcome %r leaves the row open"
                           % outcome]


# ---------------------------------------------------------------------------
# Gate
# ---------------------------------------------------------------------------

def run(manifest_path, matrix_path, evidence_dir, record_paths):
    """Core gate; returns (exit_code, result_dict). result_dict is None when
    the run cannot even write a result (usage-level problems are raised
    before this is called)."""
    manifest, manifest_errors = load_manifest(manifest_path)
    rows, matrix_errors, matrix_info = load_matrix(matrix_path)
    records, integrity = load_records(record_paths)

    result_rows = []
    not_verified = []
    failed = []
    passed_count = 0
    optional_open = []

    if manifest is not None and not matrix_errors and not manifest_errors:
        row_index = {row["id"]: row for row in rows}
        by_row = {}
        duplicated = set()
        for record in records:
            row_id = record["rowId"]
            if row_id is None:
                continue  # already an integrity failure (malformed)
            if row_id not in row_index:
                integrity.append("record %s names unknown row id %r"
                                 % (record["path"], row_id))
                continue
            if row_id in by_row:
                integrity.append("duplicate records for row %r: %s and %s"
                                 % (row_id, by_row[row_id]["path"],
                                    record["path"]))
                duplicated.add(row_id)
                continue
            by_row[row_id] = record

        for row in rows:
            if row["id"] in duplicated:
                status, reasons = "failed", [
                    "multiple evidence records claim this row; a row must "
                    "have exactly one record"]
            else:
                status, reasons = evaluate_row(row, by_row.get(row["id"]),
                                               manifest)
            result_rows.append({"id": row["id"], "required": row["required"],
                                "status": status, "reasons": reasons})
            if status == "passed":
                passed_count += 1
            elif status == "failed":
                failed.append(row["id"])
            elif row["required"]:
                not_verified.append(row["id"])
            else:
                optional_open.append(row["id"])

    record_report = [{"path": r["path"], "sha256": r["sha256"],
                      "rowId": r["rowId"]} for r in records]
    if manifest_errors or matrix_errors or integrity or failed:
        verdict = "failed"
    elif not_verified:
        verdict = "partial"
    else:
        verdict = "passed"

    result = {
        "schemaVersion": SCHEMA_VERSION,
        "generatedUtc": utc_now(),
        "verdict": verdict,
        "manifest": None if manifest is None else {
            "path": manifest["path"], "sha256": manifest["sha256"],
            "sourceCommit": manifest["commit"],
            "contentVersion": manifest["contentVersion"],
            "contentStage": manifest["contentStage"],
            "acceptanceStatus": manifest["acceptanceStatus"],
            "absentPackageFiles": manifest["absentFiles"],
        },
        "matrix": matrix_info,
        "records": record_report,
        "summary": {
            "rows": len(result_rows),
            "passed": passed_count,
            "failed": len(failed),
            "notVerified": len(not_verified),
            "optionalOpen": len(optional_open),
            "integrityFailures": len(integrity) + len(manifest_errors)
            + len(matrix_errors),
        },
        "rows": result_rows,
        "notVerified": not_verified,
        "failed": failed,
        "optionalOpen": optional_open,
        "manifestProblems": manifest_errors,
        "matrixProblems": matrix_errors,
        "integrityFailures": integrity,
    }
    out = Path(evidence_dir) / "result.json"
    out.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    return (0 if verdict == "passed" else 1), result


def main(argv=None):
    parser = argparse.ArgumentParser(
        description="Reconcile the release acceptance matrix against "
                    "evidence records for one candidate manifest.")
    parser.add_argument("--manifest", required=True,
                        help="release-manifest.json (schema 1)")
    parser.add_argument("--matrix", required=True,
                        help="acceptance-matrix.json (schema 1)")
    parser.add_argument("--evidence", required=True,
                        help="NEW output directory; result.json is written "
                             "inside it")
    parser.add_argument("--records", nargs="*", default=[],
                        help="evidence record .json files or directories "
                             "containing them")
    args = parser.parse_args(argv)

    evidence_dir = Path(args.evidence)
    if evidence_dir.exists():
        print("error: --evidence directory must be new: %s" % evidence_dir,
              file=sys.stderr)
        return 2
    for label, raw in (("manifest", args.manifest), ("matrix", args.matrix)):
        if not Path(raw).is_file():
            print("error: --%s missing: %s" % (label, raw), file=sys.stderr)
            return 2
    for raw in expand_record_paths(args.records):
        if not raw.is_file():
            print("error: record file missing: %s" % raw, file=sys.stderr)
            return 2

    evidence_dir.mkdir(parents=True)
    code, result = run(args.manifest, args.matrix, evidence_dir, args.records)
    summary = result["summary"]
    print("ReleaseQualification: verdict=%s rows=%d passed=%d failed=%d "
          "notVerified=%d integrityFailures=%d"
          % (result["verdict"], summary["rows"], summary["passed"],
             summary["failed"], summary["notVerified"],
             summary["integrityFailures"]))
    for problem in result["manifestProblems"]:
        print("  manifest: %s" % problem)
    for problem in result["matrixProblems"]:
        print("  matrix: %s" % problem)
    for failure in result["integrityFailures"]:
        print("  integrity: %s" % failure)
    if result["verdict"] != "passed":
        for row in result["rows"]:
            if row["status"] != "passed":
                print("  %s %-26s %s" % (row["status"], row["id"],
                                         "; ".join(row["reasons"])))
    print("result: %s" % (Path(args.evidence) / "result.json"))
    return code


if __name__ == "__main__":
    sys.exit(main())
