"""The task file's schema: config/task-abstract-corridor.yml, which rig-control copies at launch and again at Go with the
form's edits; the workflow reads the copy (TaskPath) at Go. Ported from Tom Childs' auditory-vr-corridor task.py.

The stage is a stage of the lab's Mouse Curriculum, recorded only as the task file's curriculum
(K:\\config\\curricula\\coen-dome-navigation\\task-abstract-corridor.conditions.yml): rig-control writes that stage's values
over this file's flat values at launch and fills curriculum (id, code, version, path, label, sha256); trialStructure holds the
timing, track the track, penalty the timeout and penalty sound, and rngSeed seeds every draw. The sweep is src/sounds/sweep.json's,
made with the tone files (`uv run generate-waveforms`), not the task file's. Every sound plays at a level in dBFS RMS
(auditoryStimulus, rewardTone and penalty.sound), each a condition-level value; the background noise is the lab's, at its
constant level (open_rigs.core.task BACKGROUND_LEVEL_DBFS). The pips are stored at sweep.json's pipLevelDbfs.

What a trial shows and offers is its trial condition's combination: a condition may set any field but the session fields
(x-session-fields, SESSION_FIELDS), and every list field (LIST_FIELDS: the contrasts, the initial azimuths, the track lengths and
the stimulus-off fractions) expands into its combinations, one entry each; the draw reads every value per trial from the
trial's combination (its row of Settings_Combinations the trialCombinationId column). Its trialType, derived from its values
(TRIAL_TYPES), names it and drives nothing."""

import json
import math
from pathlib import Path
from typing import Literal

# Integers are int: open_rigs' Int carries its own range, which drops a Field's ge or gt.
from open_rigs.core.base import Double, String
from open_rigs.core.task import (
    MAX_REWARD_UL,
    Iti,
    LickQuietWait,
    Optogenetics,
    Penalty,
    ResponseWindow,
    RewardTone,
    SoundLevelDbfs,
    Task,
    TaskBlock,
    TaskParameters,
    WheelQuietWait,
    level_dbfs,
)
from open_rigs.rigs import conditioned_task as ct
from open_rigs.rigs.conditioned_task import ALL_SPEAKERS, SoundAzimuth
from pydantic import ConfigDict, Field, field_validator, model_validator

from ucl_open_coen_dome_navigation import __semver__

STEP_SECONDS = 0.05  # the tone pips are 50 ms apart on the Harp clock (TrialLogic.cs StepSeconds)
LASER_MAX_MARGIN_SECONDS = 0.25  # how much longer than the longest laser (responseWindowSeconds + laserToStimulusSeconds) the derived cap is (TrialLogic.cs LaserMaxMarginSeconds)
MAX_LEAD_SECONDS = 0.2  # the furthest the trigger may come before the first pip, in whole steps (TrialLogic.cs MaxLeadSeconds)
UNLIMITED_WINDOW_TRIGGER_AFTER_SECONDS = 2.0  # the furthest after the first pip the trigger may come on a stage without responseWindowSeconds (TrialLogic.cs)

SWEEP_FILE = Path(__file__).resolve().parents[1] / "sounds" / "sweep.json"  # src/sounds/sweep.json


def sweep() -> dict:
    """The sweep the committed tone files were made for: startHz, endHz, steps, pipLevelDbfs and the waveforms' sampleRate."""
    return json.loads(SWEEP_FILE.read_text(encoding="utf-8"))


def track_end_hz(made: dict) -> float:
    """The highest tone the track plays: startHz times endHz / startHz in whole numbers (Sweep.cs TrackEndHz, 24 kHz)."""
    return made["startHz"] * math.floor(made["endHz"] / made["startHz"])


class TrialStructure(Iti, ResponseWindow, LickQuietWait, WheelQuietWait):
    """Timing of each trial. The inter-trial interval (open_rigs.core.task Iti) runs from the last trial's outcome (after any
    penalty timeout; trial 1: the first clock tick after Go) to this trial's first tone pip, with a quiet mouse. It includes the
    time to drink a reward (Tom Childs' fixed 2 s before his 0 or 1 s). The response window counts from the first tone pip; null:
    it never closes (optogenetics then refused, as the laser's cap is derived from it). The quiet waits count from the interval's
    start (open_rigs.core.task LickQuietWait, WheelQuietWait)."""


