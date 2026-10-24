import copy
import hashlib
import json
import re
from pathlib import Path
import tempfile
import unittest

from ProductionRelease import validate_config, validate_packages


class ProductionReleaseTests(unittest.TestCase):
    def test_packager_protocol_matches_runtime(self):
        repo = Path(__file__).resolve().parent.parent
        runtime = (repo / "Assets/FamilyRacing/Core/Networking/NetworkSettings.cs").read_text(encoding="utf-8-sig")
        packager = (repo / "Tools/Build-FamilyRelease.ps1").read_text(encoding="utf-8-sig")
        actual = re.search(r"LanProtocolVersion\s*=\s*(\d+)", runtime).group(1)
        recorded = re.search(r"\$ExpectedLanProtocol\s*=\s*(\d+)", packager).group(1)
        self.assertEqual(actual, recorded, "Release manifest must describe the shipped network protocol")

    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.config = {"schemaVersion": 1, "versionName": "1.2.3", "androidVersionCode": 4,
                       "previousAndroidVersionCode": 3, "expectedCertificateSha256": "b" * 64}
        self.commit = "a" * 40
        self.manifest = {"schemaVersion": 1, "packageKind": "release", "contentStage": "complete",
                         "source": {"cleanSource": True, "commit": self.commit},
                         "identity": {"versionName": "1.2.3", "androidVersionCode": 4},
                         "windows": {"package": "FamilyRacing-1.2.3-Windows-x64.zip"},
                         "packages": {"android": {"package": "FamilyRacing-1.2.3-Android.apk", "signed": True,
                                                  "signer": "release", "certificateSha256": "b" * 64}}, "files": []}
        for name in ("FamilyRacing-1.2.3-Windows-x64.zip", "FamilyRacing-1.2.3-Android.apk",
                     "THIRD-PARTY-NOTICES.txt", "INSTALL-GUIDE.txt"):
            data = name.encode()
            (self.root / name).write_bytes(data)
            self.manifest["files"].append({"path": name, "sizeBytes": len(data),
                                           "sha256": hashlib.sha256(data).hexdigest()})
        self.save()

    def save(self):
        (self.root / "release-manifest.json").write_text(json.dumps(self.manifest), encoding="utf-8")

    def validate(self):
        return validate_packages(self.config, "v1.2.3", self.root, self.commit)

    def test_accepts_complete_same_commit_signed_production_package(self):
        self.assertEqual(5, len(self.validate()))

    def test_tag_and_android_progression_must_match(self):
        with self.assertRaises(ValueError):
            validate_config(self.config, "v1.2.4")
        for tag in ("1.2.3", "v1.2.3-beta", "v01.2.3"):
            with self.subTest(tag=tag), self.assertRaises(ValueError):
                validate_config(self.config, tag)
        config = copy.deepcopy(self.config)
        config["previousAndroidVersionCode"] = 4
        with self.assertRaises(ValueError):
            validate_config(config, "v1.2.3")

    def test_rejects_rehearsal_partial_dirty_wrong_commit_and_unsigned(self):
        mutations = [(("packageKind",), "rehearsal"), (("contentStage",), "partial"),
                     (("source", "cleanSource"), False), (("source", "commit"), "c" * 40),
                     (("packages", "android", "signed"), False),
                     (("packages", "android", "certificateSha256"), "c" * 64),
                     (("identity", "versionName"), "1.2.2")]
        original = copy.deepcopy(self.manifest)
        for path, value in mutations:
            with self.subTest(path=path):
                self.manifest = copy.deepcopy(original)
                target = self.manifest
                for part in path[:-1]:
                    target = target[part]
                target[path[-1]] = value
                self.save()
                with self.assertRaises(ValueError):
                    self.validate()

    def test_rejects_missing_tampered_and_unlisted_packages(self):
        package = self.root / "FamilyRacing-1.2.3-Android.apk"
        package.write_bytes(b"tampered")
        with self.assertRaises(ValueError):
            self.validate()
        package.unlink()
        with self.assertRaises(ValueError):
            self.validate()
        self.manifest["files"] = self.manifest["files"][:-1]
        self.save()
        with self.assertRaises(ValueError):
            self.validate()

    def test_rejects_traversal_and_duplicate_manifest_entries(self):
        self.manifest["files"][0]["path"] = "../outside.zip"
        self.save()
        with self.assertRaises(ValueError):
            self.validate()
        self.manifest["files"].append(self.manifest["files"][1])
        self.save()
        with self.assertRaises(ValueError):
            self.validate()


if __name__ == "__main__":
    unittest.main()
