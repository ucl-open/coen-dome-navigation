"""The task file's schema: config/task-abstract-av-corridor.yml, which rig-control copies at launch and again at Go with the
form's edits; the workflow reads the copy (TaskPath) at Start for the background noise and at Go for the rest. Ported from Tom Childs' auditory-vr-corridor task.py.

The stage is the TrainingStage experiment setting, a row of trainingStages from 0 (Tom's stages 1 to 6 are rows 0 to
5), and each row says what the stage does; trialStructure holds the ITI and the track lengths, penalty the timeout and penalty
sound, and rngSeed seeds every draw. The sweep is src/waveforms/sweep.json's, made with the tone files
(`uv run generate-waveforms`), not the task file's; every sound's level is the task file's (levelDbfs, in dBFS
RMS: tonePips, rewardTone, backgroundNoise and penalty.sound)."""

import json
from enum import StrEnum
from pathlib import Path
from typing import Literal

from open_rigs.core.task import BackgroundNoise, Optogenetics, Penalty, QuietWait, RewardTone, StimulusSound, Task, TaskBlock, TaskParameters
from open_rigs.components.zapit import ZAPIT_MAX_RAMP_DOWN_SECONDS, ZAPIT_REPLY_MARGIN_SECONDS
from open_rigs.rigs.lab_rig import MAX_REWARD_UL
from pydantic import Field, field_validator, model_validator

from ucl_open_coen_dome_navigation import __semver__

BEEP_SECONDS = 0.05  # the tone pips are 50 ms apart on the Harp clock (TrialLogic.cs BeepSeconds)
LASER_MAX_MARGIN_SECONDS = 0.25  # how much longer than the longest laser (responseWindowSeconds + laser_to_stimulus_seconds) the laser_max_seconds cap must be
MAX_LEAD_SECONDS = 0.2  # the furthest the trigger may come before the first pip, in whole beeps (TrialLogic.cs MaxLeadSeconds)
UNLIMITED_WINDOW_TRIGGER_AFTER_SECONDS = 2.0  # the furthest after the first pip the trigger may come on a stage without responseWindowSeconds (TrialLogic.cs)

SWEEP_FILE = Path(__file__).resolve().parents[1] / "waveforms" / "sweep.json"  # src/waveforms/sweep.json


def sweep() -> dict:
    """The sweep the committed tone files were made for: sweepStartHz, sweepEndHz, sweepSteps, pipPeak and the waveforms' sampleRate."""
    return json.loads(SWEEP_FILE.read_text(encoding="utf-8"))


class TrialStructure(TaskBlock):
    """Timing of each trial."""

    iti_min_seconds: float = Field(
        default=2.0,
        ge=0,
        description="Lower bound of the inter-trial interval, drawn uniformly for every trial, in seconds: from the last trial's outcome (after any penalty timeout; trial 1: the first clock tick after Go) to this trial's first tone pip, with a quiet mouse. It includes the time to drink a reward (Tom Childs' fixed 2 s before his 0 or 1 s); the stage's quiet waits can only lengthen it (quietWaitSeconds)",
    )
    iti_max_seconds: float = Field(default=3.0, ge=0, description="Upper bound of the uniformly drawn inter-trial interval, in seconds (itiMinSeconds)")
    track_lengths_cm: list[float] = Field(
        default=[80.0, 100.0, 120.0], min_length=1, description="Track lengths, one drawn per trial and scaled by the stage's trackLengthScale, in cm"
    )

    @model_validator(mode="after")
    def _iti_ordered(self) -> "TrialStructure":
        if self.iti_max_seconds < self.iti_min_seconds:
            raise ValueError("iti_max_seconds must be >= iti_min_seconds")
        return self


class Truncation(TaskBlock):
    """Where the stimulus switches off on a truncated trial (a stage's truncatedTrialFraction says how many).

    On a truncated trial the stimulus (tone and grating) switches off part way along the sweep. Everything else about
    the trial is unchanged, so it is still rewarded as normal: the animal has to keep going and find the reward
    zone without the stimulus.
    """

    sweep_fractions: list[float] = Field(
        default=[0.4, 0.6],
        min_length=1,
        description="Where the stimulus switches off, as a fraction of the sweep on a log scale, split evenly over truncated trials. The reward floor is at about 0.8",
    )

    @field_validator("sweep_fractions")
    @classmethod
    def _fractions_inside(cls, value: list[float]) -> list[float]:
        if any(not 0 < fraction < 1 for fraction in value):
            raise ValueError("Truncation sweep fractions must be between 0 and 1")
        return value


