"""Composes original campaign music beds from sampled instruments.

Melodic parts are written as MIDI and rendered with fluidsynth through the MuseScore General soundfont
(MIT; parts CC0/PD). Drums are placed from Sonic Pi one-shots (CC0/MIT). Everything is original composition.
Usage: python3 compose.py <outdir> [track ...]
"""
import os
import random
import subprocess
import sys

import numpy as np
import pretty_midi
import soundfile as sf
from scipy.signal import butter, sosfilt, fftconvolve

SR = 44100
SF = "/usr/share/sounds/sf3/MuseScore_General_Full.sf3"
SAMPLES = "/usr/share/sonic-pi/samples"

NOTE = {n: i for i, n in enumerate(["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"])}
NOTE.update({"Db": 1, "Eb": 3, "Gb": 6, "Ab": 8, "Bb": 10})


def n(name, octave):
    return 12 * (octave + 1) + NOTE[name]


def chord(root, octave, intervals):
    base = n(root, octave)
    return [base + i for i in intervals]


MAJ7 = [0, 4, 7, 11]
MIN7 = [0, 3, 7, 10]
MAJ9 = [0, 4, 7, 11, 14]
MIN9 = [0, 3, 7, 10, 14]
ADD9 = [0, 4, 7, 14]
SUS2 = [0, 2, 7, 12]
MAJ = [0, 4, 7, 12]
MIN = [0, 3, 7, 12]
SUS4 = [0, 5, 7, 12]


class Song:
    def __init__(self, bpm, seed):
        self.bpm = bpm
        self.beat = 60.0 / bpm
        self.rng = random.Random(seed)
        self.parts = {}
        self.hits = []  # (time, sample, gain, pan)

    def inst(self, key, program):
        if key not in self.parts:
            self.parts[key] = pretty_midi.Instrument(program=program, name=key)
        return self.parts[key]

    def note(self, key, program, pitch, start_beats, length_beats, velocity, humanize=0.008):
        jitter = self.rng.uniform(-humanize, humanize)
        start = max(0.0, start_beats * self.beat + jitter)
        end = start + max(0.05, length_beats * self.beat)
        velocity = int(max(1, min(127, velocity + self.rng.randint(-6, 6))))
        self.inst(key, program).notes.append(pretty_midi.Note(velocity, int(pitch), start, end))

    def hit(self, sample, beat, gain=1.0, pan=0.0, humanize=0.004):
        t = max(0.0, beat * self.beat + self.rng.uniform(-humanize, humanize))
        self.hits.append((t, sample, gain * self.rng.uniform(0.88, 1.0), pan))


_cache = {}


def load_sample(name):
    if name not in _cache:
        data, rate = sf.read(os.path.join(SAMPLES, name + ".flac"), always_2d=True)
        if data.shape[1] == 1:
            data = np.repeat(data, 2, axis=1)
        assert rate == SR
        _cache[name] = data.astype(np.float64)
    return _cache[name]


