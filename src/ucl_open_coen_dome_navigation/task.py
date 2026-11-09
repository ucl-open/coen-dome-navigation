"""The task file's schema: config/task-abstract-av-corridor.yml, which rig-control copies with the form's edits and the
workflow reads at Go (TaskPath). Ported from Tom Childs' auditory-vr-corridor task.py.

Laid out as av-dome's (Pip, 9 Nov 2026): the stage is the TrainingStage experiment setting, a row of trainingStages
from 0 (Tom's stages 1 to 6 are rows 0 to 5), and each row says what the stage does, where the workflow used to test
the stage number; trialStructure holds the ITI and time limit, punishment the timeout and error sound, and rngSeed
seeds every draw."""

import math
from typing import List, Literal, Optional

from open_rigs.core.task import Task, TaskParameters
from pydantic import ConfigDict, Field, field_validator, model_validator
from swc.aeon.schema import BaseSchema

from ucl_open_coen_dome_navigation import __semver__

BEEP_INTERVAL_SEC = 0.05  # the tone pips are 50 ms apart on the Harp clock (HarpBeat)
STIM_DURATION_MARGIN_SEC = 0.25  # how much longer than the longest trial (closedLoopDuration + laser_lead_seconds) the laser_max_seconds cap must be

STAGE_COUNT = 6


class TrialStructure(BaseSchema):
    """Timing of each trial."""

    iti_min: float = Field(default=0.0, ge=0, description="Lower bound of the uniformly drawn inter-trial interval, in seconds")
    iti_max: float = Field(default=1.0, ge=0, description="Upper bound of the uniformly drawn inter-trial interval, in seconds")
    quiescent_seconds: float = Field(
        default=1.0,
        ge=0,
        description="On a stage with quiescent, the next trial waits until the mouse has gone this long without licking (uncapped), so licks for the last reward cannot carry into the next trial",
    )
    closed_loop_duration: float = Field(
        default=12.0, gt=0, description="On a stage with timeLimit, the longest a trial runs from its start, in seconds (av-dome's name for its response period)"
    )
    track_lengths_cm: List[float] = Field(
        default=[80.0, 100.0, 120.0], min_length=1, description="Track lengths, one drawn per trial and scaled by the stage's trackLengthScale, in cm"
    )

    @model_validator(mode="after")
    def _iti_ordered(self) -> "TrialStructure":
        if self.iti_max < self.iti_min:
            raise ValueError("iti_max must be >= iti_min")
        return self


class PunishmentSound(BaseSchema):
    """The error sound played on a punished trial."""

    enabled: bool = Field(default=True, description="Whether the error sound plays on a punished trial")
    path: str = Field(default="waveforms\\error_tone.wav", description="Wav file of the error sound, relative to src")
    gain: float = Field(default=1.0, ge=0, description="Playback gain of the error sound")


class Punishment(BaseSchema):
    """What an error costs, as the lab's tasks name it (the trial log's punishSound and punishTimeoutSeconds)."""

    timeout_seconds: float = Field(default=2.0, ge=0, description="Timeout after a punished trial, in seconds")
    sound: PunishmentSound = Field(default=PunishmentSound(), description="The error sound on a punished trial")


class Truncation(BaseSchema):
    """Where the cue switches off on a truncated trial (a stage's truncationFraction says how many).

    On a truncated trial the cue (tone and grating) switches off part way along the sweep. Everything else about
    the trial is unchanged, so it is still rewarded as normal: the animal has to keep going and find the reward
    zone without the cue.
    """

    points: List[float] = Field(
        default=[0.4, 0.6],
        min_length=1,
        description="Where the cue switches off, as a fraction of the sweep on a log scale, split evenly over truncated trials. The reward floor is at about 0.8",
    )

    @field_validator("points")
    @classmethod
    def _points_inside(cls, value: List[float]) -> List[float]:
        if any(not 0 < point < 1 for point in value):
            raise ValueError("Truncation points must be between 0 and 1")
        return value


