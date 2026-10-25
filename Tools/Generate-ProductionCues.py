"""Generate genuine bilingual campaign WAVs with Azure Speech, at authoring time.

Usage: python Tools/Generate-ProductionCues.py [--dry-run]
Requires AZURE_SPEECH_KEY and AZURE_SPEECH_REGION in the environment.
No credentials or runtime speech dependencies are bundled in the game.
"""
import argparse
import importlib.util
import io
import json
import os
from pathlib import Path
import re
import sys
import urllib.error
import wave

ROOT = Path(__file__).resolve().parent.parent
MANIFEST_PATH = ROOT / 'Tools/production-voice-cues.json'
OUTPUT_DIR = ROOT / 'Assets/FamilyRacing/Audio/Production'
WORLDS = ('sunny-island', 'redrock-canyon', 'snowy-peaks')
REQUIRED = ({f'world.intro.{world}' for world in WORLDS}
            | {f'rival.intro.{world}-{kind}-rival' for world in WORLDS for kind in ('car', 'motorcycle')}
            | {'objective.completion', 'objective.introductory', 'objective.mastery', 'world.unlocked', 'adventure.complete'})


def load_manifest(path=MANIFEST_PATH):
    data = json.loads(Path(path).read_text(encoding='utf-8'))
    if set(data.get('cues', {})) != REQUIRED:
        raise ValueError('Manifest must contain exactly the 14 required campaign cues')
    for language in ('en', 'he'):
        for field in ('voices', 'locales'):
            if not data.get(field, {}).get(language):
                raise ValueError(f'Missing {field}.{language}')
        for cue, row in data['cues'].items():
            text = row.get(language, '')
            if not isinstance(text, str) or not text.strip():
                raise ValueError(f'Missing {cue}/{language} text')
    if data.get('outputFormat') != 'riff-24khz-16bit-mono-pcm':
        raise ValueError('Production clips require 24kHz mono 16-bit PCM WAV')
    return data


def validate_wav(payload):
    try:
        with wave.open(io.BytesIO(payload), 'rb') as clip:
            if (clip.getnchannels(), clip.getsampwidth(), clip.getframerate()) != (1, 2, 24000):
                raise ValueError('Unexpected WAV format')
            if clip.getnframes() < 6000:
                raise ValueError('Speech clip is shorter than 250ms')
            samples = clip.readframes(clip.getnframes())
            if len(samples) != clip.getnframes() * 2 or not any(samples):
                raise ValueError('Truncated or silent WAV rejected')
    except (wave.Error, EOFError) as error:
        raise ValueError('Invalid WAV response') from error


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dry-run', action='store_true')
    options = parser.parse_args()
    manifest = load_manifest()
    if not options.dry_run:
        key = os.environ.get('AZURE_SPEECH_KEY', '')
        region = os.environ.get('AZURE_SPEECH_REGION', '')
        if not key or not region:
            parser.error('Set AZURE_SPEECH_KEY and AZURE_SPEECH_REGION, or supply licensed English/Hebrew recordings')
        if not re.fullmatch('[a-z0-9-]+', region):
            parser.error('AZURE_SPEECH_REGION must be a region identifier')
        # Reuse the existing authenticated Azure REST implementation.
        spec = importlib.util.spec_from_file_location('tutorial_cues', ROOT / 'Tools/Generate-TutorialCues.py')
        azure = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(azure)
        OUTPUT_DIR.mkdir(parents=True, exist_ok=True)
    failures = 0
    for cue in sorted(manifest['cues']):
        for language in ('en', 'he'):
            target = OUTPUT_DIR / f'cue-{cue}-{language}.wav'
            if options.dry_run:
                print(f'[dry-run] {target.name} via {manifest["voices"][language]}')
                continue
            try:
                payload = azure.synthesize(key, region, manifest['outputFormat'],
                                           manifest['cues'][cue][language], manifest['voices'][language],
                                           manifest['locales'][language])
                validate_wav(payload)
                temporary = target.with_suffix('.wav.tmp')
                temporary.write_bytes(payload)
                temporary.replace(target)
                print(f'OK {target.name}: {len(payload)} bytes')
            except (RuntimeError, urllib.error.URLError, ValueError, OSError):
                # Do not print service responses or exceptions which could contain credentials.
                failures += 1
                print(f'FAIL {target.name}: provider, audio validation, or file write failed', file=sys.stderr)
    return 1 if failures else 0


if __name__ == '__main__':
    sys.exit(main())