class Track(TaskBlock):
    """The virtual track: its lengths, how far it moves per wheel cm and how fast it moves on its own."""

    lengths_cm: list[Double] = Field(
        default=[80.0, 100.0, 120.0], min_length=1, description="Track lengths, one drawn per trial, in track cm (the mouse runs track cm / wheelGain of wheel)"
    )
    wheel_gain: Double = Field(default=8.0, gt=0, description="Track cm per wheel cm: the track's tone and grating move this many cm for each cm the wheel turns, so the mouse runs track length / wheelGain cm of wheel to the end")
    auto_progress_per_second: Double = Field(
        default=0.04,
        ge=0,
        description="Fraction of the track the tone and grating advance per second on their own from the trial start, added to what the wheel sets; 0: none. Moves the stimulus only: the track's end and the stimulus-off points are judged on the wheel alone",
    )


class VisualStimulus(TaskBlock):
    """The grating: its contrast, and its spatial frequency from start to end along the track on a log scale, as the tone does
    (TrialLogic.cs TrialRules.Frame)."""

    contrasts: list[Double] = Field(
        default=[1.0], min_length=1, description="Candidate grating contrasts (BonVision's Contrast, 1 full contrast), one drawn per trial; 0 shows no grating"
    )
    spatial_frequency_start_cpd: Double = Field(default=0.03, gt=0, description="The grating's spatial frequency at the track's start, in cycles per degree")
    spatial_frequency_end_cpd: Double = Field(default=0.3, gt=0, description="The grating's spatial frequency at the track's end, in cycles per degree")

    @field_validator("contrasts")
    @classmethod
    def _not_negative(cls, value: list[float]) -> list[float]:
        if any(contrast < 0 for contrast in value):
            raise ValueError("contrasts must each be 0 or more")
        return value


class AuditoryStimulus(ct.AuditoryStimulus):
    """The tone sweep's pips (src/sounds/pips.bin), each equalised for its speaker: on every rig.yml speaker where the
    picked initial azimuth is "all", else on rig.yml's speaker nearest it at elevationDeg."""

    level_dbfs: SoundLevelDbfs = level_dbfs(-55.0, "The tone sweep's pips")
    initial_azimuths_deg: list[SoundAzimuth] = Field(
        default=[ALL_SPEAKERS],
        min_length=1,
        description=(
            "Where the pips play, one picked per trial: \"all\" on every rig.yml speaker at once (the corridor's sound, logged "
            "auditoryInitialAzimuthDeg NaN with a level); a signed azimuth in degrees (positive right, 0 the centre) on rig.yml's "
            "speaker nearest it at elevationDeg, logged as that speaker's auditoryInitialAzimuthDeg and auditoryElevationDeg. Unused where the level is mute"
        ),
    )


class StimulusOff(TaskBlock):
    """A stimulus-off trial: which stimuli switch off, and where (a condition sets stimuli to make its trials stimulus-off ones).

    Each listed stimulus switches off once the mouse has run a track fraction of the trial's track (the fraction one of
    trackFractions, a list field); an unlisted stimulus stays on. Everything else about the trial is unchanged, so it is still
    rewarded as normal: the animal has to keep going and find the reward zone without the stimulus.
    """

    stimuli: Literal["none", "auditory", "visual", "both"] = Field(
        default="none",
        description="The stimuli that switch off: none (an ordinary trial), auditory (the tone), visual (the grating) or both",
    )
    track_fractions: list[Double] = Field(
        default=[0.4, 0.6],
        min_length=1,
        description="Where the stimuli switch off, as a fraction of the trial's track length, one drawn per trial",
    )

    @field_validator("track_fractions")
    @classmethod
    def _fractions_inside(cls, value: list[float]) -> list[float]:
        if any(not 0 < fraction < 1 for fraction in value):
            raise ValueError("stimulusOff track fractions must be between 0 and 1")
        return value


