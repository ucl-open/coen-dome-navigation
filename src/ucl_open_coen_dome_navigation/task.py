"""The task file's schema: config/task-abstract-corridor.yml, which rig-control copies at launch and again at Go with the
form's edits; the workflow reads the copy (TaskPath) at Start for the background noise and at Go for the rest. Ported from Tom Childs' auditory-vr-corridor task.py.

The stage is the TrainingStage experiment setting, a row of trainingStages from 0, and each row
says what the stage does; trialStructure holds the ITI and the track lengths, penalty the timeout and penalty
sound, and rngSeed seeds every draw. The sweep is src/sounds/sweep.json's, made with the tone files
(`uv run generate-waveforms`), not the task file's; every sound's level is the task file's (levelDbfs, in dBFS
RMS: auditoryStimulus, rewardTone, backgroundNoise and penalty.sound)."""

import json
from pathlib import Path
from typing import Literal

# Integers are int: open_rigs' Int carries its own range, which drops a Field's ge or gt.
from open_rigs.core.base import Bool, Double, String
from open_rigs.core.task import (
    MAX_REWARD_UL,
    AutoReward,
    BackgroundNoise,
    Iti,
    Optogenetics,
    Penalty,
    QuietWait,
    ResponseWindow,
    RewardSize,
    RewardTone,
    StimulusSound,
    Task,
    TaskBlock,
    TaskParameters,
)
from open_rigs.components.zapit import ZAPIT_MAX_RAMP_DOWN_SECONDS
from pydantic import Field, field_validator, model_validator

from ucl_open_coen_dome_navigation import __semver__

STEP_SECONDS = 0.05  # the tone pips are 50 ms apart on the Harp clock (TrialLogic.cs StepSeconds)
LASER_MAX_MARGIN_SECONDS = 0.25  # how much longer than the longest laser (responseWindowSeconds + laser_to_stimulus_seconds) the laser_max_seconds cap must be
MAX_LEAD_SECONDS = 0.2  # the furthest the trigger may come before the first pip, in whole steps (TrialLogic.cs MaxLeadSeconds)
UNLIMITED_WINDOW_TRIGGER_AFTER_SECONDS = 2.0  # the furthest after the first pip the trigger may come on a stage without responseWindowSeconds (TrialLogic.cs)

SWEEP_FILE = Path(__file__).resolve().parents[1] / "sounds" / "sweep.json"  # src/sounds/sweep.json


def sweep() -> dict:
    """The sweep the committed tone files were made for: sweepStartHz, sweepEndHz, sweepSteps, pipPeak and the waveforms' sampleRate."""
    return json.loads(SWEEP_FILE.read_text(encoding="utf-8"))


class TrialStructure(Iti):
    """Timing of each trial. The inter-trial interval (open_rigs.core.task Iti) runs from the last trial's outcome (after any penalty timeout; trial 1: the first clock tick after Go) to this trial's first tone pip, with a quiet mouse. It includes the time to drink a reward (Tom Childs' fixed 2 s before his 0 or 1 s)."""

    track_lengths_cm: list[Double] = Field(
        default=[80.0, 100.0, 120.0], min_length=1, description="Track lengths, one drawn per trial and scaled by the stage's trackLengthScale, in cm"
    )


class VisualStimulus(TaskBlock):
    """The grating: its spatial frequency runs from start to end along the track on a log scale, as the tone does (TrialLogic.cs TrialRules.Frame)."""

    spatial_frequency_start_cpd: Double = Field(default=0.03, gt=0, description="The grating's spatial frequency at the track's start, in cycles per degree")
    spatial_frequency_end_cpd: Double = Field(default=0.3, gt=0, description="The grating's spatial frequency at the track's end, in cycles per degree")