class OptogeneticsConfig(BaseSchema):
    """Zapit optogenetic stimulation (names Pip's, 9 Nov 2026; av-dome's stimDelay, excludeRepeats and
    pseudorandomSelection dropped: a delay must be 0, there are no repeat trials, and draws are always balanced).

    The stimulation sites and ramp-down are set in the Zapit stimulus config loaded in Zapit, not here; the laser
    power is laser_power_mw, sent with every laser-on trial's StartStimulus (it overrides the config's power) and
    logged as laserPowerMw. Bonsai reads the same file to list its conditions (stimLocations01, stimLocations02, ...). Every trial of
    an optogenetics experiment sends Zapit a condition: laser-on trials with the laser on, all others as sham trials
    with the laser off, so galvo noise and masking light are the same on every trial. The stimulus is continuous: it
    starts at the trial's start (beep 0: the trigger pulse on a hardware-triggered rig), laser_lead_seconds before the first
    tone pip, and Bonsai stops it at the trial's outcome, or after laser_max_seconds if that comes first. Zapit's host,
    port and triggering are rig.yml's zapit block.
    """

    # The same rule as validate_config_path's, in the JSON schema: rig-control checks the task file
    # against the schema, not this model, so a ticked Zapit with no config path was sent at Go and
    # the workflow stopped at trial 1 (audit, 9 Nov 2026, round 3). additionalProperties is spelt
    # out because rig-control closes every object that lists properties.
    model_config = ConfigDict(
        json_schema_extra={
            "if": {"properties": {"enabled": {"const": True}}, "required": ["enabled"], "additionalProperties": True},
            "then": {
                "properties": {"zapitStimConfigPath": {"minLength": 1}, "laserPowerMw": {"type": "number"}},
                "required": ["laserPowerMw"],
                "additionalProperties": True,
            },
        }
    )

    enabled: bool = Field(default=False, description="Whether optogenetic stimulation is enabled")
    zapit_stim_config_path: str = Field(
        default="",
        description="Path to the Zapit stimulus config loaded in Zapit. Its top-level stimLocations01, stimLocations02, ... entries are the conditions: numbered 1 to N with no gaps, each with an offRampDownDuration_ms of at least 100 ms",
    )
    laser_power_mw: Optional[float] = Field(
        default=None,
        gt=0,
        description="Laser power on laser-on trials, in mW, the same for every condition: sent to Zapit with each StartStimulus (overriding the stimulus config's power; Zapit's laser calibration turns it into volts) and logged as laserPowerMw (0 on sham trials). Required with optogenetics on",
    )
    laser_on_probability: float = Field(
        default=0.4,
        ge=0,
        le=1,
        description="Probability that a trial after trials_before_laser is a laser-on trial, shared equally by every condition in the Zapit stimulus config, a laser-on control site included. All other trials are sham trials: Zapit is sent a condition with the laser off. Conditions come from shuffled queues, one for laser-on and one for sham trials, so each is used equally often",
    )
    laser_max_seconds: float = Field(
        default=12.5,
        gt=0,
        description="Longest the stimulus runs in one trial, in seconds from onset, timed by Bonsai; the laser then ramps down over the condition's offRampDownDuration_ms. A safety cap: the stimulus normally stops at the trial's outcome. With optogenetics on it must be at least trialStructure.closedLoopDuration (the time limit, 12 s) + laser_lead_seconds + 0.25 s, so it never ends a timed-out trial's laser early; on a stage without timeLimit a longer trial loses its stimulus",
    )
    laser_lead_seconds: float = Field(
        default=0.1,
        ge=0.05,
        le=0.2,
        description="How long the stimulus runs before the first tone pip, in seconds: a multiple of the 50 ms beep, from 0.05 to 0.2. The first pip moves to beep laser_lead_seconds / 50 ms on every trial of an optogenetics experiment, laser-on or sham, so trial timing does not reveal laser-on trials. At most 0.2 s: licks before the first pip count toward the wrong-lick limits, and a longer lead could end a trial before its first pip",
    )
    trials_before_laser: int = Field(
        default=20,
        ge=0,
        description="Trials at the experiment's start with no laser-on trial among them (each still sends Zapit a condition at 0 mW). laser_on_probability applies from the next trial",
    )

    @model_validator(mode="after")
    def validate_config_path(self):
        if self.enabled and not self.zapit_stim_config_path:
            raise ValueError("optogenetics is enabled but zapit_stim_config_path is empty")
        if self.enabled and self.laser_power_mw is None:
            raise ValueError("optogenetics is enabled but laser_power_mw is not set")
        if self.laser_power_mw is not None and not math.isfinite(self.laser_power_mw):
            raise ValueError(f"laser_power_mw ({self.laser_power_mw}) must be a finite number of mW")
        return self

    @model_validator(mode="after")
    def validate_lead_time(self):
        # Bonsai starts the stimulus on a beep and moves the first tone pip laser_lead_seconds later, so laser_lead_seconds is a whole number of beeps
        beeps = self.laser_lead_seconds / BEEP_INTERVAL_SEC
        if abs(beeps - round(beeps)) > 1e-6:
            raise ValueError(f"laser_lead_seconds ({self.laser_lead_seconds}) must be a multiple of the {BEEP_INTERVAL_SEC} s beep interval")
        return self

    @model_validator(mode="after")
    def validate_stim_duration_finite(self):
        if not math.isfinite(self.laser_max_seconds):
            raise ValueError(f"laser_max_seconds ({self.laser_max_seconds}) must be a finite number of seconds")
        return self


