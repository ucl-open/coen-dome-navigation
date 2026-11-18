"""The task file's schema: config/task-abstract-corridor.yml, which rig-control copies at launch and again at Go with the
form's edits; the workflow reads the copy (TaskPath) at Start for the background noise and at Go for the rest. Ported from Tom Childs' auditory-vr-corridor task.py.

The stage is a stage of the lab's Mouse Curriculum, recorded only as the task file's curriculum
(K:\\config\\curricula\\coen-dome-navigation\\task-abstract-corridor.conditions.yml): rig-control writes that stage's values
over this file's flat values at launch and fills curriculum (id, code, version, path, label, sha256); trialStructure holds the ITI and the track lengths, penalty the timeout and penalty
sound, and rngSeed seeds every draw. The sweep is src/sounds/sweep.json's, made with the tone files
(`uv run generate-waveforms`), not the task file's. The pips play at auditoryStimulus.amplitude (1 the files as stored,
-58.1 dBFS RMS); the other sounds at the task file's levelDbfs (dBFS RMS: rewardTone, backgroundNoise and penalty.sound).

What a trial shows and offers is its trial condition's combination: a condition may set any field but the session fields
(x-session-fields, SESSION_FIELDS), and every list field (LIST_FIELDS: contrasts, the initial azimuths, the track lengths and
the stimulus-off fractions) expands into its combinations, one entry each; the draw reads every value per trial from the
trial's combination (its row of Settings_Combinations the trialCombinationId column). Its trialType names it and drives nothing."""

import copy
import json
import math
from enum import StrEnum
from pathlib import Path
from typing import Annotated, Any, Literal

# Integers are int: open_rigs' Int carries its own range, which drops a Field's ge or gt.
from open_rigs.core.base import Double, String
from open_rigs.core.task import (
    AutoReward,
    BackgroundNoise,
    Iti,
    LickQuietWait,
    Optogenetics,
    Penalty,
    ResponseWindow,
    RewardSize,
    RewardTone,
    Task,
    TaskBlock,
    TaskParameters,
    WheelQuietWait,
)
from open_rigs.components.zapit import ZAPIT_MAX_RAMP_DOWN_SECONDS
from open_rigs.rigs import conditions
from pydantic import ConfigDict, Field, ValidationError, ValidationInfo, WithJsonSchema, field_validator, model_validator

from ucl_open_coen_dome_navigation import __semver__

STEP_SECONDS = 0.05  # the tone pips are 50 ms apart on the Harp clock (TrialLogic.cs StepSeconds)
LASER_MAX_MARGIN_SECONDS = 0.25  # how much longer than the longest laser (responseWindowSeconds + laser_to_stimulus_seconds) the laser_max_seconds cap must be
MAX_LEAD_SECONDS = 0.2  # the furthest the trigger may come before the first pip, in whole steps (TrialLogic.cs MaxLeadSeconds)
UNLIMITED_WINDOW_TRIGGER_AFTER_SECONDS = 2.0  # the furthest after the first pip the trigger may come on a stage without responseWindowSeconds (TrialLogic.cs)
MAX_WEIGHT_SUM = 2147483647  # the most trial_conditions' weights may add to: the draw sums them as an int (TrialLogic.cs int.MaxValue)

SWEEP_FILE = Path(__file__).resolve().parents[1] / "sounds" / "sweep.json"  # src/sounds/sweep.json


def sweep() -> dict:
    """The sweep the committed tone files were made for: sweepStartHz, sweepEndHz, sweepSteps, pipLevelDbfs and the waveforms' sampleRate."""
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


ALL_SPEAKERS = "all"  # an auditoryStimulus.initialAzimuthsDeg item: every rig.yml speaker plays (TrialDraw.AllSpeakers)
# A sound azimuth or "all", as coen-dome-decision-making's. oneOf, not pydantic's anyOf: Bonsai.Sgen makes an empty class of
# an anyOf item, object of a oneOf.
SoundAzimuth = Annotated[Double | Literal["all"], WithJsonSchema({"oneOf": [{"type": "number"}, {"const": ALL_SPEAKERS, "type": "string"}]})]