class OptogeneticsSettings(Optogenetics):
    """Zapit optogenetic stimulation: the lab's block (open_rigs.core.task Optogenetics) and this task's laser timing.

    The stimulation sites and ramp-down are set in the Zapit conditions file loaded in Zapit, not here; Bonsai reads
    the same file to list its conditions and its longest ramp-down. "Stimulus" is the tone and grating; the laser is
    continuous: it starts at the trigger (the trigger line's rising edge on a hardware-triggered rig), laser_to_stimulus_seconds
    before the first tone pip (after it if negative), in whole 50 ms steps and at most 0.2 s, and Bonsai stops it at the
    trial's outcome, or at a cap derived at Go (the longest responseWindowSeconds any condition can draw plus
    laser_to_stimulus_seconds plus 0.25 s) if that comes first. The trigger line, and the masking light on it, stays high past
    the outcome for the conditions file's longest ramp-down. A trial that ends before its trigger has no laser.
    """

    @model_validator(mode="after")
    def validate_lead_time(self):
        # The trigger and the first pip fall on steps, so the lead is a whole number of steps, at most MAX_LEAD_SECONDS
        steps = self.laser_to_stimulus_seconds / STEP_SECONDS
        if abs(steps - round(steps)) > 1e-6:
            raise ValueError(f"laser_to_stimulus_seconds ({self.laser_to_stimulus_seconds}) must be a multiple of the {STEP_SECONDS} s step interval")
        if self.laser_to_stimulus_seconds > MAX_LEAD_SECONDS + 1e-9:
            raise ValueError(f"laser_to_stimulus_seconds ({self.laser_to_stimulus_seconds}) must be at most {MAX_LEAD_SECONDS} s")
        return self


# The session fields (the schema's x-session-fields; TaskSchema.SessionFields, generated): what cannot change per trial, so
# a condition may not set it (the path or any path under it). A condition may set every other field: the draw reads it per
# trial from the trial's combination. A condition's blank takes the stage's, then the task's value.
_COMMON = ct.COMMON_SESSION_FIELDS
# The lab's (ct.COMMON_SESSION_FIELDS); this task adds none: every sound's level is condition-level.
SESSION_FIELDS = dict(_COMMON)


# Where one setting limits another (the schema's x-rules, JSON Schema over dotted paths; open_rigs.rigs.conditions
# evaluates them), as coen-dome-decision-making's. No corridor setting limits another, so its rules only say what goes unread.
RULES = (
    {"if": ct.if_({"visualStimulus.contrasts": {"items": {"const": 0}}}), "unused": ["visualStimulus.spatialFrequencyStartCpd", "visualStimulus.spatialFrequencyEndCpd"],
     "message": "contrasts 0 shows no grating, so its spatial frequencies are unread"},
    {"if": ct.if_({"auditoryStimulus.levelDbfs": {"const": "mute"}}), "unused": ["auditoryStimulus.initialAzimuthsDeg", "auditoryStimulus.elevationDeg"],
     "message": "auditoryStimulus.levelDbfs mute plays no tone, so where it plays is unread"},
    {"if": ct.if_({"autoRewardProbability": {"const": 1}}), "unused": ["wrongLickLimit", "rewardZoneMaxHz"],
     "message": "autoRewardProbability 1 rewards every trial, so the lick limits are unread"},
    {"if": ct.if_({"stimulusOff.stimuli": {"const": "none"}}), "unused": ["stimulusOff.trackFractions"],
     "message": "stimulusOff.stimuli none switches nothing off, so its track fractions are unread"},
)
# How a condition is named (the schema's x-trial-types; derive_trial_type, the C#'s TrialTypes.Derive at Go), logged as
# trialType: it names rows and analysis groups and drives nothing. coen-dome-decision-making's words without its targets or
# azimuths (so no audiovisualConflict: the corridor has no side for the stimuli to disagree on): first match wins, on the
# condition's values each as a list. The grating shown (a contrast above 0) and the tone heard (a level, not mute): both
# audiovisualCoherent, one alone visual or auditory, neither neutral.
TRIAL_TYPES = (
    {"trialType": "audiovisualCoherent", "if": ct.if_({"visualStimulus.contrasts": ct.ABOVE_0, "auditoryStimulus.levelDbfs": ct.SOUNDING})},
    {"trialType": "visual", "if": ct.if_({"visualStimulus.contrasts": ct.ABOVE_0})},
    {"trialType": "auditory", "if": ct.if_({"auditoryStimulus.levelDbfs": ct.SOUNDING})},
    {"trialType": "neutral"},
)
# What some weighted condition must draw (the schema's x-required-conditions; open_rigs.rigs.conditions.required_problems):
# nothing, as the corridor has no target.
REQUIRED = ()


