"""Check tracked text files for personal home paths and likely credentials.

Run with: python Tools/CheckRepositoryPrivacy.py
Only file names and line numbers are printed; matched values stay private.
This checks the working tree, not past commits or binary file contents.
"""
import pathlib
import re
import subprocess
import sys

RULES = {
    "personal home path": re.compile(r"(?:[A-Za-z]:[\\/]+Users[\\/]+|/(?:home|Users)/)(?!example-user(?:[\\/]|$)|<|\$)[^\\/\s\"'<>]+[\\/]", re.I),
    "service credential": re.compile(r"\b(?:gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|AKIA[0-9A-Z]{16}|sk-[A-Za-z0-9_-]{25,})\b|-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----"),
    "recorded credential": re.compile(r'"(?:token|password|apiKey|secret)"\s*:\s*"[a-fA-F0-9]{24,}"'),
}


def main():
    root = pathlib.Path(__file__).resolve().parents[1]
    result = subprocess.run(["git", "grep", "-I", "-l", "-z", "-e", "", "--"], cwd=root, capture_output=True, check=False)
    if result.returncode not in (0, 1):
        raise RuntimeError("Cannot list tracked text files")
    findings = 0
    for name in result.stdout.decode().split("\0"):
        if not name:
            continue
        text = (root / name).read_bytes().decode("utf-8", errors="replace")
        for number, line in enumerate(text.splitlines(), 1):
            for label, pattern in RULES.items():
                # Public article slugs can contain an incidental sk- sequence.
                candidate = re.sub(r"https?://[^\s)]+", "", line) if label == "service credential" else line
                if pattern.search(candidate):
                    print(f"{name}:{number}: {label}")
                    findings += 1
    print(f"Privacy scan: {findings} findings")
    return bool(findings)


if __name__ == "__main__":
    sys.exit(main())