class AuditoryStimulus(TaskBlock):
    """The tone sweep's pips (src/sounds/all_waveforms.bin), each equalised for its speaker: on every rig.yml speaker where the
    picked initial azimuth is "all", else on rig.yml's speaker nearest it (at elevation 0)."""

    amplitude: Double = Field(
        default=1.0,
        ge=0,
        description=(
            "Scale of the pips' samples as stored: 0 no tone, 1 the files as stored (-58.1 dBFS RMS, the loudness the pips have always "
            "played at), above 1 louder. Go refuses the largest amplitude any condition can draw if a pip would peak at -1 dBFS or more "
            "after equalisation on any speaker; dB SPL is unknown until the speakers' SPL calibration"
        ),
    )
    initial_azimuths_deg: list[SoundAzimuth] = Field(
        default=[ALL_SPEAKERS],
        min_length=1,
        description=(
            "Where the pips play, one picked per trial: \"all\" on every rig.yml speaker at once (the corridor's sound, logged "
            "auditoryAzimuthDeg NaN with an amplitude above 0); a signed azimuth in degrees (positive right, 0 the centre) on rig.yml's "
            "speaker nearest it at elevation 0, logged as that speaker's auditoryAzimuthDeg and auditoryElevationDeg. Unused where the amplitude is 0"
        ),
    )

    @field_validator("initial_azimuths_deg")
    @classmethod
    def _in_range(cls, value: list) -> list:
        # TrialDraw.CheckTask's same rule.
        for azimuth in value:
            if azimuth != ALL_SPEAKERS and not -180 <= azimuth <= 180:
                raise ValueError(f"initial_azimuths_deg value {azimuth} is outside [-180, 180]")
        return value


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
        json_schema_extra={"x-whole": True},
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


class TrialType(StrEnum):
    """A condition's name, logged as trialType: it names rows and analysis groups and drives nothing (derive_trial_type);
    coen-dome-decision-making's words."""

    VISUAL = "visual"
    AUDITORY = "auditory"
    AUDIOVISUAL_COHERENT = "audiovisualCoherent"
    NEUTRAL = "neutral"


# The session fields (the schema's x-session-fields; TaskSchema.SessionFields, generated): what cannot change per trial, so
# a condition may not set it (the path or any path under it). A condition may set every other field: the draw reads it per
# trial from the trial's combination. A condition's blank takes the stage's, then the task's value.
SESSION_FIELDS = {
    "rngSeed": "seeds the one random stream every trial is drawn from, at Go",
    "curriculum": "the stage these values are: one per task file",
    "trialConditions": "the mix the conditions are",
    "backgroundNoise": "plays without a break from Ready, before the first trial",
    "penalty.sound.levelDbfs": "the penalty sound's buffer is laid out once at Go (OpenRigs.Audio PlayPenaltySound)",
    "rewardTone": "the reward tone's buffer is laid out once at Go, at its levelDbfs",
    "optogenetics.enabled": "Zapit is connected and its conditions file loaded at Go",
    "optogenetics.zapitConditionsPath": "the conditions file loaded in Zapit and counted at Go",
    "optogenetics.laserMaxSeconds": "the trial loop's laser cap and backstop (lib-open-rigs LaserTiming.UntilOutcome) are fixed at Go",
    "optogenetics.triggerHoldSeconds": "the trial loop's trigger hold (lib-open-rigs LaserTiming.UntilOutcome) is fixed at Go",
}


def _if(schemas: dict) -> dict:
    """An if over these paths, required: JSON Schema passes a missing path's properties vacuously."""
    return {"properties": schemas, "required": list(schemas)}


