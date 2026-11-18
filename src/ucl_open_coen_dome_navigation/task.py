"""The task file's schema: config/task-abstract-corridor.yml, which rig-control copies at launch and again at Go with the
form's edits; the workflow reads the copy (TaskPath) at Start for the background noise and at Go for the rest. Ported from Tom Childs' auditory-vr-corridor task.py.

The stage is a stage of the lab's Mouse Curriculum, recorded only as the task file's curriculum
(K:\\config\\curricula\\coen-dome-navigation\\task-abstract-corridor.conditions.yml): rig-control writes that stage's values
over this file's flat values at launch and fills curriculum (id, code, version, path, label, sha256); trialStructure holds the ITI and the track lengths, penalty the timeout and penalty
sound, and rngSeed seeds every draw. The sweep is src/sounds/sweep.json's, made with the tone files
(`uv run generate-waveforms`), not the task file's; every sound's level is the task file's (levelDbfs, in dBFS
RMS: auditoryStimulus, rewardTone, backgroundNoise and penalty.sound)."""

import json
from pathlib import Path
from typing import Any, Literal

# Integers are int: open_rigs' Int carries its own range, which drops a Field's ge or gt.
from open_rigs.core.base import Double, String
from open_rigs.core.task import (
    MAX_REWARD_UL,
    AutoReward,
    BackgroundNoise,
    Iti,
    LickQuietWait,
    Optogenetics,
    Penalty,
    ResponseWindow,
    RewardSize,
    RewardTone,
    StimulusSound,
    Task,
    TaskBlock,
    TaskParameters,
    WheelQuietWait,
)
from open_rigs.components.zapit import ZAPIT_MAX_RAMP_DOWN_SECONDS
from pydantic import ConfigDict, Field, field_validator, model_validator

from ucl_open_coen_dome_navigation import __semver__

STEP_SECONDS = 0.05  # the tone pips are 50 ms apart on the Harp clock (TrialLogic.cs StepSeconds)
LASER_MAX_MARGIN_SECONDS = 0.25  # how much longer than the longest laser (responseWindowSeconds + laser_to_stimulus_seconds) the laser_max_seconds cap must be
MAX_LEAD_SECONDS = 0.2  # the furthest the trigger may come before the first pip, in whole steps (TrialLogic.cs MaxLeadSeconds)
UNLIMITED_WINDOW_TRIGGER_AFTER_SECONDS = 2.0  # the furthest after the first pip the trigger may come on a stage without responseWindowSeconds (TrialLogic.cs)
MAX_WEIGHT_SUM = 2147483647  # the most trial_conditions' weights may add to: the draw sums them as an int (TrialLogic.cs int.MaxValue)

SWEEP_FILE = Path(__file__).resolve().parents[1] / "sounds" / "sweep.json"  # src/sounds/sweep.json


def sweep() -> dict:
    """The sweep the committed tone files were made for: sweepStartHz, sweepEndHz, sweepSteps, pipPeak and the waveforms' sampleRate."""
    return json.loads(SWEEP_FILE.read_text(encoding="utf-8"))


