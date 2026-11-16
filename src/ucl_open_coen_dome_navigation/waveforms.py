"""The sounds the workflow plays, made once and committed in src/waveforms.

Tom Childs' gen_session.py wrote the tones again before every experiment, at the task's amplitude, and the workflow
scaled them by the amplitude again, so a pip peaked at amplitude squared. The sweep is fixed here (SWEEP_START_HZ ...
PIP_PEAK, Tom's 2000 to 25000 Hz in 100 tones at 0.05 squared) and recorded in sweep.json, which the workflow reads
for the sweep's frequencies (Sweep.cs) and task.py for the reward zones' bounds. The files' amplitudes do not
set how loud they play: the workflow plays each at the task file's level in dBFS RMS of the file as stored (tonePips,
rewardTone .levelDbfs) on rig.yml's speakers, equalised for each (OpenRigs.Audio SpeakerBuffer); the penalty sound is the lab's, from OpenRigs.Audio,
and the background noise the lab's one file, rig.yml's speakerArray.backgroundNoise. Run `uv run generate-waveforms` after changing one of them, and commit.
"""

import json
from pathlib import Path

import numpy as np

ROOT = Path(__file__).resolve().parents[2]
OUT_DIR = ROOT / "src" / "waveforms"
SAMPLE_RATE = 48000  # every waveform's, recorded in sweep.json: the workflow stops at Start unless rig.yml's speakerArray.device.sampleRate is the same
TONE_MS = 50  # one tone pip; the pips are 50 ms apart

SWEEP_START_HZ = 2000
SWEEP_END_HZ = 25000
SWEEP_STEPS = 100
TONE_AMPLITUDE = 0.05  # Tom's toneAmplitude
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


def sweep() -> dict:
    """sweep.json: what the workflow and task.py read, with the rate every waveform was made at."""
    return {"sweepStartHz": SWEEP_START_HZ, "sweepEndHz": SWEEP_END_HZ, "sweepSteps": SWEEP_STEPS, "pipPeak": PIP_PEAK, "sampleRate": SAMPLE_RATE}


def write(out_dir: Path = OUT_DIR) -> None:
    """Write all_waveforms.bin (n x 2400 float32, row-major, no header), frequencies.txt, reward_tone.bin (float32)
    and sweep.json. The penalty sound is OpenRigs.Audio's; the background noise is rig.yml's speakerArray.backgroundNoise."""
    out_dir.mkdir(parents=True, exist_ok=True)
    frequencies = tone_frequencies()
    tones(frequencies).tofile(out_dir / "all_waveforms.bin")
    (out_dir / "frequencies.txt").write_text("".join(f"{f}\n" for f in frequencies), encoding="utf-8")
    reward_tone().tofile(out_dir / "reward_tone.bin")
    (out_dir / "sweep.json").write_text(json.dumps(sweep(), indent=2) + "\n", encoding="utf-8")


def main() -> None:
    """Write the waveforms for the sweep above."""
    write()
    print(f"Wrote {SWEEP_STEPS} tones from {SWEEP_START_HZ} to {SWEEP_END_HZ} Hz peaking at {PIP_PEAK:g} and the reward tone to {OUT_DIR}")


if __name__ == "__main__":
    main()
