"""Validate a version tag and the existing family production package set before release upload."""
import argparse
import hashlib
import json
from pathlib import Path
import re
import sys


def load_json(path):
    return json.loads(Path(path).read_text(encoding="utf-8-sig"))


def validate_config(config, tag):
    version = config.get("versionName", "")
    if not re.fullmatch(r"(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)", version):
        raise ValueError("Release versionName must be a stable major.minor.patch version")
    if tag != "v" + version:
        raise ValueError("Tag must equal v" + version + " from Release/release.json")
    code, previous = config.get("androidVersionCode"), config.get("previousAndroidVersionCode")
    if type(code) is not int or type(previous) is not int or not 0 <= previous < code <= 2100000000:
        raise ValueError("Android version code must increase above previousAndroidVersionCode")


def digest(path):
    with Path(path).open("rb") as handle:
        return hashlib.file_digest(handle, "sha256").hexdigest()


def validate_packages(config, tag, directory, commit):
    validate_config(config, tag)
    directory = Path(directory)
    manifest = load_json(directory / "release-manifest.json")
    if manifest.get("schemaVersion") != 1 or manifest.get("packageKind") != "release" or manifest.get("contentStage") != "complete":
        raise ValueError("Only complete production release packages may be uploaded")
    source = manifest.get("source", {})
    if source.get("cleanSource") is not True or source.get("commit") != commit:
        raise ValueError("Release must be built from the clean tagged commit")
    identity = manifest.get("identity", {})
    if any(identity.get(key) != config.get(key) for key in ("versionName", "androidVersionCode")):
        raise ValueError("Package identity differs from release config")
    version = config["versionName"]
    windows = "FamilyRacing-" + version + "-Windows-x64.zip"
    android = "FamilyRacing-" + version + "-Android.apk"
    signing = manifest.get("packages", {}).get("android", {})
    if (signing.get("signed") is not True or signing.get("signer") != "release" or
            signing.get("certificateSha256", "").lower() != config.get("expectedCertificateSha256", "").lower() or
            not re.fullmatch("[0-9a-fA-F]{64}", signing.get("certificateSha256", ""))):
        raise ValueError("Android must be signed with the configured production certificate")
    if manifest.get("windows", {}).get("package") != windows or signing.get("package") != android:
        raise ValueError("Manifest must name the versioned Windows and Android packages")
    expected = {windows, android, "THIRD-PARTY-NOTICES.txt", "INSTALL-GUIDE.txt"}
    entries = manifest.get("files", [])
    names = [entry.get("path") for entry in entries]
    if len(names) != len(expected) or set(names) != expected:
        raise ValueError("Manifest must contain exactly the four distribution files without duplicate/unsafe paths")
    for entry in entries:
        path = directory / entry["path"]
        if (not path.is_file() or path.is_symlink() or path.stat().st_size <= 0 or
                path.stat().st_size != entry.get("sizeBytes") or digest(path) != entry.get("sha256")):
            raise ValueError("Missing or changed package: " + entry["path"])
    return sorted(expected | {"release-manifest.json"})


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("command", choices=("preflight", "validate"))
    parser.add_argument("--config", default="Release/release.json")
    parser.add_argument("--tag", required=True)
    parser.add_argument("--packages", type=Path)
    parser.add_argument("--commit")
    args = parser.parse_args()
    try:
        config = load_json(args.config)
        validate_config(config, args.tag)
        if args.command == "validate":
            if not args.packages or not args.commit:
                parser.error("validate requires --packages and --commit")
            names = validate_packages(config, args.tag, args.packages, args.commit)
            checksums = "".join(digest(args.packages / name) + "  " + name + "\n" for name in names)
            (args.packages / "SHA256SUMS.txt").write_text(checksums, encoding="utf-8")
        print("Production release " + args.command + " passed for " + args.tag)
        return 0
    except (OSError, ValueError, TypeError, KeyError) as error:
        print("Production release rejected: " + str(error), file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