class StimulusOff(TaskBlock):
    """Where the stimulus switches off on a stimulus-off trial (a stage's stimulusOffProbability says how often).

    On a stimulus-off trial each listed stimulus switches off once the mouse has run a track fraction of the trial's
    track (position / trackLengthCm); an unlisted stimulus stays on. Everything else about the trial is unchanged, so
    it is still rewarded as normal: the animal has to keep going and find the reward zone without the stimulus.
    """

    track_fractions: list[Double] = Field(
        default=[0.4, 0.6],
        min_length=1,
        description="Where the stimulus switches off, as a fraction of the trial's track length, split evenly over stimulus-off trials",
    )
    stimuli: list[Literal["auditory", "visual"]] = Field(
        default=["auditory", "visual"],
        min_length=1,
        description="The stimuli that switch off on a stimulus-off trial: auditory (the tone), visual (the grating) or both",
    )

    @field_validator("track_fractions")
    @classmethod
    def _fractions_inside(cls, value: list[float]) -> list[float]:
        if any(not 0 < fraction < 1 for fraction in value):
            raise ValueError("stimulusOff track fractions must be between 0 and 1")
        return value

    @field_validator("stimuli")
    @classmethod
    def _stimuli_unique(cls, value: list[str]) -> list[str]:
        if len(set(value)) != len(value):
            raise ValueError("stimulusOff stimuli must not repeat")
        return value


class OptogeneticsSettings(Optogenetics):
    """Zapit optogenetic stimulation: the lab's block (open_rigs.core.task Optogenetics) and this task's laser timing.

    The stimulation sites and ramp-down are set in the Zapit conditions file loaded in Zapit, not here; Bonsai reads
    the same file to list its conditions. "Stimulus" is the tone and grating; the laser is continuous: it starts at the
    trigger (the trigger line's rising edge on a hardware-triggered rig), laser_to_stimulus_seconds before the first
    tone pip (after it if negative), in whole 50 ms steps and at most 0.2 s, and Bonsai stops it at the trial's
    outcome, or after laser_max_seconds if that comes first. A trial that ends before its trigger has no laser.
    """

    laser_max_seconds: Double = Field(
        default=12.5,
        gt=0,
        description="Longest the laser stays on in one trial, in seconds from the trigger, timed by Bonsai; it then ramps down over the condition's offRampDownDuration_ms. A safety cap: the laser normally stops at the trial's outcome. Timed on the Harp clock, with a wall-clock backstop should the board stall. With optogenetics on it must be at least the longest stage responseWindowSeconds + laser_to_stimulus_seconds + 0.25 s (the laser runs from the first pip less laser_to_stimulus_seconds to at most the first pip plus responseWindowSeconds), so it never ends the laser before the response window closes; on a stage without responseWindowSeconds a longer trial loses its laser before the outcome",
    )
    trigger_hold_seconds: Double = Field(
        default=1.0,
        ge=ZAPIT_MAX_RAMP_DOWN_SECONDS,
        description=f"How long the trigger line, and the masking light on it, stays high after the outcome, in seconds, on every trial of an optogenetics experiment (the line stays low without optogenetics): at least {ZAPIT_MAX_RAMP_DOWN_SECONDS}, the longest ramp-down a conditions file may have, and at most trialStructure.itiMinSeconds - laser_to_stimulus_seconds - 0.2 s, since the next trial is armed once the line falls and Zapit's reply needs 0.2 s before its trigger",
    )

    @model_validator(mode="after")
    def validate_lead_time(self):
        # The trigger and the first pip fall on steps, so the lead is a whole number of steps, at most MAX_LEAD_SECONDS
        steps = self.laser_to_stimulus_seconds / STEP_SECONDS
        if abs(steps - round(steps)) > 1e-6:
            raise ValueError(f"laser_to_stimulus_seconds ({self.laser_to_stimulus_seconds}) must be a multiple of the {STEP_SECONDS} s step interval")
        if self.laser_to_stimulus_seconds > MAX_LEAD_SECONDS + 1e-9:
            raise ValueError(f"laser_to_stimulus_seconds ({self.laser_to_stimulus_seconds}) must be at most {MAX_LEAD_SECONDS} s")
        return self