class OptogeneticsConfig(Optogenetics):
    """Zapit optogenetic stimulation: the lab's block (open_rigs.core.task Optogenetics) and this task's laser timing.

    The stimulation sites and ramp-down are set in the Zapit conditions file loaded in Zapit, not here; Bonsai reads
    the same file to list its conditions. "Stimulus" is the tone and grating; the laser is continuous: it starts at the
    trigger (the trigger line's rising edge on a hardware-triggered rig), laser_to_stimulus_seconds before the first
    tone pip (after it if negative), in whole 50 ms beeps and at most 0.2 s, and Bonsai stops it at the trial's
    outcome, or after laser_max_seconds if that comes first. A trial that ends before its trigger has no laser.
    """

    laser_max_seconds: float = Field(
        default=12.5,
        gt=0,
        description="Longest the laser stays on in one trial, in seconds from the trigger, timed by Bonsai; it then ramps down over the condition's offRampDownDuration_ms. A safety cap: the laser normally stops at the trial's outcome. Timed on the Harp clock, with a wall-clock backstop should the board stall. With optogenetics on it must be at least the longest stage responseWindowSeconds + laser_to_stimulus_seconds + 0.25 s (the laser runs from the first pip less laser_to_stimulus_seconds to at most the first pip plus responseWindowSeconds), so it never ends the laser before the response window closes; on a stage without responseWindowSeconds a longer trial loses its laser before the outcome",
    )
    trigger_hold_seconds: float = Field(
        default=1.0,
        ge=ZAPIT_MAX_RAMP_DOWN_SECONDS,
        description=f"How long the trigger line, and the masking light on it, stays high after the outcome, in seconds, on every trial of an optogenetics experiment (the line stays low without optogenetics): at least {ZAPIT_MAX_RAMP_DOWN_SECONDS}, the longest ramp-down a conditions file may have, and at most trialStructure.itiMinSeconds - laser_to_stimulus_seconds - 0.2 s, since the next trial is armed once the line falls and Zapit's reply needs 0.2 s before its trigger",
    )

    @model_validator(mode="after")
    def validate_lead_time(self):
        # The trigger and the first pip fall on beeps, so the lead is a whole number of beeps, at most MAX_LEAD_SECONDS
        beeps = self.laser_to_stimulus_seconds / BEEP_SECONDS
        if abs(beeps - round(beeps)) > 1e-6:
            raise ValueError(f"laser_to_stimulus_seconds ({self.laser_to_stimulus_seconds}) must be a multiple of the {BEEP_SECONDS} s beep interval")
        if self.laser_to_stimulus_seconds > MAX_LEAD_SECONDS + 1e-9:
            raise ValueError(f"laser_to_stimulus_seconds ({self.laser_to_stimulus_seconds}) must be at most {MAX_LEAD_SECONDS} s")
        return self


class Modality(StrEnum):
    """What a trial shows."""

    AUDITORY = "auditory"
    VISUAL = "visual"
    AUDIOVISUAL = "audiovisual"


class TrainingStage(QuietWait):
    """One row of the training stage table: what the stage does, its quiet waits the lab's (open_rigs.core.task QuietWait). Rows are selected by the TrainingStage setting."""

    label: str = Field(description="The stage's name, for people")
    reward_floor_hz: float = Field(ge=1, le=25000, description="Lower frequency of the reward zone, in Hz")
    reward_ceiling_hz: float = Field(ge=1, le=25000, description="Upper frequency of the reward zone, in Hz")
    reward_size_ul: float = Field(
        gt=0,
        le=MAX_REWARD_UL,
        description=f"Microlitres each reward gives, at most {MAX_REWARD_UL} (the most one reward may ask for), on the left spout's valve, planned from the valve's calibration (calibration\\valve-DO1.json). An uncalibrated valve stops the workflow at Start",
    )
    lick_to_reward: bool = Field(description="Whether a lick in the zone earns the reward; false rewards reaching the zone, no lick needed")
    wrong_lick_limit: int = Field(ge=0, description="Licks outside the zone that end the trial; 0: wrong licks never end it")
    penalise_wrong_zone: bool = Field(
        description="Whether a trial that ends outside the reward zone is penalised. A trial that runs out of time or track with the frequency in the zone is penalised on every stage"
    )
    response_window_seconds: float | None = Field(
        default=None, gt=0, description="The response window: the longest a trial runs from its first tone pip, in seconds, after which it ends as NoResponse (endReason ResponseWindowEnd); null: the window never closes"
    )
    track_end_ends_trial: bool = Field(description="Whether reaching the end of the track ends the trial")
    track_length_scale: float = Field(gt=0, description="The drawn track length is multiplied by this")
    frequency_drift: bool = Field(description="Whether the frequency also drifts up with time, not only with distance run")
    truncated_trial_fraction: float = Field(ge=0, le=1, description="Fraction of trials whose stimulus switches off early (truncation.sweepFractions)")

    @model_validator(mode="after")
    def _zone_ordered(self) -> "TrainingStage":
        if self.reward_floor_hz >= self.reward_ceiling_hz:
            raise ValueError("reward_floor_hz must be below reward_ceiling_hz")
        return self