# Where one setting limits another (the schema's x-rules, JSON Schema over dotted paths; open_rigs.rigs.conditions
# evaluates them), as coen-dome-decision-making's. No corridor setting limits another, so its rules only say what goes unread.
RULES = (
    {"if": _if({"contrasts": {"items": {"const": 0}}}), "unused": ["visualStimulus.spatialFrequencyStartCpd", "visualStimulus.spatialFrequencyEndCpd"],
     "message": "contrasts 0 shows no grating, so its spatial frequencies are unread"},
    {"if": _if({"auditoryStimulus.amplitude": {"const": 0}}), "unused": ["auditoryStimulus.initialAzimuthsDeg"],
     "message": "auditoryStimulus.amplitude 0 plays no tone, so its azimuths are unread"},
    {"if": _if({"autoRewardProbability": {"const": 1}}), "unused": ["wrongLickLimit", "rewardCeilingHz"],
     "message": "autoRewardProbability 1 rewards every trial, so the lick limits are unread"},
    {"if": _if({"stimulusOffProbability": {"const": 0}}), "unused": ["stimulusOff.trackFractions", "stimulusOff.stimuli"],
     "message": "stimulusOffProbability 0 never cuts a stimulus, so stimulusOff is unread"},
)
# How a condition is named (the schema's x-trial-types; derive_trial_type), coen-dome-decision-making's without its targets
# or azimuths: first match wins, on the condition's values each as a list. The grating shown (a contrast above 0) and the
# tone heard (an amplitude above 0): both audiovisualCoherent, one alone visual or auditory, neither neutral.
_ABOVE_0 = {"contains": {"exclusiveMinimum": 0}}
TRIAL_TYPES = (
    {"trialType": "audiovisualCoherent", "if": _if({"contrasts": _ABOVE_0, "auditoryStimulus.amplitude": _ABOVE_0})},
    {"trialType": "visual", "if": _if({"contrasts": _ABOVE_0})},
    {"trialType": "auditory", "if": _if({"auditoryStimulus.amplitude": _ABOVE_0})},
    {"trialType": "neutral"},
)
# What some weighted condition must draw (the schema's x-required-conditions; open_rigs.rigs.conditions.required_problems):
# nothing, as the corridor has no target.
REQUIRED = ()


def _get(data: dict, dotted) -> Any:
    for part in dotted.split("."):
        data = data[part]
    return data


def _put(data: dict, dotted, value: Any) -> None:
    *parents, key = dotted.split(".")
    for part in parents:
        data = data[part]
    if key not in data:
        raise KeyError(dotted)
    data[key] = value


def _options(base: dict, values: dict[str, Any], path) -> list:
    # A cell's picks: a list field's entries, each a one-entry list; a scalar field's list, else its one value (a whole
    # list, x-whole, is one value).
    value = values[path] if path in values else _get(base, path)
    if path in LIST_FIELDS:
        return [[entry] for entry in value] if isinstance(value, list) else [value]
    return value if isinstance(value, list) and not isinstance(_get(base, path), list) else [value]


def condition_combinations(base: dict, values: dict[str, Any], pairs: list[list[str]] = ()) -> list[dict[str, Any]]:
    """Every combination of a condition (coen-dome-decision-making's): each pairs group's lists zipped (equal lengths), then
    by path each other list cell of its set and every list field (LIST_FIELDS, the set's or the task's list) one pick, all
    combined as a product, the first cell's pick changing fastest (TrialDraw.BuildConditions' order: Settings_Combinations'
    rows, conditions in turn). A list field's entry is a one-entry list, so the draw's chances are those of picking each
    list's entry per trial.

    The C# draw is the rule the rig runs; this copy stays only for the task model's validation and for the test that holds
    the C#'s Settings_Combinations equal to it (tests/test_combinations.py)."""
    return _product(_cells(base, values, pairs))


def _cells(base: dict, values: dict[str, Any], pairs) -> list[tuple[tuple[str, ...], list[tuple]]]:
    # A condition's cells (each pairs group, then each other path), each with its picks; none empty.
    cells: list[tuple[tuple[str, ...], list[tuple]]] = []
    paired = [path for group in pairs for path in group]
    for group in pairs:
        lists = [_options(base, values, path) for path in group]
        if len({len(options) for options in lists}) > 1:
            raise ValueError(f"pairs {group} have lists of lengths {', '.join(str(len(options)) for options in lists)}; paired lists must be equally long")
        cells.append((tuple(group), list(zip(*lists))))
    cells += [((path,), [(option,) for option in _options(base, values, path)]) for path in sorted({*values, *LIST_FIELDS} - {*paired})]
    for paths, options in cells:
        if not options:
            raise ValueError(f"{', '.join(paths)} is an empty list")
    return cells