UNDEFINED_TYPES = ("audiovisual_conflict", "neutral")  # decision-making's trial types the corridor has no trial for yet


class TrialTypeWeights(TaskBlock):
    """Relative draw weight of each trial type, by the name the trial log gives it (trialType: what the trial showed).
    audiovisualCoherent, audiovisualConflict and neutral are coen-dome-decision-making's names, kept so one analysis reads both tasks."""

    visual: int = Field(ge=0, description="The grating alone")
    auditory: int = Field(ge=0, description="The tone sweep alone")
    audiovisual_coherent: int = Field(ge=0, description="The tone sweep and the grating together")
    audiovisual_conflict: int = Field(ge=0, description="Not defined in this task yet: must be 0")
    neutral: int = Field(ge=0, description="Not defined in this task yet: must be 0")

    @model_validator(mode="after")
    def _some_defined_weight(self) -> "TrialTypeWeights":
        for name in UNDEFINED_TYPES:
            if getattr(self, name) > 0:
                raise ValueError(f"trial_weights.{name} must be 0: that trial is not defined in this task yet")
        if not any(self.model_dump().values()):
            raise ValueError("trial_weights needs a trial type with a weight above 0")
        return self


class TrainingStage(QuietWait, ResponseWindow, AutoReward, RewardSize):
    """One row of the training stage table: what the stage does. Its quiet waits, response window, auto-reward and reward size are the lab's (open_rigs.core.task). Rows are selected by the TrainingStage setting.

    A lick first counts from the trial's first tone pip, where the response window starts. Auto-reward is given on the pip whose tone reaches the reward zone's floor, no lick needed, and the trial's licks are ignored (outcomeReason FrequencyReached); on the other trials only a lick in the zone is rewarded. A trial that runs out of time or track with the tone in the zone, or ends outside the zone, is penalised. The reward is given on the left spout's valve, planned from the valve's calibration (calibration\RewardValve-Left.json); an uncalibrated valve stops the workflow at Start."""

    label: String | None = Field(default=None, description="The stage's name, shown on Setup after its number")
    trial_weights: TrialTypeWeights | None = Field(
        default=None,
        description="This stage's own trialWeights, replacing the task's (the mouse's) for this stage; null: the task's trialWeights apply",
    )
    reward_floor_hz: Double = Field(ge=1, description="Lower frequency of the reward zone, in Hz, above the sweep's start (src/sounds/sweep.json)")
    reward_ceiling_hz: Double = Field(ge=1, description="Upper frequency of the reward zone, in Hz, at most the sweep's end (src/sounds/sweep.json)")
    wrong_lick_limit: int = Field(ge=0, description="Licks outside the zone that end the trial; 0: wrong licks never end it")
    track_length_scale: Double = Field(gt=0, description="The drawn track length is multiplied by this")
    frequency_drift: Bool = Field(description="Whether the frequency also drifts up with time, not only with distance run")
    stimulus_off_probability: Double = Field(ge=0, le=1, description="Probability that a trial is a stimulus-off trial (stimulusOff): its listed stimuli switch off part way along the track; 0: never, 1: every trial")

    @model_validator(mode="after")
    def _zone_ordered(self) -> "TrainingStage":
        if self.reward_floor_hz >= self.reward_ceiling_hz:
            raise ValueError("reward_floor_hz must be below reward_ceiling_hz")
        return self


