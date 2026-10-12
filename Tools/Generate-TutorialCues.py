"""Offline tutorial voice-cue generator (Azure Speech, authoring-time only).

Reads Tools/tutorial-cues.json and synthesizes one WAV per cue id per
locale into Assets/FamilyRacing/Audio/Tutorial/cue-<id>-<locale>.wav.
Standard library only; the generated clips are committed assets — no
service, credential, or network access exists in the player.

Credentials come from the environment and are never written to disk by
this tool:

    AZURE_SPEECH_KEY      Speech resource key (free-tier F0 suffices)
    AZURE_SPEECH_REGION   Speech resource region, e.g. "eastus"

Usage:  python Tools/Generate-TutorialCues.py [--dry-run]
"""

import json
import os
import sys
import urllib.error
import urllib.request
from pathlib import Path
from xml.sax.saxutils import escape

ROOT = Path(__file__).resolve().parent.parent
MANIFEST_PATH = ROOT / "Tools" / "tutorial-cues.json"
OUTPUT_DIR = ROOT / "Assets" / "FamilyRacing" / "Audio" / "Tutorial"

ENDPOINT = "https://{region}.tts.speech.microsoft.com/cognitiveservices/v1"


def load_manifest(path=MANIFEST_PATH):
    """Parse and validate the cue manifest; raises SystemExit on defects."""
    data = json.loads(path.read_text(encoding="utf-8"))
    cues = data.get("cues")
    voices = data.get("voices")
    locales = data.get("locales")
    fmt = data.get("outputFormat", "riff-24khz-16bit-mono-pcm")
    problems = []
    if not isinstance(cues, dict) or not cues:
        problems.append("'cues' must be a non-empty object")
    else:
        for cue_id, entry in cues.items():
            if not isinstance(entry, dict):
                problems.append(f"cue '{cue_id}' must be an object")
                continue
            for locale in ("en", "he"):
                text = entry.get(locale)
                if not isinstance(text, str) or not text.strip():
                    problems.append(f"cue '{cue_id}' is missing text.{locale}")
    for locale in ("en", "he"):
        if not isinstance(voices, dict) or not voices.get(locale):
            problems.append(f"voices.{locale} is missing")
        if not isinstance(locales, dict) or not locales.get(locale):
            problems.append(f"locales.{locale} is missing")
    if problems:
        for problem in problems:
            print(f"manifest error: {problem}", file=sys.stderr)
        raise SystemExit(2)
    return data


def ssml(text, voice, locale):
    return (
        "<speak version='1.0' xml:lang='{locale}'>"
        "<voice xml:lang='{locale}' name='{voice}'>{text}</voice>"
        "</speak>"
    ).format(locale=escape(locale), voice=escape(voice), text=escape(text))


def synthesize(key, region, fmt, text, voice, locale):
    """POST one SSML document; returns the response body (WAV bytes)."""
    request = urllib.request.Request(
        ENDPOINT.format(region=region),
        data=ssml(text, voice, locale).encode("utf-8"),
        headers={
            "Ocp-Apim-Subscription-Key": key,
            "Content-Type": "application/ssml+xml; charset=utf-8",
            "X-Microsoft-OutputFormat": fmt,
            "User-Agent": "family-racing-tutorial-cues",
        },
    )
    try:
        with urllib.request.urlopen(request, timeout=30) as response:
            return response.read()
    except urllib.error.HTTPError as error:
        detail = error.read().decode("utf-8", errors="replace")
        raise RuntimeError(
            f"Azure TTS HTTP {error.code}: {detail[:200]}") from None


def main():
    dry_run = "--dry-run" in sys.argv[1:]
    manifest = load_manifest()
    cues = manifest["cues"]
    voices = manifest["voices"]
    locales = manifest["locales"]
    fmt = manifest.get("outputFormat", "riff-24khz-16bit-mono-pcm")

    key = os.environ.get("AZURE_SPEECH_KEY", "")
    region = os.environ.get("AZURE_SPEECH_REGION", "")
    if not dry_run and (not key or not region):
        print(
            "AZURE_SPEECH_KEY and AZURE_SPEECH_REGION must be set to the "
            "Speech resource credentials (free tier F0 is sufficient).",
            file=sys.stderr)
        raise SystemExit(2)

    OUTPUT_DIR.mkdir(parents=True, exist_ok=True)
    failures = 0
    for cue_id in sorted(cues):
        for locale in ("en", "he"):
            target = OUTPUT_DIR / f"cue-{cue_id}-{locale}.wav"
            if dry_run:
                print(f"[dry-run] {target.name}: "
                      f"{cue_id}/{locale} via {voices[locale]}")
                continue
            try:
                wav = synthesize(key, region, fmt, cues[cue_id][locale],
                                 voices[locale], locales[locale])
            except (RuntimeError, urllib.error.URLError) as error:
                failures += 1
                print(f"FAIL {target.name}: {error}", file=sys.stderr)
                continue
            target.write_bytes(wav)
            print(f"OK   {target.name}: {len(wav)} bytes")
    if failures:
        print(f"{failures} clip(s) failed", file=sys.stderr)
        raise SystemExit(1)
    print("All tutorial cues generated." if not dry_run
          else "Dry run complete.")


if __name__ == "__main__":
    main()