def render_midi(song, key, workdir, gain=0.5):
    midi = pretty_midi.PrettyMIDI(initial_tempo=song.bpm)
    midi.instruments.append(song.parts[key])
    mid = os.path.join(workdir, key + ".mid")
    wav = os.path.join(workdir, key + ".wav")
    midi.write(mid)
    subprocess.run(
        ["fluidsynth", "-ni", "-q", "-r", str(SR), "-g", str(gain), "-R", "1", "-C", "0",
         "-F", wav, SF, mid],
        check=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    data, _ = sf.read(wav, always_2d=True)
    return data


def render_drums(song, length):
    out = np.zeros((length, 2))
    for t, name, gain, pan in song.hits:
        s = load_sample(name)
        i = int(t * SR)
        if i >= length:
            continue
        j = min(length, i + len(s))
        left = gain * (1 - max(0, pan))
        right = gain * (1 + min(0, pan))
        out[i:j, 0] += s[: j - i, 0] * left
        out[i:j, 1] += s[: j - i, 1] * right
    return out


def fit(x, length):
    if len(x) >= length:
        return x[:length]
    return np.vstack([x, np.zeros((length - len(x), 2))])


def sidechain(song, length, depth, kick_beats):
    """Gain curve that dips after each kick, the 'pumping' modern pop/house feel."""
    env = np.ones(length)
    release = int(0.6 * song.beat * SR)
    curve = 1 - depth * np.exp(-np.linspace(0, 5, release))
    for b in kick_beats:
        i = int(b * song.beat * SR)
        if i >= length:
            continue
        j = min(length, i + release)
        env[i:j] = np.minimum(env[i:j], curve[: j - i])
    return env[:, None]


def filt(x, kind, freq):
    sos = butter(2, freq, btype=kind, fs=SR, output="sos")
    return sosfilt(sos, x, axis=0)


def reverb(x, seconds=2.2, wet=0.25, seed=7):
    rng = np.random.default_rng(seed)
    size = int(seconds * SR)
    decay = np.exp(-np.linspace(0, 7, size))
    ir = np.stack([rng.standard_normal(size) * decay, rng.standard_normal(size) * decay], axis=1)
    ir = filt(ir, "lowpass", 6000)
    ir[: int(0.012 * SR)] = 0
    ir /= np.sqrt((ir ** 2).sum(axis=0))
    tail = np.stack([fftconvolve(x[:, c], ir[:, c])[: len(x)] for c in range(2)], axis=1)
    return x + wet * tail


def normalize(x, target=0.02):
    """Scales a stem so its level while playing (silence ignored) is the target RMS."""
    active = np.abs(x).max(axis=1) > 1e-4
    if not active.any():
        return x
    rms = np.sqrt((x[active] ** 2).mean())
    return x * (target / rms)


def master(mix, path, fade_in=0.4, fade_out=3.0):
    mix = mix - mix.mean(axis=0)
    fi = int(fade_in * SR)
    fo = int(fade_out * SR)
    mix[:fi] *= np.linspace(0, 1, fi)[:, None]
    mix[-fo:] *= np.linspace(1, 0, fo)[:, None]
    peak = np.abs(mix).max()
    mix = mix / peak * 0.9
    tmp = path + ".tmp.wav"
    sf.write(tmp, mix, SR, subtype="PCM_24")
    subprocess.run(
        ["ffmpeg", "-y", "-v", "error", "-i", tmp, "-af",
         "acompressor=threshold=-14dB:ratio=2.5:attack=15:release=180:makeup=2,"
         "loudnorm=I=-14:TP=-1.2:LRA=9,aresample=44100",
         "-c:a", "libmp3lame", "-b:a", "160k", path],
        check=True)
    os.remove(tmp)


def bars_of(progression, bars_each=1):
    for c in progression:
        for _ in range(bars_each):
            yield c


# --------------------------------------------------------------------------------------------------------------
# Track 1: Warm acoustic. Fingerpicked steel guitar, soft piano, upright-ish bass, brushed groove. 84 BPM, G major.
# --------------------------------------------------------------------------------------------------------------
def warm_acoustic():
    s = Song(84, 11)
    prog = [("G", MAJ, "G"), ("D", MAJ, "F#"), ("E", MIN7, "E"), ("C", ADD9, "C")]
    sections = [("intro", 1), ("a", 8), ("b", 8), ("a", 8), ("b", 8), ("outro", 4)]
    bar = 0
    pick = [0, 2, 1, 3, 2, 1, 3, 2]  # index pattern over chord tones, eighths
    for name, length in sections:
        for k in range(length):
            root, quality, bass = prog[k % 4]
            start = bar * 4
            tones = chord(root, 3, quality) + [n(root, 4) + quality[1]]
            for e, idx in enumerate(pick):
                pitch = tones[idx % len(tones)]
                if pitch < n("D", 3):
                    pitch += 12
                s.note("guitar", 25, pitch, start + e * 0.5, 1.4, 70 if e % 2 == 0 else 58)
            if name != "intro":
                s.note("bass", 32, n(bass, 2), start, 1.8, 82)
                s.note("bass", 32, n(bass, 2) + 7, start + 2.5, 1.2, 70)
            if name in ("b", "outro"):
                for v in chord(root, 4, quality)[:3]:
                    s.note("piano", 0, v, start, 3.8, 50)
            if name == "b":
                melody = [7, 4, 2, 4] if k % 2 == 0 else [9, 7, 4, 2]
                for m, iv in enumerate(melody):
                    s.note("lead", 0, n(root, 5) + iv - 12 * (iv > 7 and root in "DE"), start + m, 0.9, 64)
            if name in ("a", "b"):
                for b in range(4):
                    s.hit("bd_808" if b in (0,) else "elec_soft_kick", start + b, 0.55 if b == 0 else 0.25)
                    if b == 2.5:
                        pass
                s.hit("elec_soft_kick", start + 2.5, 0.3)
                for b in (1, 3):
                    s.hit("perc_snap", start + b, 0.45, pan=0.1)
                for e in range(8):
                    s.hit("drum_cymbal_pedal", start + e * 0.5 + (0.08 if e % 2 else 0), 0.18 if e % 2 else 0.12,
                          pan=-0.3)
            bar += 1
    return s, bar, dict(guitar=1.0, bass=0.7, piano=0.5, lead=0.6, drums=0.55), 0.15, "warm"


# --------------------------------------------------------------------------------------------------------------
# Track 2: Modern chill. Rhodes 7th/9th chords, warm sub, laid-back house groove with swing. 96 BPM, A minor.
# --------------------------------------------------------------------------------------------------------------
def modern_chill():
    s = Song(96, 22)
    prog = [("F", MAJ9), ("E", MIN7), ("D", MIN9), ("C", MAJ7)]
    sections = [("intro", 1), ("a", 8), ("b", 8), ("break", 4), ("b", 8), ("outro", 4)]
    bar = 0
    kicks = []
    rhythm = [(0, 1.4), (1.5, 0.4), (2.5, 1.3)]  # syncopated chord stabs
    for name, length in sections:
        for k in range(length):
            root, quality = prog[k % 4]
            start = bar * 4
            voicing = chord(root, 3, quality)
            voicing = [v + 12 if v < n("A", 3) else v for v in voicing]
            for off, dur in rhythm:
                for v in voicing:
                    s.note("keys", 4, v, start + off, dur, 62 if off == 0 else 50)
            if name != "intro":
                s.note("bass", 38, n(root, 2), start, 1.4, 92)
                s.note("bass", 38, n(root, 2), start + 1.75, 0.5, 70)
                s.note("bass", 38, n(root, 2) + 7, start + 2.5, 1.2, 80)
            if name in ("b",):
                hook = [(0.0, 7), (0.75, 9), (1.5, 12), (2.5, 9), (3.0, 7)] if k % 2 == 0 else [(0.5, 4), (1.5, 7),
                                                                                               (2.5, 2)]
                for off, iv in hook:
                    s.note("pluck", 11, n(root, 5) + iv, start + off, 0.6, 70)
            if name == "break":
                s.note("pad", 89, n(root, 3), start, 4, 60)
            if name in ("a", "b"):
                for b in range(4):
                    s.hit("bd_haus", start + b, 0.85)
                    kicks.append(start + b)
                for b in (1, 3):
                    s.hit("perc_snap2", start + b, 0.6, pan=0.05)
                    s.hit("sn_dub", start + b, 0.18)
                for sxt in range(16):
                    swing = 0.06 if sxt % 2 else 0
                    g = 0.16 if sxt % 4 == 2 else 0.08
                    s.hit("drum_cymbal_closed", start + sxt * 0.25 + swing, g, pan=0.35)
                if k % 4 == 3:
                    s.hit("drum_cymbal_open", start + 3.5, 0.18, pan=-0.2)
            bar += 1
    return s, bar, dict(keys=1.0, bass=0.9, pluck=0.55, pad=0.4, drums=1.0), 0.22, "chill", kicks


# --------------------------------------------------------------------------------------------------------------
# Track 3: Upbeat lifestyle. Piano house stabs, bright guitar, four-on-the-floor, claps. 118 BPM, D major.
# --------------------------------------------------------------------------------------------------------------
def upbeat_pop():
    s = Song(118, 33)
    prog = [("D", MAJ), ("A", MAJ), ("B", MIN), ("G", MAJ)]
    sections = [("intro", 1), ("a", 8), ("b", 8), ("break", 4), ("b", 8), ("outro", 4)]
    bar = 0
    kicks = []
    for name, length in sections:
        for k in range(length):
            root, quality = prog[k % 4]
            start = bar * 4
            voicing = chord(root, 4, quality)
            voicing = [v - 12 if v > n("A", 4) else v for v in voicing]
            stabs = [0.5, 1.5, 2.5, 3.0, 3.5] if name in ("b",) else [0.5, 1.5, 2.5, 3.5]
            for off in stabs:
                for v in voicing:
                    s.note("piano", 0, v, start + off, 0.35, 84)
            if name != "intro":
                for e in range(8):
                    s.note("bass", 33, n(root, 2), start + e * 0.5 + 0.0, 0.4, 88 if e % 2 else 76)
            if name in ("a", "b", "outro"):
                for e in range(8):
                    if e % 2 == 1:
                        for v in chord(root, 3, quality)[1:]:
                            s.note("guitar", 27, v + 12, start + e * 0.5, 0.25, 60)
            if name == "b":
                hook = [(0, 12), (0.5, 14), (1, 16), (2, 14), (2.5, 12), (3, 9)] if k % 2 == 0 else \
                    [(0, 9), (1, 12), (1.5, 9), (2, 7)]
                for off, iv in hook:
                    s.note("lead", 80 if False else 81, n(root, 4) + iv, start + off, 0.45, 58)
            if name == "break":
                for v in voicing:
                    s.note("pad", 48, v, start, 4, 66)
            if name in ("a", "b"):
                for b in range(4):
                    s.hit("bd_haus", start + b, 0.95)
                    s.hit("bd_808", start + b, 0.25)
                    kicks.append(start + b)
                    s.hit("drum_cymbal_open", start + b + 0.5, 0.11, pan=-0.3)
                for b in (1, 3):
                    s.hit("perc_snap", start + b, 0.55, pan=0.08)
                    s.hit("perc_snap2", start + b + 0.012, 0.4, pan=-0.08)
                    s.hit("sn_dub", start + b, 0.25)
                for sxt in range(16):
                    s.hit("drum_cymbal_closed", start + sxt * 0.25, 0.1 if sxt % 2 else 0.05, pan=0.35)
            if name == "break" and k == length - 1:
                for i in range(8):
                    s.hit("sn_dub", start + 2 + i * 0.25, 0.1 + i * 0.04)
            bar += 1
    return s, bar, dict(piano=1.0, bass=0.85, guitar=0.5, lead=0.45, pad=0.45, drums=1.1), 0.18, "upbeat", kicks


# --------------------------------------------------------------------------------------------------------------
# Track 4: Luxury cinematic. Felt piano arpeggios, chamber strings, low pulse, soft boom hits. 72 BPM, Eb major.
# --------------------------------------------------------------------------------------------------------------
def luxury_cinematic():
    s = Song(72, 44)
    prog = [("Eb", MAJ9), ("C", MIN9), ("Ab", MAJ7), ("Bb", SUS4)]
    sections = [("intro", 1), ("a", 8), ("b", 8), ("a", 4), ("outro", 4)]
    bar = 0
    for name, length in sections:
        for k in range(length):
            root, quality = prog[k % 4]
            start = bar * 4
            tones = chord(root, 3, quality)
            tones = [t + 12 if t < n("G", 3) else t for t in tones]
            arp = tones + [tones[1] + 12, tones[2] + 12]
            order = [0, 2, 4, 1, 3, 5, 2, 4]
            for e, idx in enumerate(order):
                s.note("piano", 0, arp[idx % len(arp)], start + e * 0.5, 1.2, 56 if e % 4 == 0 else 44)
            s.note("piano", 0, n(root, 2), start, 3.9, 60)
            if name in ("a", "b", "outro"):
                for v in chord(root, 3, quality)[:4]:
                    s.note("strings", 48, v, start, 4.0, 54 if name == "a" else 70, humanize=0.0)
                s.note("cello", 42, n(root, 2), start, 4.0, 64, humanize=0.0)
            if name == "b":
                line = [12, 14, 11, 7] if k % 2 == 0 else [14, 16, 12, 10]
                s.note("violin", 40, n(root, 4) + line[0], start, 1.5, 72, humanize=0.0)
                s.note("violin", 40, n(root, 4) + line[1], start + 1.5, 1.5, 70, humanize=0.0)
                s.note("violin", 40, n(root, 4) + line[2], start + 3, 1.0, 66, humanize=0.0)
                for b in range(4):
                    s.hit("bd_808", start + b, 0.35)
                for b in (1, 3):
                    s.hit("perc_snap", start + b, 0.25)
                for e in range(8):
                    s.hit("elec_tick", start + e * 0.5, 0.12, pan=0.3)
            if name in ("a", "b") and k == 0:
                s.hit("bd_boom", start, 0.5)
            bar += 1
    return s, bar, dict(piano=1.0, strings=0.55, cello=0.45, violin=0.55, drums=0.4), 0.35, "lux"


TRACKS = {
    "warm-acoustic": warm_acoustic,
    "modern-chill": modern_chill,
    "upbeat-pop": upbeat_pop,
    "luxury-cinematic": luxury_cinematic,
}


def build(name, outdir):
    result = TRACKS[name]()
    song, bars, gains, wet, _ = result[:5]
    kicks = result[5] if len(result) > 5 else []
    work = os.path.join(outdir, "work-" + name)
    os.makedirs(work, exist_ok=True)
    length = int((bars * 4 * song.beat + 3.0) * SR)
    duck = sidechain(song, length, 0.45, kicks) if kicks else np.ones((length, 1))
    mix = np.zeros((length, 2))
    for key, g in gains.items():
        if key == "drums" or key not in song.parts:
            continue
        stem = normalize(fit(render_midi(song, key, work), length)) * g
        if key == "bass":
            stem = filt(stem, "lowpass", 900)
            mix += stem * duck
        else:
            stem = filt(stem, "highpass", 120)
            mix += reverb(stem, wet=wet) * (duck if key not in ("lead", "violin") else 1)
    drums = normalize(render_drums(song, length)) * gains.get("drums", 1.0)
    mix += reverb(drums, seconds=0.9, wet=0.08, seed=3)
    master(mix, os.path.join(outdir, name + ".mp3"))
    print(name, song.bpm, "bpm", round(length / SR, 1), "s")


if __name__ == "__main__":
    out = sys.argv[1]
    os.makedirs(out, exist_ok=True)
    for name in sys.argv[2:] or TRACKS:
        build(name, out)