class UclOpenCoenDomeNavigationTaskParameters(TaskParameters):
    """Task params."""

    modality: Modality = Field(
        default=Modality.AUDITORY, description="What the trial shows: the tone sweep (auditory), the grating (visual) or both (audiovisual)"
    )
    tone_pips: StimulusSound = Field(
        default=StimulusSound(), description="The tone sweep's pips (src/waveforms/all_waveforms.bin), each played at levelDbfs (open_rigs.core.task StimulusSound)"
    )
    reward_tone: RewardTone = Field(
        default=RewardTone(), description="The reward tone (src/waveforms/reward_tone.bin), played at levelDbfs (open_rigs.core.task RewardTone)"
    )
    background_noise: BackgroundNoise = Field(
        default=BackgroundNoise(), description="The lab's background white noise (rig.yml's speakerArray.backgroundNoise) on every speaker, from Ready at the level of the task file as launched (open_rigs.core.task BackgroundNoise)"
    )
    position_step_cm: int = Field(default=1, description="The distance run is rounded to this before it sets the frequency, in cm", ge=1)
    trial_structure: TrialStructure = Field(default=TrialStructure(), description="Timing of each trial")
    penalty: Penalty = Field(description="The timeout and penalty sound a penalised trial gets (open_rigs.core.task Penalty)")
    truncation: Truncation = Field(default=Truncation(), description="Where the stimulus switches off on a truncated trial")
    optogenetics: OptogeneticsConfig = Field(description="Zapit optogenetic stimulation")
    training_stages: list[TrainingStage] = Field(
        description="Training stage table, indexed by the TrainingStage setting from 0; a setting past the end stops the workflow at Go"
    )

    @model_validator(mode="after")
    def _laser_max_outlasts_the_trial(self):
        # The laser is meant to stay on until the outcome: the cap must be longer than the longest trial, a stage's
        # response window (responseWindowSeconds, counted from the first tone pip) plus laser_to_stimulus_seconds (zapit-v2)
        opto = self.optogenetics
        limits = [stage.response_window_seconds for stage in self.training_stages if stage.response_window_seconds is not None]
        if not opto.use_zapit or not limits:
            return self
        shortest = max(limits) + opto.laser_to_stimulus_seconds + LASER_MAX_MARGIN_SECONDS
        if opto.laser_max_seconds < shortest - 1e-9:
            raise ValueError(
                f"optogenetics.laser_max_seconds ({opto.laser_max_seconds} s) must be at least {shortest:g} s: the longest stage responseWindowSeconds "
                f"({max(limits):g} s) counts from the first tone pip, laser_to_stimulus_seconds ({opto.laser_to_stimulus_seconds} s) after onset"
            )
        return self

    @model_validator(mode="after")
    def _the_lead_fits(self):
        # TrialLogic.cs TrialRules.Create's same rules (Pip, 13 Nov 2026).
        self.optogenetics.check_lead_fits(self.trial_structure.iti_min_seconds)
        lead = self.optogenetics.laser_to_stimulus_seconds
        # A negative lead puts the trigger after the first pip, at most the stage's responseWindowSeconds (any stage here; Go checks the one chosen).
        after = max(stage.response_window_seconds or UNLIMITED_WINDOW_TRIGGER_AFTER_SECONDS for stage in self.training_stages)
        if -lead > after + 1e-9:
            raise ValueError(f"optogenetics.laser_to_stimulus_seconds ({lead} s) puts the trigger more than {after:g} s after the first pip, past every stage's limit")
        return self

    @model_validator(mode="after")
    def _the_next_trial_is_armed_in_time(self):
        # The next trial is armed once the trigger line falls, trigger_hold_seconds after the outcome; its trigger comes at the
        # soonest iti_min_seconds - laser_to_stimulus_seconds after that outcome, and Zapit's reply needs its margin before it.
        hold, iti, lead = self.optogenetics.trigger_hold_seconds, self.trial_structure.iti_min_seconds, self.optogenetics.laser_to_stimulus_seconds
        longest = iti - lead - ZAPIT_REPLY_MARGIN_SECONDS
        if self.optogenetics.use_zapit and hold > longest + 1e-9:
            raise ValueError(
                f"optogenetics.trigger_hold_seconds ({hold} s) must be at most {longest:g} s (trial_structure.iti_min_seconds - laser_to_stimulus_seconds - "
                f"{ZAPIT_REPLY_MARGIN_SECONDS} s): the next trial is armed once the line falls"
            )
        return self

    @model_validator(mode="after")
    def _zones_inside_sweep(self):
        made = sweep()
        for i, stage in enumerate(self.training_stages):
            if stage.reward_floor_hz <= made["sweepStartHz"] or stage.reward_ceiling_hz > made["sweepEndHz"]:
                raise ValueError(
                    f"training_stages[{i}]'s reward zone is not inside the sweep ({made['sweepStartHz']} to {made['sweepEndHz']} Hz, src/waveforms/sweep.json)"
                )
        return self

    @field_validator("training_stages")
    @classmethod
    def _at_least_one_stage(cls, value: list[TrainingStage]) -> list[TrainingStage]:
        if not value:
            raise ValueError("training_stages must have at least one entry")
        return value


class UclOpenCoenDomeNavigationTaskLogic(Task):
    """Top-level task logic schema."""

    version: Literal[__semver__] = __semver__
    name: Literal["UclOpenCoenDomeNavigation"] = Field(default="UclOpenCoenDomeNavigation", description="Name of the task")
    task_parameters: UclOpenCoenDomeNavigationTaskParameters