def _product(cells) -> list[dict[str, Any]]:
    combos: list[dict[str, Any]] = [{}]
    for paths, options in cells:
        combos = [{**combo, **dict(zip(paths, option))} for option in options for combo in combos]
    return combos


def session_field(path) -> str | None:
    """The session field a dotted path is or lies under (SESSION_FIELDS), else None."""
    return next((field for field in SESSION_FIELDS if path == field or path.startswith(field + ".")), None)


class TrialCondition(TaskBlock):
    """One kind of trial in the mix (coen-dome-decision-making's shape): its trialType, its relative weight, its condition-level
    values (set) by dotted path and the lists it pairs. A list in a scalar field's cell is one value picked per trial; a
    list field's list (LIST_FIELDS), at any level, one entry picked per trial. Paired lists share one picked index per trial."""

    model_config = ConfigDict(extra="forbid")

    trial_type: TrialType = Field(
        description="The condition's name, logged as each trial's trialType: derived from its contrasts and amplitude (x-trial-types, derive_trial_type) unless chosen; it names rows and analysis groups and drives nothing"
    )
    weight: int = Field(ge=0, le=1_000_000, description="Relative draw weight: each new trial picks a condition with these as relative chances")
    pairs: list[list[String]] = Field(
        default_factory=list,
        description="Groups of paths whose lists (the set's, else the task's) have equal length and share one picked index per trial; unpaired lists are picked independently",
    )
    set: dict[str, Any] = Field(default_factory=dict, description="Condition-level values by dotted path, any field but x-session-fields'; the rest are the task's")


