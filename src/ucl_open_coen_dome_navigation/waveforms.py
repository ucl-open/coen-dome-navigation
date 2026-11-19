"""The sounds the workflow plays, made once and committed in src/sounds.

Tom Childs' gen_session.py wrote the tones again before every experiment, at the task's amplitude, and the workflow
scaled them by the amplitude again, so a pip peaked at amplitude squared. The sweep is fixed here (SWEEP_START_HZ ...
SWEEP_PIP_COUNT, Tom's 2000 to 25000 Hz in 100 pips) and recorded in sweep.json, which the workflow reads for the sweep's
frequencies (Sweep.cs) and task.py for the reward zone's bounds. Each pip is stored at PIP_LEVEL_DBFS RMS, -58.1, the level
the pips played at from 15 Nov 2026 (Tom's 0.05 squared peak, -58.06, rounded); the workflow scales them to the trial's
auditoryStimulus.levelDbfs (PipSet). The reward tone plays at rewardTone.levelDbfs of the file as
stored, on rig.yml's speakers, equalised for each (OpenRigs.Audio PlayRewardTone); the penalty sound is the lab's, from OpenRigs.Audio,
and the background noise the lab's one file, rig.yml's speakerArray.backgroundNoise. Run `uv run generate-waveforms` after changing one of them, and commit.
"""

import json
from pathlib import Path

import numpy as np

ROOT = Path(__file__).resolve().parents[2]
OUT_DIR = ROOT / "src" / "sounds"
SAMPLE_RATE = 48000  # every waveform's, recorded in sweep.json: the workflow stops at Start unless rig.yml's speakerArray.device.sampleRate is the same
PIP_MS = 50  # one pip; the pips are 50 ms apart

SWEEP_START_HZ = 2000
SWEEP_END_HZ = 25000
SWEEP_PIP_COUNT = 100
TONE_AMPLITUDE = 0.05  # Tom's toneAmplitude
REWARD_TONE_PEAK = TONE_AMPLITUDE * TONE_AMPLITUDE  # the reward tone peaks at amplitude squared, as it always played
PIP_LEVEL_DBFS = -58.1  # each pip's RMS as made, in dBFS; sweep.json's pipLevelDbfs is the pips' level measured (level_dbfs)
SILENT_STRETCH_SECONDS = 0.001  # a run of exactly-zero samples this long or longer is silence, left out of a level (OpenRigs.Audio SoundLevel)
REWARD_TONES_HZ = (6000, 9000)  # the reward tone: 250 ms of each, as Tom's FunctionGenerator pair
REWARD_TONE_MS = 250


def pip_frequencies(sweep_start_hz: int = SWEEP_START_HZ, sweep_end_hz: int = SWEEP_END_HZ, pip_count: int = SWEEP_PIP_COUNT) -> np.ndarray:
    """The sweep's pip frequencies: pip_count log-spaced from sweep_start_hz to sweep_end_hz, rounded to whole Hz."""
    return np.round(np.logspace(np.log10(sweep_start_hz), np.log10(sweep_end_hz), pip_count)).astype(int)


def pips(frequencies: np.ndarray, level_dbfs: float = PIP_LEVEL_DBFS) -> np.ndarray:
    """One 50 ms sine pip per frequency under a half-sine envelope, each scaled to level_dbfs RMS, float32 (n, samples)."""
    duration = PIP_MS / 1000.0
    t = np.linspace(0, duration, int(SAMPLE_RATE * duration), endpoint=False)
    envelope = np.sin(np.pi * t / duration)
    waves = np.stack([np.sin(2 * np.pi * f * t) * envelope for f in frequencies])
    rms = np.sqrt(np.mean(waves**2, axis=1, keepdims=True))
    return (waves * 10 ** (level_dbfs / 20) / rms).astype(np.float32)


def level_dbfs(samples: np.ndarray, sample_rate: int = SAMPLE_RATE) -> float:
    """A sound's level in dBFS RMS by the lab's rule (OpenRigs.Audio SoundLevel.RmsDbfs): the RMS of its samples outside
    silent stretches, runs of exactly-zero samples at least SILENT_STRETCH_SECONDS long (a lone zero counts)."""
    x = np.asarray(samples, dtype=np.float64).ravel()
    keep = np.ones(x.size, dtype=bool)
    zero = np.concatenate(([False], x == 0, [False]))
    starts = np.flatnonzero(~zero[:-1] & zero[1:])
    ends = np.flatnonzero(zero[:-1] & ~zero[1:])
    shortest = int(np.ceil(SILENT_STRETCH_SECONDS * sample_rate))
    for start, end in zip(starts, ends):
        if end - start >= shortest:
            keep[start:end] = False
    return float(20 * np.log10(np.sqrt(np.mean(x[keep] ** 2))))


def reward_tone(peak: float = REWARD_TONE_PEAK) -> np.ndarray:
    """250 ms of 6 kHz then 250 ms of 9 kHz, sine at peak, float32 (1, samples)."""
    t = np.arange(int(SAMPLE_RATE * REWARD_TONE_MS / 1000)) / SAMPLE_RATE
    return np.concatenate([np.sin(2 * np.pi * f * t) * peak for f in REWARD_TONES_HZ]).astype(np.float32)[np.newaxis, :]


def sweep() -> dict:
    """sweep.json: what the workflow and task.py read, with the rate every waveform was made at; pipLevelDbfs is the pips'
    level by the lab's rule (level_dbfs, to 0.01 dB), which the workflow plays each pip at its trial's level from."""
    waves = pips(pip_frequencies())
    level = round(float(np.mean([level_dbfs(wave) for wave in waves])), 2)
    return {"startHz": SWEEP_START_HZ, "endHz": SWEEP_END_HZ, "pipCount": SWEEP_PIP_COUNT, "pipLevelDbfs": level, "sampleRate": SAMPLE_RATE}


def write(out_dir: Path = OUT_DIR) -> None:
    """Write pips.bin (n x 2400 float32, row-major, no header; row i is pip_frequencies()[i], as Sweep.cs computes it),
    reward_tone.bin (float32) and sweep.json. The penalty sound is OpenRigs.Audio's; the background noise is rig.yml's
    speakerArray.backgroundNoise."""
    out_dir.mkdir(parents=True, exist_ok=True)
    pips(pip_frequencies()).tofile(out_dir / "pips.bin")
    reward_tone().tofile(out_dir / "reward_tone.bin")
    (out_dir / "sweep.json").write_text(json.dumps(sweep(), indent=2) + "\n", encoding="utf-8")


def main() -> None:
    """Write the waveforms for the sweep above."""
    write()
    print(f"Wrote {SWEEP_PIP_COUNT} pips from {SWEEP_START_HZ} to {SWEEP_END_HZ} Hz at {PIP_LEVEL_DBFS:g} dBFS RMS and the reward tone to {OUT_DIR}")


if __name__ == "__main__":
    main()
