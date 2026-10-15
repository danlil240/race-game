"""Offline race-audio generator (M4 Task 5): synthesizes the bundled
engine/skid/boost loops, one-shot cues and music beds for the visual
benchmark into Assets/FamilyRacing/Audio/VisualBenchmark/.

Pure standard library, fully deterministic, no services or credentials
— the generated WAVs are committed project-owned assets (US$0). Loops
are rendered over a whole number of waveform periods (or boundary
crossfaded) so AudioSource.loop cycles click-free.

Usage:  python Tools/Generate-RaceAudio.py [--dry-run]
"""

import math
import struct
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
OUTPUT_DIR = ROOT / "Assets" / "FamilyRacing" / "Audio" / "VisualBenchmark"
RATE = 22050


def write_wav(path, samples):
    """Write mono 16-bit PCM WAV."""
    frames = bytearray()
    for s in samples:
        s = max(-1.0, min(1.0, s))
        frames += struct.pack("<h", int(s * 32767))
    header = (
        b"RIFF" + struct.pack("<I", 36 + len(frames)) + b"WAVE"
        + b"fmt " + struct.pack("<IHHIIHH", 16, 1, 1, RATE,
                                 RATE * 2, 2, 16)
        + b"data" + struct.pack("<I", len(frames))
    )
    path.write_bytes(header + frames)


def loop_samples(cycles, freq):
    """Sample count for `cycles` whole periods of `freq` — seamless."""
    period = RATE / freq
    return int(round(cycles * period))


def tone(freq, t):
    return math.sin(2 * math.pi * freq * t)


def norm(samples, peak=0.9):
    m = max((abs(s) for s in samples), default=1.0)
    if m <= 0:
        return samples
    gain = peak / m
    return [s * gain for s in samples]