def derive_trial_type(task: "UclOpenCoenDomeNavigationTaskParameters", condition: TrialCondition) -> str:
    """A condition's trialType from its settings (its set over the task's), by TRIAL_TYPES (x-trial-types)."""
    return conditions.derive_trial_type(TRIAL_TYPES, task.model_dump(by_alias=True, mode="json"), condition.set, condition.pairs)


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

    model_config = ConfigDict(json_schema_extra={"x-session-fields": list(SESSION_FIELDS), "x-rules": list(RULES), "x-trial-types": list(TRIAL_TYPES),
                                                  "x-required-conditions": list(REQUIRED), "x-max-combinations": conditions.MAX_COMBINATIONS})

    reward: RewardSettings = Field(description="Reward delivery parameters")

    trial_conditions: list[TrialCondition] = Field(
        min_length=1,
        description=(
            "The trial mix: each new trial picks one condition at random with the weights as relative chances, independently of every other "
            "trial, then one of its combinations: the task's values with the condition's set over them, any field but x-session-fields', each list field one entry; "
            "its trialType is the trial row's. One whole value: a curriculum stage's conditions replace it; the trialCondition column is its 0-based index"
        ),
        json_schema_extra={"x-whole": True},
    )
    auditory_stimulus: AuditoryStimulus = Field(default=AuditoryStimulus(), description="The tone sweep's pips (src/sounds/all_waveforms.bin), each played at amplitude")
    contrasts: list[Double] = Field(
        default=[1.0], min_length=1, description="Candidate grating contrasts (BonVision's Contrast, 1 full contrast), one drawn per trial; 0 shows no grating"
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

    @field_validator("contrasts")
    @classmethod
    def _not_negative(cls, value: list[float]) -> list[float]:
        if any(contrast < 0 for contrast in value):
            raise ValueError("contrasts must each be 0 or more")
        return value

    @model_validator(mode="after")
    def _weights_and_rules(self):
        # TrialDraw.CheckTask's same rules: x-rules on the task's own values (each combination's in _conditions_run), the weights' sum.
        problems = [f"{p} (x-rules)" for p in conditions.rule_problems(RULES, self.model_dump(by_alias=True, mode="json"))]
        if problems:
            raise ValueError("; ".join(problems))
        if not sum(condition.weight for condition in self.trial_conditions) > 0:
            raise ValueError("trial_conditions needs a condition with a weight above 0")
        if sum(condition.weight for condition in self.trial_conditions) > MAX_WEIGHT_SUM:
            raise ValueError(f"trial_conditions' weights add to more than {MAX_WEIGHT_SUM}, the most the draw can sum")
        return self

    @model_validator(mode="after")
    def _conditions_run(self, info: ValidationInfo):
        # TrialDraw's same build: each condition's set and pairs over the task's values, every combination (at most
        # MAX_COMBINATIONS), each a task that must validate; then x-required-conditions on the weighted combinations.
        if (info.context or {}).get("condition"):
            return self
        base = self.model_dump(by_alias=True, mode="json")
        leaves = conditions.flatten(base)
        drawn, built = [], {}   # built: each distinct set and pairs' combinations, checked once
        for i, condition in enumerate(self.trial_conditions):
            where = f"trial_conditions[{i}] ({condition.trial_type.value})"
            paired = [path for group in condition.pairs for path in group]
            for path in [*condition.set, *paired]:
                if field := session_field(path):
                    raise ValueError(f"{where} sets or pairs {path}, a session field (x-session-fields: {field} {SESSION_FIELDS[field]})")
                if path not in leaves:
                    raise ValueError(f"{where} sets or pairs {path}, which is not a task field")
                if path.startswith("optogenetics.") and not self.optogenetics.enabled:
                    raise ValueError(f"{where} sets {path} with optogenetics off")
            if len(set(paired)) < len(paired) or any(len(group) < 2 for group in condition.pairs):
                raise ValueError(f"{where}: each pairs group needs two paths or more, each path in one group")
            key = json.dumps([condition.set, condition.pairs], sort_keys=True)
            if key not in built:
                built[key] = self._combinations(base, condition, where)
            drawn += built[key] * (condition.weight > 0)
        if problems := conditions.required_problems(list(REQUIRED), base, drawn):
            raise ValueError("; ".join(f"{problem} (x-required-conditions)" for problem in problems))
        return self

    def _combinations(self, base: dict, condition: TrialCondition, where) -> list[dict]:
        """A condition's combinations as tasks' values (by alias, as JSON), each checked as a task."""
        try:
            cells = _cells(base, condition.set, condition.pairs)
        except ValueError as e:
            raise ValueError(f"{where}: {e}") from None
        # Counted before any is built, as TrialDraw.BuildConditions does, so an oversized condition is refused at once.
        count = math.prod(len(options) for _, options in cells)
        if count > conditions.MAX_COMBINATIONS:
            raise ValueError(f"{where} expands into {count} combinations, more than {conditions.MAX_COMBINATIONS} (x-max-combinations)")
        tasks = []
        for combo in _product(cells):
            data = copy.deepcopy(base)
            for path, value in combo.items():
                _put(data, path, value)
            data["trialConditions"] = [{"trialType": condition.trial_type.value, "weight": 1}]
            try:
                type(self).model_validate(data, context={"condition": True})
            except ValidationError as e:
                raise ValueError(f"{where} with {combo}: {e}") from None
            tasks.append(data)
        return tasks

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


# The list fields one entry of which each trial draws (open_rigs.rigs.conditions.pick_lists).
LIST_FIELDS = conditions.pick_lists(UclOpenCoenDomeNavigationTaskParameters.model_json_schema(by_alias=True))


class UclOpenCoenDomeNavigationTaskLogic(Task):
    """Top-level task logic schema."""

    version: Literal[__semver__] = __semver__
    name: Literal["UclOpenCoenDomeNavigation"] = Field(default="UclOpenCoenDomeNavigation", description="Name of the task")
    task_parameters: UclOpenCoenDomeNavigationTaskParameters