class TrainingStage(BaseSchema):
    """One row of the training stage table: what the stage does. Rows are selected by the TrainingStage setting."""

    label: str = Field(description="The stage's name, for people")
    reward_floor_hz: float = Field(ge=1, le=25000, description="Lower frequency of the reward zone, in Hz")
    reward_ceiling_hz: float = Field(ge=1, le=25000, description="Upper frequency of the reward zone, in Hz")
    reward_size_ul: float = Field(
        gt=0,
        description="Microlitres each reward gives, on the left spout's valve. The valve's calibration plans the pulses; an uncalibrated valve gives one pulse of rig.yml's width per 2 ul (av-dome's RewardLine)",
    )
    lick_to_reward: bool = Field(description="Whether a lick in the zone earns the reward; false rewards reaching the zone, no lick needed")
    wrong_lick_limit: int = Field(ge=0, description="Licks outside the zone that end the trial; 0: wrong licks never end it")
    punish_wrong_zone: bool = Field(
        description="Whether a trial that ends outside the reward zone is punished. A trial that runs out of time or track with the frequency in the zone is punished on every stage"
    )
    quiescent: bool = Field(description="Whether the next trial waits for trialStructure.quiescentSeconds without licks")
    time_limit: bool = Field(description="Whether a trial ends at trialStructure.closedLoopDuration")
    track_end_ends_trial: bool = Field(description="Whether reaching the end of the track ends the trial")
    track_length_scale: float = Field(gt=0, description="The drawn track length is multiplied by this")
    frequency_drift: bool = Field(description="Whether the frequency also drifts up with time, not only with distance run")
    truncation_fraction: float = Field(ge=0, le=1, description="Fraction of trials whose cue switches off early (truncation.points)")

    @model_validator(mode="after")
    def _zone_ordered(self) -> "TrainingStage":
        if self.reward_floor_hz >= self.reward_ceiling_hz:
            raise ValueError("reward_floor_hz must be below reward_ceiling_hz")
        return self