class TrialCondition(ct.TrialCondition):
    """One kind of trial in the mix (coen-dome-decision-making's shape): its relative weight, its condition-level values (set)
    by dotted path and the lists it pairs; its trialType is derived from its contrasts and level (TRIAL_TYPES), never stored.
    A list in a scalar field's cell is one value picked per trial; a list field's list (LIST_FIELDS), at any level, one entry
    picked per trial. Paired lists share one picked index per trial."""

    pairs: list[list[String]] = Field(
        default_factory=list,
        description="Groups of paths whose lists (the set's, else the task's) have equal length and share one picked index per trial; unpaired lists are picked independently",
    )


class Curriculum(ct.Curriculum):
    """The Mouse Curriculum stage the values came from, and the task file's only record of the stage: filled by rig-control
    at launch; empty in the repo's task files. Bonsai only records it."""

    label: String = Field(description="The stage's label in the curriculum")
    sha256: String = Field(description="The sha256 of the curriculum file's text rig-control ran it from")


class Reward(ct.Reward):
    """Reward delivery on the left spout's valve: the size, and the large-reward trials (the lab's reward block)."""


class UclOpenCoenDomeNavigationTaskParameters(TaskParameters, ct.ConditionedTask):
    """Task parameters for the coen-dome-navigation corridor: the lab's blocks in the lab's order (the mix, timing, stimuli,
    reward, penalty, sounds, optogenetics), then the track, the reward zone, the wrong-lick limit and stimulus off.

    A lick first counts from the trial's first tone pip, where the response window starts. Auto-reward is given on the pip whose tone reaches the reward zone's floor (rewardZoneMinHz), no lick needed, and the trial's licks are ignored (outcomeReason RewardZoneReached); on the other trials only a lick in the zone is rewarded. A trial that runs out of time or track with the tone in the zone, or ends outside the zone, is penalised. The reward is given on the left spout's valve, planned from the valve's calibration (calibration\\RewardValve-Left.json); an uncalibrated valve stops the workflow at Start."""

    model_config = ConfigDict(json_schema_extra=ct.schema_extra(SESSION_FIELDS, RULES, TRIAL_TYPES, REQUIRED))
    SESSION_FIELDS = SESSION_FIELDS
    RULES = RULES
    TRIAL_TYPES = TRIAL_TYPES
    REQUIRED = REQUIRED

    curriculum: Curriculum | None = Field(
        default=None,
        description="The Mouse Curriculum stage these values came from, the only record of the stage, filled by rig-control at launch; Go refuses a task whose curriculum is empty or malformed",
    )
    trial_conditions: list[TrialCondition] = Field(
        min_length=1,
        description=(
            "The trial mix: each new trial picks one condition at random with the weights as relative chances, independently of every other "
            "trial, then one of its combinations: the task's values with the condition's set over them, any field but x-session-fields', each list field one entry; "
            "its trialType, derived from its values (x-trial-types), is the trial row's. One whole value: a curriculum stage's conditions replace it; the trialConditionId column is its 0-based index"
        ),
        json_schema_extra={"x-whole": True},
    )
    trial_structure: TrialStructure = Field(description="Timing of each trial")
    visual_stimulus: VisualStimulus = Field(description="The grating: its contrasts and spatial frequencies along the track")
    auditory_stimulus: AuditoryStimulus = Field(description="The tone sweep's pips (src/sounds/pips.bin), each played at levelDbfs")
    reward: Reward = Field(description="Reward delivery parameters")
    auto_reward_probability: Double = Field(
        default=1.0, ge=0, le=1, description="Probability that a trial is an auto-reward trial, rewarded on reaching the zone's floor without a lick; 0: never, 1: every trial"
    )
    penalty: Penalty = Field(description="The timeout and penalty sound a penalised trial gets (open_rigs.core.task Penalty)")
    reward_tone: RewardTone = Field(
        default=RewardTone(), description="The reward tone (src/sounds/reward_tone.bin), played on a rewarded trial at its levelDbfs (open_rigs.core.task RewardTone, OpenRigs.Audio PlayRewardTone)"
    )
    optogenetics: OptogeneticsSettings = Field(description="Zapit optogenetic stimulation")
    track: Track = Field(default=Track(), description="The virtual track")
    reward_zone_min_hz: Double = Field(default=18000, ge=1, description="Lower frequency of the reward zone, in Hz, above the sweep's start (src/sounds/sweep.json)")
    reward_zone_max_hz: Double = Field(default=24000, ge=1, description="Upper frequency of the reward zone, in Hz, at most the track's top (24 kHz: the sweep's start times endHz / startHz in whole numbers, src/sounds/sweep.json)")
    wrong_lick_limit: int = Field(default=0, ge=0, description="Licks outside the zone that end the trial; 0: wrong licks never end it")
    stimulus_off: StimulusOff = Field(default=StimulusOff(), description="Which stimuli switch off part way along the track, and where")

    @model_validator(mode="after")
    def _zone_ordered(self):
        if self.reward_zone_min_hz >= self.reward_zone_max_hz:
            raise ValueError("reward_zone_min_hz must be below reward_zone_max_hz")
        return self

    @model_validator(mode="after")
    def _large_reward_fits(self):
        reward = self.reward
        if reward.large_probability > 0 and reward.size_ul * reward.large_multiplier > MAX_REWARD_UL + 1e-9:
            raise ValueError(
                f"reward.size_ul ({reward.size_ul} ul) x reward.large_multiplier ({reward.large_multiplier}) is more than {MAX_REWARD_UL} ul, the most one reward may ask for"
            )
        return self

    @model_validator(mode="after")
    def _the_laser_has_a_cap(self):
        # The laser stops at the outcome, capped at responseWindowSeconds + laser_to_stimulus_seconds + 0.25 s (TrialRules'
        # LaserMaxSeconds, derived at Go): with no response window there is no cap to derive.
        if self.optogenetics.enabled and self.trial_structure.response_window_seconds is None:
            raise ValueError("optogenetics needs trial_structure.response_window_seconds: the laser's cap is derived from it")
        return self

    @model_validator(mode="after")
    def _the_lead_fits(self):
        # TrialLogic.cs TrialDraw.Check's same rules.
        self.optogenetics.check_lead_fits(self.trial_structure.iti_min_seconds)
        lead = self.optogenetics.laser_to_stimulus_seconds
        # A negative lead puts the trigger after the first pip, at most responseWindowSeconds.
        after = self.trial_structure.response_window_seconds or UNLIMITED_WINDOW_TRIGGER_AFTER_SECONDS
        if -lead > after + 1e-9:
            raise ValueError(f"optogenetics.laser_to_stimulus_seconds ({lead} s) puts the trigger more than {after:g} s after the first pip")
        return self

    @model_validator(mode="after")
    def _zones_inside_the_track(self):
        made = sweep()
        top = track_end_hz(made)
        if self.reward_zone_min_hz <= made["startHz"] or self.reward_zone_max_hz > top:
            raise ValueError(f"the reward zone is not inside the track's tones ({made['startHz']} to {top:g} Hz, src/sounds/sweep.json)")
        return self


# The list fields one entry of which each trial draws (open_rigs.rigs.conditions.pick_lists).
LIST_FIELDS = UclOpenCoenDomeNavigationTaskParameters.list_fields()


class UclOpenCoenDomeNavigationTaskLogic(Task):
    """Task logic definition for the coen-dome-navigation experiment."""

    version: Literal[__semver__] = __semver__
    name: Literal["UclOpenCoenDomeNavigation"] = Field(default="UclOpenCoenDomeNavigation", description="Name of the task logic", frozen=True)
    task_parameters: UclOpenCoenDomeNavigationTaskParameters = Field(description="Parameters of the task logic")