def fade_edges(samples, n=256):
    """Short linear fade at both ends — kills the loop/entry click."""
    out = list(samples)
    for i in range(min(n, len(out) // 2)):
        g = i / n
        out[i] *= g
        out[-1 - i] *= g
    return out


def engine_loop(freq, roughness, bright):
    """Loopable engine hum: fundamental + harmonics + soft AM, rendered
    over a whole number of fundamental periods so it cycles clean."""
    n = loop_samples(40, freq)
    out = []
    for i in range(n):
        t = i / RATE
        s = (tone(freq, t) * 0.6
             + tone(freq * 2, t) * 0.28 * bright
             + tone(freq * 3, t) * 0.14 * bright
             + tone(freq * 0.5, t) * 0.3)
        am = 1.0 + roughness * 0.22 * tone(11, t)  # idle throb
        out.append(s * am)
    return norm(out, 0.62)


def noise_loop(seconds, low, high, seed=7):
    """Loopable filtered noise: spectral-sum over whole cycles."""
    n = int(seconds * RATE)
    rng = [math.sin(i * 12.9898 + seed) * 43758.5453 for i in range(64)]
    rng = [x - math.floor(x) for x in rng]
    out = []
    harmonics = 12
    for i in range(n):
        t = i / RATE
        s = 0.0
        for h in range(1, harmonics + 1):
            f = low + (high - low) * rng[h % len(rng)]
            cycles = max(1, round(f * seconds))
            s += tone(cycles / seconds, t) * rng[(h * 7) % len(rng)]
        out.append(s / harmonics)
    return norm(out, 0.5)


def envelope_hit(seconds, body):
    n = int(seconds * RATE)
    out = []
    for i in range(n):
        t = i / RATE
        out.append(body(t, i / n))
    return norm(fade_edges(out, 64), 0.85)


def music_loop(chords, beat, rate=RATE):
    """Tiny square-ish arpeggio: each note decays before the next hits,
    the bar count is exact so the loop point lands on silence."""
    n = int(len(chords) * beat * rate)
    out = [0.0] * n
    note_len = beat * rate
    for ci, freqs in enumerate(chords):
        start = int(ci * note_len)
        for i in range(int(note_len)):
            t = i / rate
            decay = math.exp(-t * 3.2)
            s = 0.0
            for f in freqs:
                s += (tone(f, t) + tone(f * 2, t) * 0.25) * 0.5
            out[start + i] += s * decay / len(freqs)
    return norm(out, 0.55)


A4 = 440.0
def note(semitones):
    return A4 * (2 ** (semitones / 12.0))


CLIPS = {
    # --- loops -------------------------------------------------------------
    # Warm low car engine (~75 Hz, smooth) vs. buzzier, higher moto.
    "engine-car.wav": lambda: engine_loop(75.0, roughness=0.5, bright=0.8),
    "engine-motorcycle.wav": lambda: engine_loop(112.0, roughness=0.9, bright=1.6),
    # Tire hiss while drifting.
    "skid.wav": lambda: noise_loop(0.4, 900, 2600, seed=11),
    # Bright pressurized whoosh while boosting.
    "boost.wav": lambda: noise_loop(0.5, 500, 1400, seed=23),
    # Race bed: an easy two-bar island groove; results: a calm resolve.
    "music-race.wav": lambda: music_loop(
        [(note(0), note(4), note(7)), (note(2), note(5), note(9)),
         (note(4), note(7), note(12)), (note(2), note(5), note(9)),
         (note(0), note(4), note(7)), (note(-3), note(2), note(5)),
         (note(4), note(7), note(12)), (note(7), note(12), note(16))],
        beat=0.32),
    "music-results.wav": lambda: music_loop(
        [(note(0), note(4), note(7), note(12)),
         (note(-1), note(4), note(9), note(12)),
         (note(0), note(4), note(7), note(12)),
         (note(0), note(7), note(12), note(16))],
        beat=0.6),
    # --- one-shots ------------------------------------------------------------
    "cue-landing.wav": lambda: envelope_hit(0.22,
        lambda t, p: tone(64, t) * math.exp(-t * 22)
            + tone(128, t) * math.exp(-t * 30) * 0.4),
    "cue-boost.wav": lambda: envelope_hit(0.4,
        lambda t, p: tone(240 + p * 660, t) * math.exp(-t * 4)),
    "cue-checkpoint.wav": lambda: envelope_hit(0.3,
        lambda t, p: (tone(880, t) if t < 0.12 else tone(1320, t - 0.12))
            * math.exp(-t * 9)),
    "cue-wall.wav": lambda: envelope_hit(0.18,
        lambda t, p: tone(52, t) * math.exp(-t * 26)
            + noise_loop(0.18, 200, 700, seed=5)[int(t * RATE)]
            * math.exp(-t * 18) * 0.5),
    "cue-nudge.wav": lambda: envelope_hit(0.12,
        lambda t, p: tone(140, t) * math.exp(-t * 30)),
    "cue-air.wav": lambda: envelope_hit(0.25,
        lambda t, p: tone(320 + p * 240, t) * math.exp(-t * 8) * 0.6),
    "cue-finish.wav": lambda: envelope_hit(0.9,
        lambda t, p: sum(
            (tone(f, t - d) if t >= d else 0.0) * math.exp(-(t - d) * 5)
            for d, f in ((0.0, note(0)), (0.22, note(4)), (0.44, note(7)),
                         (0.6, note(12)))
        )),
    "cue-go.wav": lambda: envelope_hit(0.35,
        lambda t, p: tone(660, t) * math.exp(-t * 7)
            + tone(990, t) * math.exp(-t * 9) * 0.5),
    # --- M5 Task 5 gadget cues -------------------------------------------------
    # Pickup: a bright two-note "got it" chime. Use: a quick whoosh up.
    # Foam: a soft squelch. Shield block: a glassy ping.
    "cue-gadget-pickup.wav": lambda: envelope_hit(0.3,
        lambda t, p: (tone(1040, t) if t < 0.1 else tone(1560, t - 0.1))
            * math.exp(-t * 8)),
    "cue-gadget-use.wav": lambda: envelope_hit(0.28,
        lambda t, p: tone(360 + p * 720, t) * math.exp(-t * 6)),
    "cue-gadget-foam.wav": lambda: envelope_hit(0.22,
        lambda t, p: tone(90, t) * math.exp(-t * 20)
            + noise_loop(0.22, 100, 600, seed=11)[int(t * RATE)]
            * math.exp(-t * 16) * 0.5),
    "cue-gadget-shield.wav": lambda: envelope_hit(0.3,
        lambda t, p: tone(1240, t) * math.exp(-t * 10)
            + tone(1860, t) * math.exp(-t * 14) * 0.4),
}


def main():
    dry = "--dry-run" in sys.argv
    OUTPUT_DIR.mkdir(parents=True, exist_ok=True)
    written = []
    for name, synth in sorted(CLIPS.items()):
        target = OUTPUT_DIR / name
        samples = synth()
        if dry:
            print(f"[dry] {name}: {len(samples)} samples "
                  f"({len(samples) / RATE:.2f}s)")
            continue
        write_wav(target, samples)
        written.append(name)
        print(f"wrote {name}: {len(samples) / RATE:.2f}s")
    print(f"{len(written)} clips -> {OUTPUT_DIR}")


if __name__ == "__main__":
    main()
