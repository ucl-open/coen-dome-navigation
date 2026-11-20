"""The task file's schema: config/task-abstract-corridor.yml, which app-rig-control copies at launch and again at Go with the
form's edits; the workflow reads the copy (TaskPath) at Go. Ported from Tom Childs' auditory-vr-corridor task.py.

The stage is a stage of the lab's Mouse Curriculum, recorded only as the task file's curriculum
(K:\\config\\curricula\\coen-dome-navigation\\task-abstract-corridor.conditions.yml): app-rig-control writes that stage's values
over this file's flat values at launch and fills curriculum (id, code, version, path, label, sha256); trialStructure holds the
timing, track the track, reward the reward and its tone, penalty each penalised kind's timeout, sound
and flash, and rngSeed seeds every draw. The sweep is src/sounds/sweep.json's,
made with the tone files (`uv run generate-waveforms`), not the task file's. Every sound plays at a level in dBFS RMS
(auditoryStimulus, reward.tone and each penalty kind's sound), each a condition-level value; the background noise is the lab's, at its
constant level (open_rigs.core.task BACKGROUND_LEVEL_DBFS). The pips are stored at sweep.json's pipLevelDbfs.

What a trial shows and offers is its trial condition's combination: a condition may set any field but the session fields
(x-session-fields, SESSION_FIELDS), and every list field (LIST_FIELDS: the contrasts, the initial azimuths, the track lengths and
the cutoff fractions) expands into its combinations, one entry each; the draw reads every value per trial from the
trial's combination (its row of Settings_Combinations the trialCombinationId column). Its trialType, derived from its values
(TRIAL_TYPES), names it and drives nothing."""

import json
import math
from pathlib import Path
from typing import Literal

# Integers are int: open_rigs' Int carries its own range, which drops a Field's ge or gt.
from open_rigs.core.base import Bool, Double, String
from open_rigs.core.task import (
    MAX_REWARD_UL,
    Iti,
    LickQuietWait,
    Optogenetics,
    Penalty,
    ResponseWindow,
    SoundLevelDbfs,
    Spouts,
    StimulusTiming,
    Task,
    TaskBlock,
    TaskParameters,
    WheelQuietWait,
    level_dbfs,
)
from open_rigs.core.task import (
    Reward as LabReward,
)
from open_rigs.rigs import conditioned_task as ct
from open_rigs.rigs.conditioned_task import ALL_SPEAKERS, SoundAzimuth
from open_rigs.components.zapit import ramp_down_from
from pydantic import ConfigDict, Field, ValidationInfo, field_validator, model_validator

from ucl_open_coen_dome_navigation import __semver__

STEP_SECONDS = 0.05  # the tone pips are 50 ms apart on the Harp clock (TrialLogic.cs StepSeconds)
LASER_CAP_MARGIN_SECONDS = 0.25  # how much longer than the max laser (stimulusToResponseWindowSeconds + responseWindowSeconds + laserToStimulusSeconds) the derived cap is (TrialLogic.cs LaserCapMarginSeconds)
MAX_LEAD_SECONDS = 0.2  # the furthest the trigger may come before the first pip, in whole steps (TrialLogic.cs MaxLeadSeconds)
UNLIMITED_WINDOW_TRIGGER_AFTER_SECONDS = 2.0  # the furthest after the first pip the trigger may come on a stage without responseWindowSeconds (TrialLogic.cs)

SWEEP_FILE = Path(__file__).resolve().parents[1] / "sounds" / "sweep.json"  # src/sounds/sweep.json


def sweep() -> dict:
    """The sweep the committed tone files were made for: startHz, endHz, pipCount, pipLevelDbfs and the waveforms' sampleRate."""
    return json.loads(SWEEP_FILE.read_text(encoding="utf-8"))


def track_end_hz(made: dict) -> float:
    """The highest tone the track plays: startHz times endHz / startHz in whole numbers (Sweep.cs TrackEndHz, 24 kHz)."""
    return made["startHz"] * math.floor(made["endHz"] / made["startHz"])


def min_outcome_to_iti(task: dict) -> float:
    """The shortest outcome to next interval a trial of this task (by alias, as JSON) can have: the shortest penalty timeout
    among the outcomes that can occur, 0 where a reward can come. Every trial here can be rewarded (a lick in the zone, or
    auto-reward), so 0 (TrialLogic.cs MinOutcomeToItiSeconds)."""
    return 0.0