class UclOpenCoenDomeNavigationTaskParameters(TaskParameters):
    """Task parameters for the coen-dome-navigation corridor: the trial mix, the sounds, the trial structure, the penalty, stimulus off, optogenetics and the training stages."""

    trial_weights: TrialTypeWeights = Field(
        default=TrialTypeWeights(visual=0, auditory=1, audiovisual_coherent=0, audiovisual_conflict=0, neutral=0),
        description="Relative draw weight per trial type, set per mouse and kept from experiment to experiment: each trial draws what it shows at random with these as relative chances, independently of every other trial (trialType in the trial row). A stage with its own trialWeights uses those instead",
    )
    auditory_stimulus: StimulusSound = Field(
        default=StimulusSound(), description="The tone sweep's pips (src/sounds/all_waveforms.bin), each played at levelDbfs (open_rigs.core.task StimulusSound)"
    )
    reward_tone: RewardTone = Field(
        default=RewardTone(), description="The reward tone (src/sounds/reward_tone.bin), played at levelDbfs (open_rigs.core.task RewardTone)"
    )
    background_noise: BackgroundNoise = Field(
        default=BackgroundNoise(), description="The lab's background white noise (rig.yml's speakerArray.backgroundNoise) on every speaker, from Ready at the level of the task file as launched (open_rigs.core.task BackgroundNoise)"
    )
    visual_stimulus: VisualStimulus = Field(default=VisualStimulus(), description="The grating's spatial frequencies along the track")
    position_step_cm: int = Field(default=1, description="The distance run is rounded to this before it sets the frequency, in cm", ge=1)
    drift_seconds: Double = Field(
        default=25.0,
        gt=0,
        description="On a stage with frequencyDrift, the time from the trial start over which the tone and grating drift on their own from the sweep's start to its end, in seconds, added to what the distance run sets. One value for every drift stage, as there is one sweep",
    )
    trial_structure: TrialStructure = Field(description="Timing of each trial")
    penalty: Penalty = Field(description="The timeout and penalty sound a penalised trial gets (open_rigs.core.task Penalty)")
    stimulus_off: StimulusOff = Field(default=StimulusOff(), description="Where and which stimuli switch off on a stimulus-off trial")
    optogenetics: OptogeneticsSettings = Field(description="Zapit optogenetic stimulation")
    training_stages: list[TrainingStage] = Field(
        description="Training stage table, indexed by the TrainingStage setting from 0; a setting past the end stops the workflow at Go"
    )

    @model_validator(mode="after")
    def _laser_max_outlasts_the_trial(self):
        # The laser is meant to stay on until the outcome: the cap must be longer than the longest trial, a stage's
        # response window (responseWindowSeconds, counted from the first tone pip) plus laser_to_stimulus_seconds (zapit-v2)
        opto = self.optogenetics
        limits = [stage.response_window_seconds for stage in self.training_stages if stage.response_window_seconds is not None]
        if not opto.enabled or not limits:
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
        # TrialLogic.cs TrialRules.Create's same rules.
        self.optogenetics.check_lead_fits(self.trial_structure.iti_min_seconds)
        lead = self.optogenetics.laser_to_stimulus_seconds
        # A negative lead puts the trigger after the first pip, at most the stage's responseWindowSeconds (any stage here; Go checks the one chosen).
        after = max(stage.response_window_seconds or UNLIMITED_WINDOW_TRIGGER_AFTER_SECONDS for stage in self.training_stages)
        if -lead > after + 1e-9:
            raise ValueError(f"optogenetics.laser_to_stimulus_seconds ({lead} s) puts the trigger more than {after:g} s after the first pip, past every stage's limit")
        return self

    @model_validator(mode="after")
    def _the_next_trial_is_armed_in_time(self):
        # The next trial is armed once the trigger line falls, trigger_hold_seconds after the outcome (TrialLoop's rule).
        self.optogenetics.check_zapit_free_in_time(self.trial_structure.iti_min_seconds, trigger_hold_seconds=self.optogenetics.trigger_hold_seconds)
        return self

    @model_validator(mode="after")
    def _zones_inside_sweep(self):
        made = sweep()
        for i, stage in enumerate(self.training_stages):
            if stage.reward_floor_hz <= made["sweepStartHz"] or stage.reward_ceiling_hz > made["sweepEndHz"]:
                raise ValueError(
                    f"training_stages[{i}]'s reward zone is not inside the sweep ({made['sweepStartHz']} to {made['sweepEndHz']} Hz, src/sounds/sweep.json)"
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
