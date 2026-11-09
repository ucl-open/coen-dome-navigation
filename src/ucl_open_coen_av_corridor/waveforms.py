"""The sounds the workflow plays, made once from config/task.yml and committed in src/waveforms.

Tom Childs' gen_session.py wrote the tones again before every experiment. They depend only on the task's
start_freq, end_freq, n_freq_bins and amplitude, so they are made here once, as gen_session made them:
the tones and the error tone at the task's amplitude (the workflow scales the tones by the amplitude
again, so a pip peaks at amplitude squared, as it always has). sweep.txt records the four values and
the workflow stops at Go if the task file's differ. Run `uv run generate-waveforms` after changing any
of them in config/task.yml, and commit.
"""

from pathlib import Path

import numpy as np
import yaml
from scipy.io import wavfile
from scipy.signal import butter, sosfilt

from ucl_open_coen_av_corridor.task import UclOpenCoenAvCorridorTaskLogic

ROOT = Path(__file__).resolve().parents[2]
OUT_DIR = ROOT / "src" / "waveforms"
TASK_FILE = ROOT / "config" / "task.yml"
SAMPLE_RATE = 48000  # the mixer's
TONE_MS = 50  # one tone pip; the pips are 50 ms apart
NOISE_SECONDS = 60
ERROR_TONE_SEED = 1  # Tom's error tone was unseeded; seeded here so the committed file can be checked
NOISE_SEED = 0  # as Tom's gen-background-noise.ipynb


def tone_frequencies(start_freq: int, end_freq: int, n_freq_bins: int) -> np.ndarray:
    """The sweep's tone frequencies: n_freq_bins log-spaced from start_freq to end_freq, rounded to whole Hz."""
    return np.round(np.logspace(np.log10(start_freq), np.log10(end_freq), n_freq_bins)).astype(int)


def tones(frequencies: np.ndarray, amplitude: float) -> np.ndarray:
    """One 50 ms sine pip per frequency under a half-sine envelope at amplitude, float32 (n, samples)."""
    duration = TONE_MS / 1000.0
    t = np.linspace(0, duration, int(SAMPLE_RATE * duration), endpoint=False)
    envelope = np.sin(np.pi * t / duration)
    return np.stack([(np.sin(2 * np.pi * f * t) * envelope * amplitude).astype(np.float32) for f in frequencies])


def error_tone(low: float, high: float, amplitude: float) -> np.ndarray:
    """One second of band-limited white noise between the sweep's ends, peaking at amplitude, int16."""
    sos = butter(4, [low, min(high, SAMPLE_RATE / 2 - 1)], btype="band", fs=float(SAMPLE_RATE), output="sos")
    noise = np.random.default_rng(ERROR_TONE_SEED).standard_normal(SAMPLE_RATE)
    filtered = sosfilt(sos, noise)
    filtered /= np.max(np.abs(filtered)) + 1e-12
    filtered *= amplitude
    return np.clip(filtered * 32767, -32768, 32767).astype(np.int16)


def background_noise() -> np.ndarray:
    """60 s of zero-mean uniform white noise in [-1, 1], float32; the workflow sets its level."""
    return np.random.default_rng(NOISE_SEED).uniform(-1.0, 1.0, SAMPLE_RATE * NOISE_SECONDS).astype(np.float32)


def sweep_line(start_freq: int, end_freq: int, n_freq_bins: int, amplitude: float) -> str:
    """sweep.txt's one line, which the workflow's CheckWaveforms reads: start end bins amplitude."""
    return f"{start_freq} {end_freq} {n_freq_bins} {amplitude!r}\n"


def write(start_freq: int, end_freq: int, n_freq_bins: int, amplitude: float, out_dir: Path = OUT_DIR) -> None:
    """Write all_waveforms.bin (n x 2400 float32, row-major, no header), frequencies.txt, error_tone.wav
    (int16 mono), background_noise.bin (float32 mono, no header) and sweep.txt."""
    out_dir.mkdir(parents=True, exist_ok=True)
    frequencies = tone_frequencies(start_freq, end_freq, n_freq_bins)
    tones(frequencies, amplitude).tofile(out_dir / "all_waveforms.bin")
    (out_dir / "frequencies.txt").write_text("".join(f"{f}\n" for f in frequencies), encoding="utf-8")
    wavfile.write(out_dir / "error_tone.wav", SAMPLE_RATE, error_tone(float(frequencies[0]), float(frequencies[-1]), amplitude))
    background_noise().tofile(out_dir / "background_noise.bin")
    (out_dir / "sweep.txt").write_text(sweep_line(start_freq, end_freq, n_freq_bins, amplitude), encoding="utf-8")


def task_parameters(task_file: Path = TASK_FILE):
    """The task parameters config/task.yml gives."""
    data = yaml.safe_load(task_file.read_text(encoding="utf-8"))
    return UclOpenCoenAvCorridorTaskLogic.model_validate(data).task_parameters


def main() -> None:
    """Write the waveforms for config/task.yml's sweep."""
    p = task_parameters()
    write(p.start_freq, p.end_freq, p.n_freq_bins, p.amplitude)
    print(f"Wrote {p.n_freq_bins} tones from {p.start_freq} to {p.end_freq} Hz at amplitude {p.amplitude}, the error tone and the background noise to {OUT_DIR}")


if __name__ == "__main__":
    main()