class UclOpenCoenDomeNavigationTaskParameters(TaskParameters):
    """Task params."""

    modality: Literal["auditory", "visual", "audiovisual"] = Field(
        default="auditory", description="What the trial shows: the tone sweep (auditory), the grating (visual) or both (audiovisual)"
    )
    start_freq: int = Field(
        default=2000,
        description="Start frequency of the sweep (Hz). The tone files in src/waveforms are made for this value: the workflow stops at Go if they differ",
        ge=1,
        le=25000,
    )
    end_freq: int = Field(
        default=25000,
        description="End frequency of the sweep (Hz). The tone files in src/waveforms are made for this value: the workflow stops at Go if they differ",
        ge=1,
        le=25000,
    )
    n_freq_bins: int = Field(
        default=100,
        description="Number of frequency bins for quantization. The tone files in src/waveforms are made for this value: the workflow stops at Go if they differ",
        ge=10,
    )
    amplitude: float = Field(
        default=0.05,
        description="Audio amplitude (0.0 to 1.0). The tone files in src/waveforms are made for this value: the workflow stops at Go if they differ",
        ge=0.0,
        le=1.0,
    )
    background_noise_level: float = Field(
        default=0.5, description="Constant background white noise RMS, as a fraction of the beep RMS (0 = off)", ge=0.0, le=1.0
    )
    quantize_bin_size: int = Field(default=1, description="The distance run is rounded to this before it sets the frequency, in cm", ge=1)
    wheel_diameter_mm: Optional[float] = Field(
        default=75.0,
        gt=0,
        description=(
            "Wheel diameter the task converts encoder counts to cm with, replacing rig.yml's behaviorBoard runningWheel "
            "wheelDiameterMm; null uses rig.yml's. Tom Childs' workflow converted at 75 mm while every corridor rig's "
            "rig.yml says 170 mm, so 75 keeps each track the number of wheel turns the mice trained on until the real "
            "diameter is settled"
        ),
    )
    trial_structure: TrialStructure = Field(default=TrialStructure(), description="Timing of each trial")
    punishment: Punishment = Field(default=Punishment(), description="The timeout and error sound a punished trial gets")
    truncation: Truncation = Field(default=Truncation(), description="Where the cue switches off on a truncated trial")
    optogenetics: OptogeneticsConfig = Field(default=OptogeneticsConfig(), description="Zapit optogenetic stimulation")
    training_stages: List[TrainingStage] = Field(
        description="Training stage table, indexed by the TrainingStage setting from 0; a setting past the end uses the last row"
    )

    @model_validator(mode="after")
    def _stim_duration_outlasts_the_trial(self):
        # The laser is meant to stay on until the outcome: the cap must be longer than the longest trial, the time limit
        # (closedLoopDuration, counted from the first tone pip) plus laser_lead_seconds (zapit-v2)
        opto = self.optogenetics
        shortest = self.trial_structure.closed_loop_duration + opto.laser_lead_seconds + STIM_DURATION_MARGIN_SEC
        if opto.enabled and opto.laser_max_seconds < shortest - 1e-9:
            raise ValueError(
                f"optogenetics.laser_max_seconds ({opto.laser_max_seconds} s) must be at least {shortest:g} s: trialStructure.closedLoopDuration "
                f"({self.trial_structure.closed_loop_duration:g} s) counts from the first tone pip, laser_lead_seconds ({opto.laser_lead_seconds} s) after onset"
            )
        return self

    @model_validator(mode="after")
    def _zones_inside_sweep(self):
        if self.start_freq >= self.end_freq:
            raise ValueError("start_freq must be less than end_freq")
        for i, stage in enumerate(self.training_stages):
            if stage.reward_floor_hz <= self.start_freq or stage.reward_ceiling_hz > self.end_freq:
                raise ValueError(f"training_stages[{i}]'s reward zone is not inside the sweep ({self.start_freq} to {self.end_freq} Hz)")
        return self

    @field_validator("training_stages")
    @classmethod
    def _at_least_one_stage(cls, value: List[TrainingStage]) -> List[TrainingStage]:
        if not value:
            raise ValueError("training_stages must have at least one entry")
        return value


class UclOpenCoenDomeNavigationTaskLogic(Task):
    """Top-level task logic schema."""

    version: Literal[__semver__] = __semver__
    name: Literal["UclOpenCoenDomeNavigation"] = Field(default="UclOpenCoenDomeNavigation", description="Name of the task")
    task_parameters: UclOpenCoenDomeNavigationTaskParameters