class TrialStructure(Iti, WheelQuietWait):
    """Timing of each trial. The inter-trial interval (open_rigs.core.task Iti) runs from the last trial's outcome (after any penalty timeout; trial 1: the first clock tick after Go) to this trial's first tone pip, with a quiet mouse. It includes the time to drink a reward (Tom Childs' fixed 2 s before his 0 or 1 s). The wheel quiet wait (open_rigs.core.task WheelQuietWait) is here, the same for every stage; the lick quiet wait is each stage's."""

    track_lengths_cm: list[Double] = Field(
        default=[80.0, 100.0, 120.0], min_length=1, description="Track lengths, one drawn per trial, in track cm (the mouse runs track cm / the stage's wheelGain of wheel)"
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


# The trial types the corridor shows (TrialDraw.TypeNames), in coen-dome-decision-making's names; its audiovisualConflict
# and neutral are not defined here yet.
TRIAL_TYPES = ("visual", "auditory", "audiovisualCoherent")
# The fields a condition may set (the schema's x-conditionFields, a list as coen-dome-decision-making's): none yet, so every
# condition's set and pairs are empty; nor any rules linking them (x-rules).
CONDITION_FIELDS: tuple[str, ...] = ()


class TrialCondition(TaskBlock):
    """One kind of trial in the mix: its label, its relative weight, its condition-level values (set) by dotted path and its
    pairs, coen-dome-decision-making's shape. The corridor has no condition fields yet (x-conditionFields is empty), so set and
    pairs are empty, and the label (one of TRIAL_TYPES) is what the corridor shows."""

    model_config = ConfigDict(extra="forbid")

    label: String = Field(description="What the trial shows, logged as trialType: visual (the grating alone), auditory (the tone sweep alone) or audiovisualCoherent (both); here it drives the draw, as no condition field does yet")
    weight: int = Field(ge=0, le=1_000_000, description="Relative draw weight: each new trial picks a condition with these as relative chances")
    pairs: list[list[String]] = Field(default_factory=list, description="Groups of x-conditionFields paths whose lists share one pick per trial; none in this task yet")
    set: dict[str, Any] = Field(default_factory=dict, description="Condition-level values by dotted path (x-conditionFields); none in this task yet")


class Curriculum(TaskBlock):
    """The Mouse Curriculum stage the values came from, and the task file's only record of the stage: filled by rig-control
    at launch; empty in the repo's task files. Bonsai only records it."""

    id: int = Field(ge=0, description="The stage's id in the curriculum, fixed and hidden: never shown to the experimenter")
    code: String = Field(pattern=r"^S\d{2}[A-Z]$", description="The stage's code in the curriculum, S then two digits then a capital letter (S03A)")
    version: String = Field(pattern=r"^[abcdefghjkmnpqrstuvwxyz23456789]{4}$", description="The stage's version, 4 characters from abcdefghjkmnpqrstuvwxyz23456789 (k7f2)")
    path: String = Field(pattern=r"^[A-Z]$", description="The stage's path in the curriculum, one capital letter (A)")
    label: String = Field(description="The stage's label in the curriculum")
    sha256: String = Field(description="The sha256 of the curriculum file's text rig-control ran it from")


class RewardSettings(RewardSize):
    """Reward delivery: the reward size on the left spout's valve (the lab's RewardSize block), as coen-dome-decision-making's reward block."""


class UclOpenCoenDomeNavigationTaskParameters(TaskParameters, LickQuietWait, ResponseWindow, AutoReward):
    """Task parameters for the coen-dome-navigation corridor: the reward, the trial mix, the sounds, the trial structure, the penalty, stimulus off, optogenetics and what a training stage sets (the flat values below, stage 0's by default; the lick quiet wait, response window and auto-reward are the lab's, open_rigs.core.task).

    A lick first counts from the trial's first tone pip, where the response window starts. Auto-reward is given on the pip whose tone reaches the reward zone's floor, no lick needed, and the trial's licks are ignored (outcomeReason FrequencyReached); on the other trials only a lick in the zone is rewarded. A trial that runs out of time or track with the tone in the zone, or ends outside the zone, is penalised. The reward is given on the left spout's valve, planned from the valve's calibration (calibration\\RewardValve-Left.json); an uncalibrated valve stops the workflow at Start."""

    model_config = ConfigDict(json_schema_extra={"x-conditionFields": list(CONDITION_FIELDS), "x-rules": []})

    reward: RewardSettings = Field(description="Reward delivery parameters")

    trial_conditions: list[TrialCondition] = Field(
        min_length=1,
        description=(
            "The trial mix: each new trial picks one condition at random with the weights as relative chances, independently of every other "
            "trial, and shows its label (visual, auditory or audiovisualCoherent; trialType in the trial row). One whole value: a curriculum "
            "stage's conditions replace it; the trialCondition column is its 0-based index"
        ),
        json_schema_extra={"x-whole": True},
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
    trial_structure: TrialStructure = Field(description="Timing of each trial")
    penalty: Penalty = Field(description="The timeout and penalty sound a penalised trial gets (open_rigs.core.task Penalty)")
    stimulus_off: StimulusOff = Field(default=StimulusOff(), description="Where and which stimuli switch off on a stimulus-off trial")
    optogenetics: OptogeneticsSettings = Field(description="Zapit optogenetic stimulation")
    auto_reward_probability: Double = Field(
        default=1.0, ge=0, le=1, description="Probability that a trial is an auto-reward trial, rewarded on reaching the zone's floor without a lick; 0: never, 1: every trial"
    )
    reward_floor_hz: Double = Field(default=18000, ge=1, description="Lower frequency of the reward zone, in Hz, above the sweep's start (src/sounds/sweep.json)")
    reward_ceiling_hz: Double = Field(default=25000, ge=1, description="Upper frequency of the reward zone, in Hz, at most the sweep's end (src/sounds/sweep.json)")
    wrong_lick_limit: int = Field(default=0, ge=0, description="Licks outside the zone that end the trial; 0: wrong licks never end it")
    wheel_gain: Double = Field(default=8.0, gt=0, description="Track cm per wheel cm: the track's tone and grating move this many cm for each cm the wheel turns, so the mouse runs track length / wheelGain cm of wheel to the end")
    auto_progress_per_second: Double = Field(
        default=0.04,
        ge=0,
        description="Fraction of the track the tone and grating advance per second on their own from the trial start, added to what the wheel sets; 0: none. Moves the stimulus only: the track's end and the stimulus-off points are judged on the wheel alone",
    )
    stimulus_off_probability: Double = Field(default=0.0, ge=0, le=1, description="Probability that a trial is a stimulus-off trial (stimulusOff): its listed stimuli switch off part way along the track; 0: never, 1: every trial")
    curriculum: Curriculum | None = Field(
        default=None,
        description="The Mouse Curriculum stage these values came from, the only record of the stage, filled by rig-control at launch; Go refuses a task whose curriculum is empty or malformed",
    )

    @model_validator(mode="after")
    def _conditions(self):
        # TrialDraw.CheckTask's same rules.
        for i, condition in enumerate(self.trial_conditions):
            where = f"trial_conditions[{i}] ({condition.label})"
            if condition.label not in TRIAL_TYPES:
                raise ValueError(f"{where}: its label is not one of {', '.join(TRIAL_TYPES)}")
            for path in [*condition.set, *(path for group in condition.pairs for path in group)]:
                if path not in CONDITION_FIELDS:
                    raise ValueError(f"{where} sets or pairs {path}, which is not a condition field (x-conditionFields)")
        if not sum(condition.weight for condition in self.trial_conditions) > 0:
            raise ValueError("trial_conditions needs a condition with a weight above 0")
        if sum(condition.weight for condition in self.trial_conditions) > MAX_WEIGHT_SUM:
            raise ValueError(f"trial_conditions' weights add to more than {MAX_WEIGHT_SUM}, the most the draw can sum")
        return self

    @model_validator(mode="after")
    def _zone_ordered(self):
        if self.reward_floor_hz >= self.reward_ceiling_hz:
            raise ValueError("reward_floor_hz must be below reward_ceiling_hz")
        return self

    @model_validator(mode="after")
    def _laser_max_outlasts_the_trial(self):
        # The laser is meant to stay on until the outcome: the cap must be longer than the longest trial, the
        # response window (responseWindowSeconds, counted from the first tone pip) plus laser_to_stimulus_seconds (zapit-v2)
        opto = self.optogenetics
        window = self.response_window_seconds
        if not opto.enabled or window is None:
            return self
        shortest = window + opto.laser_to_stimulus_seconds + LASER_MAX_MARGIN_SECONDS
        if opto.laser_max_seconds < shortest - 1e-9:
            raise ValueError(
                f"optogenetics.laser_max_seconds ({opto.laser_max_seconds} s) must be at least {shortest:g} s: responseWindowSeconds "
                f"({window:g} s) counts from the first tone pip, laser_to_stimulus_seconds ({opto.laser_to_stimulus_seconds} s) after onset"
            )
        return self

    @model_validator(mode="after")
    def _the_lead_fits(self):
        # TrialLogic.cs TrialRules.Create's same rules.
        self.optogenetics.check_lead_fits(self.trial_structure.iti_min_seconds)
        lead = self.optogenetics.laser_to_stimulus_seconds
        # A negative lead puts the trigger after the first pip, at most responseWindowSeconds.
        after = self.response_window_seconds or UNLIMITED_WINDOW_TRIGGER_AFTER_SECONDS
        if -lead > after + 1e-9:
            raise ValueError(f"optogenetics.laser_to_stimulus_seconds ({lead} s) puts the trigger more than {after:g} s after the first pip")
        return self

    @model_validator(mode="after")
    def _the_next_trial_is_armed_in_time(self):
        # The next trial is armed once the trigger line falls, trigger_hold_seconds after the outcome (TrialLoop's rule).
        self.optogenetics.check_zapit_free_in_time(self.trial_structure.iti_min_seconds, trigger_hold_seconds=self.optogenetics.trigger_hold_seconds)
        return self

    @model_validator(mode="after")
    def _zones_inside_sweep(self):
        made = sweep()
        if self.reward_floor_hz <= made["sweepStartHz"] or self.reward_ceiling_hz > made["sweepEndHz"]:
            raise ValueError(f"the reward zone is not inside the sweep ({made['sweepStartHz']} to {made['sweepEndHz']} Hz, src/sounds/sweep.json)")
        return self


class UclOpenCoenDomeNavigationTaskLogic(Task):
    """Top-level task logic schema."""

    version: Literal[__semver__] = __semver__
    name: Literal["UclOpenCoenDomeNavigation"] = Field(default="UclOpenCoenDomeNavigation", description="Name of the task")
    task_parameters: UclOpenCoenDomeNavigationTaskParameters
