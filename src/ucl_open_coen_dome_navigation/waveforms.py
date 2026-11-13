"""The sounds the workflow plays, made once and committed in src/waveforms, each at the loudness it is played at.

Tom Childs' gen_session.py wrote the tones again before every experiment, at the task's amplitude, and the workflow
scaled them by the amplitude again, so a pip peaked at amplitude squared. The sweep and its loudness are fixed here
(SWEEP_START_HZ ... PIP_PEAK, Tom's 2000 to 25000 Hz in 100 tones at 0.05 squared) and recorded in sweep.json, which
the workflow reads for the sweep's frequencies (Corridor.cs Sweep) and task.py for the reward zones' bounds; the
workflow plays every file as it is. Run `uv run generate-waveforms` after changing one of them, and commit.
"""

import json
from pathlib import Path

import numpy as np
from scipy.io import wavfile
from scipy.signal import butter, sosfilt

ROOT = Path(__file__).resolve().parents[2]
OUT_DIR = ROOT / "src" / "waveforms"
SAMPLE_RATE = 48000  # the mixer's
TONE_MS = 50  # one tone pip; the pips are 50 ms apart
NOISE_SECONDS = 60
ERROR_TONE_SEED = 1  # Tom's error tone was unseeded; seeded here so the committed file can be checked
NOISE_SEED = 0  # as Tom's gen-background-noise.ipynb

SWEEP_START_HZ = 2000
SWEEP_END_HZ = 25000
SWEEP_STEPS = 100
TONE_AMPLITUDE = 0.05  # Tom's toneAmplitude: the error tone peaks at it
PIP_PEAK = TONE_AMPLITUDE * TONE_AMPLITUDE  # a pip and the reward tone peak at amplitude squared, as they always played
REWARD_TONES_HZ = (6000, 9000)  # the reward tone: 250 ms of each, as Tom's FunctionGenerator pair
REWARD_TONE_MS = 250


def tone_frequencies(sweep_start_hz: int = SWEEP_START_HZ, sweep_end_hz: int = SWEEP_END_HZ, sweep_steps: int = SWEEP_STEPS) -> np.ndarray:
    """The sweep's tone frequencies: sweep_steps log-spaced from sweep_start_hz to sweep_end_hz, rounded to whole Hz."""
    return np.round(np.logspace(np.log10(sweep_start_hz), np.log10(sweep_end_hz), sweep_steps)).astype(int)


def tones(frequencies: np.ndarray, peak: float = PIP_PEAK) -> np.ndarray:
    """One 50 ms sine pip per frequency under a half-sine envelope peaking at peak, float32 (n, samples)."""
    duration = TONE_MS / 1000.0
    t = np.linspace(0, duration, int(SAMPLE_RATE * duration), endpoint=False)
    envelope = np.sin(np.pi * t / duration)
    return np.stack([(np.sin(2 * np.pi * f * t) * envelope * peak).astype(np.float32) for f in frequencies])


def reward_tone(peak: float = PIP_PEAK) -> np.ndarray:
    """250 ms of 6 kHz then 250 ms of 9 kHz, sine at peak, float32 (1, samples)."""
    t = np.arange(int(SAMPLE_RATE * REWARD_TONE_MS / 1000)) / SAMPLE_RATE
    return np.concatenate([np.sin(2 * np.pi * f * t) * peak for f in REWARD_TONES_HZ]).astype(np.float32)[np.newaxis, :]


def error_tone(low: float, high: float, amplitude: float = TONE_AMPLITUDE) -> np.ndarray:
    """One second of band-limited white noise between the sweep's ends, peaking at amplitude, int16."""
    sos = butter(4, [low, min(high, SAMPLE_RATE / 2 - 1)], btype="band", fs=float(SAMPLE_RATE), output="sos")
    noise = np.random.default_rng(ERROR_TONE_SEED).standard_normal(SAMPLE_RATE)
    filtered = sosfilt(sos, noise)
    filtered /= np.max(np.abs(filtered)) + 1e-12
    filtered *= amplitude
    return np.clip(filtered * 32767, -32768, 32767).astype(np.int16)


def background_noise(peak: float = PIP_PEAK) -> np.ndarray:
    """60 s of zero-mean uniform white noise with a pip's RMS (half its peak), float32: the task's
    backgroundNoiseLevel is the mixer gain it plays at."""
    unit = np.random.default_rng(NOISE_SEED).uniform(-1.0, 1.0, SAMPLE_RATE * NOISE_SECONDS)  # RMS 1 / sqrt(3)
    return (unit * (0.5 * peak * np.sqrt(3))).astype(np.float32)


def sweep() -> dict:
    """sweep.json: what the workflow and task.py read."""
    return {"sweepStartHz": SWEEP_START_HZ, "sweepEndHz": SWEEP_END_HZ, "sweepSteps": SWEEP_STEPS, "pipPeak": PIP_PEAK}


def write(out_dir: Path = OUT_DIR) -> None:
    """Write all_waveforms.bin (n x 2400 float32, row-major, no header), frequencies.txt, reward_tone.bin (float32),
    error_tone.wav (int16 mono), background_noise.bin (float32 mono, no header) and sweep.json."""
    out_dir.mkdir(parents=True, exist_ok=True)
    frequencies = tone_frequencies()
    tones(frequencies).tofile(out_dir / "all_waveforms.bin")
    (out_dir / "frequencies.txt").write_text("".join(f"{f}\n" for f in frequencies), encoding="utf-8")
    reward_tone().tofile(out_dir / "reward_tone.bin")
    wavfile.write(out_dir / "error_tone.wav", SAMPLE_RATE, error_tone(float(frequencies[0]), float(frequencies[-1])))
    background_noise().tofile(out_dir / "background_noise.bin")
    (out_dir / "sweep.json").write_text(json.dumps(sweep(), indent=2) + "\n", encoding="utf-8")


def main() -> None:
    """Write the waveforms for the sweep above."""
    write()
    print(f"Wrote {SWEEP_STEPS} tones from {SWEEP_START_HZ} to {SWEEP_END_HZ} Hz peaking at {PIP_PEAK:g}, the reward and error tones and the background noise to {OUT_DIR}")


if __name__ == "__main__":
    main()