class TrialStructure(Iti, ResponseWindow, LickQuietWait, WheelQuietWait):
    """Timing of each trial. The inter-trial interval (open_rigs.core.task Iti) runs to this trial's first tone pip, with a
    quiet mouse, from the last trial's outcome when rewarded (outcome, interval; the spouts out spouts.rewardConsumptionSeconds into it)
    or its outcome plus its penalty timeout when penalised (outcome, timeout, interval); trial 1: from the first clock tick after Go. The response window opens stimulusToResponseWindowSeconds
    after the first tone pip, licks before it not counted, and runs responseWindowSeconds from there; null: it never closes
    (optogenetics then refused, as the laser's cap is derived from it). The quiet waits count from the interval's start
    (open_rigs.core.task LickQuietWait, WheelQuietWait)."""

    stimulus_to_response_window_seconds: Double = Field(
        default=0.0,
        ge=0,
        description="From stimulus onset (the first tone pip) to the response window opening, in seconds; responseWindowSeconds runs from that opening, and licks before it are not counted",
    )


class Track(TaskBlock):
    """The virtual track: its lengths, how far it moves per wheel cm and how fast it moves on its own."""

    lengths_cm: list[Double] = Field(
        default=[80.0, 100.0, 120.0], min_length=1, description="Track lengths, one drawn per trial, in track cm (the mouse runs track cm / wheelGain of wheel)"
    )
    wheel_gain: Double = Field(default=8.0, gt=0, description="Track cm per wheel cm: the track's tone and grating move this many cm for each cm the wheel turns, so the mouse runs track length / wheelGain cm of wheel to the end")
    auto_progress_per_second: Double = Field(
        default=0.04,
        ge=0,
        description="Fraction of the track the tone and grating advance per second on their own from the trial start, added to what the wheel sets; 0: none. Moves the stimulus only: the track's end and the cutoff points are judged on the wheel alone",
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


class StimulusCutoff(TaskBlock):
    """A cutoff trial: which stimuli switch off, and where (a condition sets stimuli to make its trials cutoff ones).

    Each listed stimulus switches off once the mouse has run a track fraction of the trial's track (the fraction one of
    trackFractions, a list field); an unlisted stimulus stays on. Everything else about the trial is unchanged, so it is still
    rewarded as normal: the animal has to keep going and find the reward zone without the stimulus.
    """

    stimuli: Literal["none", "auditory", "visual", "both"] = Field(
        default="none",
        description="The stimuli that switch off: none (an ordinary trial), auditory (the tone), visual (the grating) or both",
    )
    track_fractions: list[Double] = Field(
        default=[1.0],
        min_length=1,
        description=(
            "Where the stimuli switch off, as a fraction of the trial's track length, one drawn per trial; 1 (the default) is the "
            "track's end, so never during the trial: a cutoff condition sets its own fractions"
        ),
    )

    @field_validator("track_fractions")
    @classmethod
    def _fractions_inside(cls, value: list[float]) -> list[float]:
        if any(not 0 < fraction <= 1 for fraction in value):
            raise ValueError("stimulusCutoff track fractions must be above 0 and at most 1 (1: never during the trial)")
        return value


class SpoutsSettings(Spouts):
    """The spouts (the lab's block, open_rigs.core.task Spouts; OpenRigs.Trials SpoutCommands): only the left spout counts.
    Always in (this task's default): both spouts stay where Ready put them all session, as before the block. Otherwise the left
    spout alone comes in at the SpoutsIn cue, stimulusToSpoutsInSeconds from the first pip (negative: before it, the trial
    starting that much earlier; always in, no lead), stays in on a reward and goes out without one, and goes out
    rewardConsumptionSeconds into the next interval (time to drink a reward, inside the interval; nothing while always in). A lick counts from the response window's opening (trialStructure.stimulusToResponseWindowSeconds after
    the first pip), so a positive stimulusToSpoutsInSeconds is at most that (lib's Spouts.check_fits); a negative one, the
    trial's lead, is in whole 50 ms steps (the pips' grid from the trial start)."""

    always_in: Bool = Field(
        default=True, description="Always in: both spouts stay where Ready put them for the whole session, never moved by the task (this task's default)"
    )

    @model_validator(mode="after")
    def _the_lead_on_the_pip_grid(self):
        if self.stimulus_to_spouts_in_seconds >= 0:
            return self
        steps = self.stimulus_to_spouts_in_seconds / STEP_SECONDS
        if abs(steps - round(steps)) > 1e-6:
            raise ValueError(f"spouts.stimulus_to_spouts_in_seconds ({self.stimulus_to_spouts_in_seconds}) must be a multiple of the {STEP_SECONDS} s step interval")
        return self


class Stimulus(StimulusTiming):
    """When the tone and grating end (the lab's block, open_rigs.core.task StimulusTiming): durationSeconds from the first pip,
    or responseToStimulusOffSeconds after the outcome (on a miss, the response window's end or the track's end), whichever comes
    first; until then they go on following the wheel and the auto-progress after the outcome, held at the track's end. A cutoff
    trial's cut stimuli stay off. Logged as stimulusOffTime (the earlier of this and a cutoff where neither stimulus plays)."""

    response_to_stimulus_off_seconds: Double | None = Field(
        default=0.0,
        ge=0,
        description="From the response (the outcome; on a miss, the response window's end or the track's end) to the stimulus off, in seconds, unless the duration ends it first: 0 (the default), off at the outcome; null, the outcome does not end it (it runs durationSeconds); at most the shortest penalty timeout (0 where a reward can come, so every trial here) plus itiMinSeconds",
    )


class OptogeneticsSettings(Optogenetics):
    """Zapit optogenetic stimulation: the lab's block (open_rigs.core.task Optogenetics) and this task's laser timing.

    The stimulation sites and ramp-down are set in the Zapit conditions file loaded in Zapit, not here; Bonsai reads
    the same file to list its conditions and its max ramp-down. "Stimulus" is the tone and grating; the laser is
    continuous: it starts at the trigger (the trigger line's rising edge on a hardware-triggered rig), laser_to_stimulus_seconds
    before the first tone pip (after it if negative), in whole 50 ms steps and at most 0.2 s, and Bonsai stops it at the
    trial's outcome, or at a cap derived at Go (the max stimulusToResponseWindowSeconds plus responseWindowSeconds any condition can draw plus
    laser_to_stimulus_seconds plus 0.25 s) if that comes first. The trigger line, and the masking light on it, stays high past
    the outcome for the conditions file's max ramp-down. A trial that ends before its trigger has no laser.
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
    {"if": ct.if_({"stimulusCutoff.stimuli": {"const": "none"}}), "unused": ["stimulusCutoff.trackFractions"],
     "message": "stimulusCutoff.stimuli none switches nothing off, so its track fractions are unread"},
)
# How a condition is named (the schema's x-trial-types; derive_trial_type, the C#'s TrialTypes.Derive at Go), logged as
# trialType: it names rows and analysis groups and drives nothing. coen-dome-decision-making's words without its targets or
# azimuths (so no audiovisualConflict: the corridor has no side for the stimuli to disagree on): first match wins, on the
# condition's values each as a list. The grating shown (a contrast above 0) and the tone heard (a level, not mute): both
# audiovisualCoherent, one alone visual or auditory, neither neutral. A condition whose stimulusCutoff.stimuli is not none
# (a stimulus switches off) takes "Truncated" after its name, first: neutral has no stimulus, so no truncated form.
_OFF = {"stimulusCutoff.stimuli": {"contains": {"enum": ["auditory", "visual", "both"]}}}
TRIAL_TYPES = (
    {"trialType": "audiovisualCoherentTruncated", "if": ct.if_({"visualStimulus.contrasts": ct.ABOVE_0, "auditoryStimulus.levelDbfs": ct.SOUNDING, **_OFF})},
    {"trialType": "audiovisualCoherent", "if": ct.if_({"visualStimulus.contrasts": ct.ABOVE_0, "auditoryStimulus.levelDbfs": ct.SOUNDING})},
    {"trialType": "visualTruncated", "if": ct.if_({"visualStimulus.contrasts": ct.ABOVE_0, **_OFF})},
    {"trialType": "visual", "if": ct.if_({"visualStimulus.contrasts": ct.ABOVE_0})},
    {"trialType": "auditoryTruncated", "if": ct.if_({"auditoryStimulus.levelDbfs": ct.SOUNDING, **_OFF})},
    {"trialType": "auditory", "if": ct.if_({"auditoryStimulus.levelDbfs": ct.SOUNDING})},
    {"trialType": "neutral"},
)
# What some weighted condition must draw (the schema's x-required-conditions; open_rigs.rigs.conditions.required_problems):
# nothing, as the corridor has no target.
REQUIRED_CONDITIONS = ()


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
    """The Mouse Curriculum stage the values came from, and the task file's only record of the stage: filled by app-rig-control
    at launch; empty in the repo's task files. Bonsai only records it."""

    label: String = Field(description="The stage's label in the curriculum")
    sha256: String = Field(description="The sha256 of the curriculum file's text app-rig-control ran it from")


class Reward(LabReward):
    """Reward delivery on the left spout's valve (the lab's reward block): the size, the large-reward trials and the reward
    tone (src/sounds/reward_tone.bin, OpenRigs.Audio PlayRewardTone) at tone.levelDbfs, on every speaker."""


class UclOpenCoenDomeNavigationTaskParameters(TaskParameters, ct.ConditionedTask):
    """Task parameters for the coen-dome-navigation corridor: the lab's blocks in the lab's order (the mix, timing, stimuli,
    reward, penalty, optogenetics), then the spouts, the stimulus's end, the track, the reward zone, the wrong-lick limit and stimulus cutoff.

    A lick first counts from the response window's opening, trialStructure.stimulusToResponseWindowSeconds after the trial's first tone pip. Auto-reward is given on the pip whose tone reaches the reward zone's floor (rewardZoneMinHz), no lick needed, and the trial's licks are ignored (outcomeReason rewardZoneReached); on the other trials only a lick in the zone is rewarded. A trial that runs out of time or track with the tone in the zone, or ends outside the zone, is penalised. The reward is given on the left spout's valve, planned from the valve's calibration (calibration\\RewardValve-Left.json); an uncalibrated valve stops the workflow at Start."""

    model_config = ConfigDict(json_schema_extra=ct.schema_extra(SESSION_FIELDS, RULES, TRIAL_TYPES, REQUIRED_CONDITIONS))
    SESSION_FIELDS = SESSION_FIELDS
    RULES = RULES
    TRIAL_TYPES = TRIAL_TYPES
    REQUIRED_CONDITIONS = REQUIRED_CONDITIONS

    curriculum: Curriculum | None = Field(
        default=None,
        description="The Mouse Curriculum stage these values came from, the only record of the stage, filled by app-rig-control at launch; Go refuses a task whose curriculum is empty or malformed",
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
    penalty: Penalty = Field(
        description="What a penalised trial costs by its kind (open_rigs.core.task Penalty): incorrect, a trial ending outside the zone; noResponse, one out of time or track (a miss), which this task penalises, so never null"
    )
    optogenetics: OptogeneticsSettings = Field(description="Zapit optogenetic stimulation")
    spouts: SpoutsSettings = Field(
        default=SpoutsSettings(stimulus_to_spouts_in_seconds=0.0), description="When the spouts move: always in, or the left spout in before the first pip and out after the reward"
    )
    stimulus: Stimulus = Field(default=Stimulus(), description="When the tone and grating end: at the outcome, after it, or after a duration")
    track: Track = Field(default=Track(), description="The virtual track")
    reward_zone_min_hz: Double = Field(default=18000, ge=1, description="Lower frequency of the reward zone, in Hz, above the sweep's start (src/sounds/sweep.json)")
    reward_zone_max_hz: Double = Field(default=24000, ge=1, description="Upper frequency of the reward zone, in Hz, at most the track's top (24 kHz: the sweep's start times endHz / startHz in whole numbers, src/sounds/sweep.json)")
    wrong_lick_limit: int = Field(default=0, ge=0, description="Licks outside the zone that end the trial; 0: wrong licks never end it")
    stimulus_cutoff: StimulusCutoff = Field(default=StimulusCutoff(), description="Which stimuli switch off part way along the track, and where")

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
        # LaserCapSeconds, derived at Go): with no response window there is no cap to derive.
        if self.optogenetics.enabled and self.trial_structure.response_window_seconds is None:
            raise ValueError("optogenetics needs trial_structure.response_window_seconds: the laser's cap is derived from it")
        return self

    @model_validator(mode="after")
    def _the_lead_fits(self, info: ValidationInfo):
        # TrialLogic.cs TrialDraw.Check's same rules; the ramp-down's (TrialDraw.CheckTask, holdMax) when the check runs
        # where the Zapit conditions file is (open_rigs.rigs.check_task's context), Go's otherwise.
        self.optogenetics.check_lead_fits(self.trial_structure.iti_min_seconds)
        self.optogenetics.check_ramp_down_fits(self.trial_structure.iti_min_seconds, ramp_down_from(info))
        lead = self.optogenetics.laser_to_stimulus_seconds
        # A negative lead puts the trigger after the first pip, at most the response window's close.
        s = self.trial_structure
        after = s.stimulus_to_response_window_seconds + (s.response_window_seconds or UNLIMITED_WINDOW_TRIGGER_AFTER_SECONDS)
        if -lead > after + 1e-9:
            raise ValueError(f"optogenetics.laser_to_stimulus_seconds ({lead} s) puts the trigger more than {after:g} s after the first pip")
        return self

    @model_validator(mode="after")
    def _spouts_and_stimulus_fit(self):
        # TrialLogic.cs TrialDraw.Check's same rules (OpenRigs.Trials Spouts.Problems, StimulusTiming.Problems and
        # Penalties.Problems): a lick counts from the response window's opening, the soonest outcome; a miss is penalised.
        s = self.trial_structure
        self.penalty.check_misses(True)
        self.spouts.check_fits(s.iti_min_seconds, s.stimulus_to_response_window_seconds)
        window = s.response_window_seconds
        self.stimulus.check_fits(
            s.iti_min_seconds, math.inf if window is None else s.stimulus_to_response_window_seconds + window,
            stimulus_to_earliest_outcome_seconds=s.stimulus_to_response_window_seconds, min_outcome_to_iti_seconds=min_outcome_to_iti(self.model_dump(by_alias=True)),
        )
        return self

    @classmethod
    def between_trials(cls, tasks: list[dict], context: dict | None = None) -> None:
        """TrialLogic.cs TrialDraw.CheckTask's rules across the weighted combinations (OpenRigs.Trials StimulusTiming
        BetweenTrialsProblems): each stimulus off before the soonest next stimulus, its durationSeconds past its soonest outcome
        (stimulusToResponseWindowSeconds) and the longest responseToStimulusOffSeconds each within the shortest outcome to next
        interval (min_outcome_to_iti over every combination) plus the shortest itiMinSeconds; and (OpenRigs.Trials Spouts
        BetweenTrialsProblems) the longest spouts.rewardConsumptionSeconds plus the spouts' longest lead before the first pip
        (a negative stimulusToSpoutsInSeconds where the spouts are not always in) at most the shortest itiMinSeconds."""
        if not tasks:
            return
        iti = min(t["trialStructure"]["itiMinSeconds"] for t in tasks)
        consumption = max(t["spouts"]["rewardConsumptionSeconds"] for t in tasks)
        lead = max([0.0] + [-t["spouts"]["stimulusToSpoutsInSeconds"] for t in tasks if not t["spouts"]["alwaysIn"]])
        if consumption + lead > iti + 1e-9:
            raise ValueError(f"spouts.rewardConsumptionSeconds {consumption} s plus the spouts' longest lead before the stimulus {lead:g} s exceeds itiMinSeconds {iti} s of a combination that may come next")
        gap = min(min_outcome_to_iti(t) for t in tasks) + iti
        past = [t["stimulus"]["durationSeconds"] - t["trialStructure"]["stimulusToResponseWindowSeconds"] for t in tasks if t["stimulus"]["durationSeconds"] is not None]
        if past and max(past) > gap + 1e-9:
            raise ValueError(f"stimulus.durationSeconds runs {max(past):g} s past the soonest outcome, longer than the shortest outcome to next interval plus itiMinSeconds ({gap:g} s) of a combination that may come next")
        after = [t["stimulus"]["responseToStimulusOffSeconds"] for t in tasks if t["stimulus"]["responseToStimulusOffSeconds"] is not None]
        if after and max(after) > gap + 1e-9:
            raise ValueError(f"stimulus.responseToStimulusOffSeconds {max(after)} s exceeds the shortest outcome to next interval plus itiMinSeconds ({gap:g} s) of a combination that may come next")

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
