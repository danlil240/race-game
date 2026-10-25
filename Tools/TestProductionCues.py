"""Offline tests: broken manifests and invalid/silent responses must never ship."""
import importlib.util
import io
import json
from pathlib import Path
import tempfile
import unittest
import wave

ROOT = Path(__file__).resolve().parent


class ProductionCueTests(unittest.TestCase):
    def setUp(self):
        self.assertTrue((ROOT / 'Generate-ProductionCues.py').exists(),
                        'Production generator must exist')
        spec = importlib.util.spec_from_file_location('production_cues', ROOT / 'Generate-ProductionCues.py')
        self.generator = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(self.generator)

    def test_manifest_covers_bilingual_production_inventory(self):
        manifest = self.generator.load_manifest()
        self.assertEqual(14, len(manifest['cues']))
        self.assertIn('rival.intro.snowy-peaks-motorcycle-rival', manifest['cues'])
        self.assertTrue(all(any('\u0590' <= c <= '\u05ff' for c in row['he'])
                            for row in manifest['cues'].values()))

    def test_missing_required_cue_is_rejected(self):
        data = self.generator.load_manifest()
        del data['cues']['world.unlocked']
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / 'bad.json'
            path.write_text(json.dumps(data), encoding='utf-8')
            with self.assertRaises(ValueError):
                self.generator.load_manifest(path)

    def test_silence_and_non_wave_response_are_rejected(self):
        for payload in (b'<html>error</html>', self.wav(bytes(48000))):
            with self.assertRaises(ValueError):
                self.generator.validate_wav(payload)

    def test_real_pcm_payload_is_accepted(self):
        # Deterministic non-silent fixture only; never emitted as game audio.
        self.generator.validate_wav(self.wav(b'\x01\x10' * 24000))

    @staticmethod
    def wav(samples):
        buffer = io.BytesIO()
        with wave.open(buffer, 'wb') as output:
            output.setnchannels(1)
            output.setsampwidth(2)
            output.setframerate(24000)
            output.writeframes(samples)
        return buffer.getvalue()


if __name__ == '__main__':
    unittest.main()
