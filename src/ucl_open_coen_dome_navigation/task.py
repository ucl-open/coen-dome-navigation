"""The task file's schema: config/task-abstract-av-corridor.yml, which rig-control copies with the form's edits and the
workflow reads at Go (TaskPath). Ported from Tom Childs' auditory-vr-corridor task.py.

Laid out as av-dome's (Pip, 9 Nov 2026): the stage is the TrainingStage experiment setting, a row of trainingStages
from 0 (Tom's stages 1 to 6 are rows 0 to 5), and each row says what the stage does, where the workflow used to test
the stage number; trialStructure holds the ITI, the drink time and the track lengths, punishment the timeout and error
sound, and rngSeed seeds every draw. The sweep and its loudness are src/waveforms/sweep.json's, made with the tone files
(`uv run generate-waveforms`), not the task file's (plan v11)."""

import json
import math
from pathlib import Path
from typing import List, Literal, Optional

from open_rigs.core.task import Task, TaskParameters
from pydantic import ConfigDict, Field, field_validator, model_validator
from swc.aeon.schema import BaseSchema

from ucl_open_coen_dome_navigation import __semver__

BEEP_INTERVAL_SEC = 0.05  # the tone pips are 50 ms apart on the Harp clock (Corridor.cs BeepSeconds)
LASER_MAX_MARGIN_SEC = 0.25  # how much longer than the longest trial (timeLimitSeconds + laser_to_stimulus_seconds) the laser_max_seconds cap must be

STAGE_COUNT = 6
SWEEP_FILE = Path(__file__).resolve().parents[1] / "waveforms" / "sweep.json"  # src/waveforms/sweep.json


def sweep() -> dict:
    """The sweep the committed tone files were made for: sweepStartHz, sweepEndHz, sweepSteps and pipPeak."""
    return json.loads(SWEEP_FILE.read_text(encoding="utf-8"))


class TrialStructure(BaseSchema):
    """Timing of each trial."""

    iti_min_seconds: float = Field(default=0.0, ge=0, description="Lower bound of the uniformly drawn inter-trial interval, in seconds")
    iti_max_seconds: float = Field(default=1.0, ge=0, description="Upper bound of the uniformly drawn inter-trial interval, in seconds")
    drink_seconds: float = Field(
        default=2.0,
        ge=0,
        description="After each outcome, and a punished trial's timeout, before the next trial's inter-trial interval begins, in seconds: time to drink (Tom Childs' fixed 2 s)",
    )
    track_lengths_cm: List[float] = Field(
        default=[80.0, 100.0, 120.0], min_length=1, description="Track lengths, one drawn per trial and scaled by the stage's trackLengthScale, in cm"
    )

    @model_validator(mode="after")
    def _iti_ordered(self) -> "TrialStructure":
        if self.iti_max_seconds < self.iti_min_seconds:
            raise ValueError("iti_max_seconds must be >= iti_min_seconds")
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
    """Where the stimulus switches off on a truncated trial (a stage's truncatedTrialFraction says how many).

    On a truncated trial the stimulus (tone and grating) switches off part way along the sweep. Everything else about
    the trial is unchanged, so it is still rewarded as normal: the animal has to keep going and find the reward
    zone without the stimulus.
    """

    sweep_fractions: List[float] = Field(
        default=[0.4, 0.6],
        min_length=1,
        description="Where the stimulus switches off, as a fraction of the sweep on a log scale, split evenly over truncated trials. The reward floor is at about 0.8",
    )

    @field_validator("sweep_fractions")
    @classmethod
    def _fractions_inside(cls, value: List[float]) -> List[float]:
        if any(not 0 < fraction < 1 for fraction in value):
            raise ValueError("Truncation sweep fractions must be between 0 and 1")
        return value


class OptogeneticsConfig(BaseSchema):
    """Zapit optogenetic stimulation.

    The stimulation sites and ramp-down are set in the Zapit conditions file loaded in Zapit, not here; the laser
    power is laser_power_mw, sent with every laser-on trial's StartStimulus (it overrides the file's power) and
    logged as laserPowerMw. Bonsai reads the same file to list its conditions (stimLocations01, stimLocations02, ...).
    Every trial of an optogenetics experiment sends Zapit a condition drawn at random: laser-on trials with the laser
    on, all others as laser-off trials with the laser off, so galvo noise and masking light are the same on every
    trial. "Stimulus" is the tone and grating; the laser is continuous: it starts at the trial's start (beep 0, the
    trigger line's rising edge on a hardware-triggered rig), laser_to_stimulus_seconds before the first tone pip, and
    Bonsai stops it at the trial's outcome, or after laser_max_seconds if that comes first. Zapit's host, port and
    triggering are rig.yml's zapit block.
    """

    # The same rule as validate_config_path's, in the JSON schema: rig-control checks the task file
    # against the schema, not this model, so a ticked Zapit with no config path was sent at Go and
    # the workflow stopped at trial 1 (audit, 9 Nov 2026, round 3). additionalProperties is spelt
    # out because rig-control closes every object that lists properties.
    model_config = ConfigDict(
        json_schema_extra={
            "if": {"properties": {"useZapit": {"const": True}}, "required": ["useZapit"], "additionalProperties": True},
            "then": {
                "properties": {"zapitConfigPath": {"minLength": 1}, "laserPowerMw": {"type": "number"}},
                "required": ["laserPowerMw"],
                "additionalProperties": True,
            },
        }
    )

    use_zapit: bool = Field(default=False, description="Whether Zapit runs this experiment: every trial sends it a condition, laser on or off, and rig-control checks Zapit is answering before Go")
    zapit_config_path: str = Field(
        default="",
        description="Path to the Zapit conditions file loaded in Zapit. Its top-level stimLocations01, stimLocations02, ... entries are the conditions: numbered 1 to N with no gaps, each with an offRampDownDuration_ms of 100 to 500 ms (the masking light goes out 1 s after the outcome)",
    )
    laser_power_mw: Optional[float] = Field(
        default=None,
        gt=0,
        description="Laser power on laser-on trials, in mW, the same for every condition: sent to Zapit with each StartStimulus (overriding the conditions file's power; Zapit's laser calibration turns it into volts) and logged as laserPowerMw (0 on laser-off trials). Required with optogenetics on",
    )
    laser_on_probability: float = Field(
        default=0.4,
        ge=0,
        le=1,
        description="Probability that a trial after trials_before_laser is a laser-on trial, shared equally by every condition in the Zapit conditions file, a laser-on control site included. All other trials are laser-off trials: Zapit is sent a condition with the laser off. Each trial's condition is drawn at random from all of them, laser on or off, independently of every other trial (no queue: one would make sequences predictable)",
    )
    laser_max_seconds: float = Field(
        default=12.5,
        gt=0,
        description="Longest the laser stays on in one trial, in seconds from its onset, timed by Bonsai; it then ramps down over the condition's offRampDownDuration_ms. A safety cap: the laser normally stops at the trial's outcome. Timed on the Harp clock, with a wall-clock backstop should the board stall. With optogenetics on it must be at least the longest stage timeLimitSeconds + laser_to_stimulus_seconds + 0.25 s, so it never ends a timed-out trial's laser early; on a stage without timeLimitSeconds a longer trial loses its laser before the outcome",
    )
    laser_to_stimulus_seconds: float = Field(
        default=0.1,
        ge=0.05,
        le=0.2,
        description="How long the laser is on before the stimulus (the first tone pip and grating frame) starts, in seconds: a multiple of the 50 ms beep, from 0.05 to 0.2. The first pip moves to beep laser_to_stimulus_seconds / 50 ms on every trial of an optogenetics experiment, laser-on or laser-off, so trial timing does not reveal laser-on trials. At most 0.2 s: licks before the first pip count toward the wrong-lick limits, and a longer lead could end a trial before its first pip",
    )
    trigger_hold_seconds: float = Field(
        default=1.0,
        ge=0.5,
        description="How long the trigger line, and the masking light on it, stays high after the outcome, in seconds: at least 0.5, the longest ramp-down a conditions file may have",
    )
    trials_before_laser: int = Field(
        default=20,
        ge=0,
        description="Trials at the experiment's start with no laser-on trial among them (each still sends Zapit a condition at 0 mW). laser_on_probability applies from the next trial",
    )

    @model_validator(mode="after")
    def validate_config_path(self):
        if self.use_zapit and not self.zapit_config_path:
            raise ValueError("use_zapit is on but zapit_config_path is empty")
        if self.use_zapit and self.laser_power_mw is None:
            raise ValueError("use_zapit is on but laser_power_mw is not set")
        if self.laser_power_mw is not None and not math.isfinite(self.laser_power_mw):
            raise ValueError(f"laser_power_mw ({self.laser_power_mw}) must be a finite number of mW")
        return self

    @model_validator(mode="after")
    def validate_lead_time(self):
        # Bonsai starts the laser on a beep and moves the first tone pip laser_to_stimulus_seconds later, so it is a whole number of beeps
        beeps = self.laser_to_stimulus_seconds / BEEP_INTERVAL_SEC
        if abs(beeps - round(beeps)) > 1e-6:
            raise ValueError(f"laser_to_stimulus_seconds ({self.laser_to_stimulus_seconds}) must be a multiple of the {BEEP_INTERVAL_SEC} s beep interval")
        return self

    @model_validator(mode="after")
    def validate_laser_max_finite(self):
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
    quiet_seconds: Optional[float] = Field(
        default=None,
        gt=0,
        description="After the inter-trial interval, the trial waits until the mouse has gone this long without licking, in seconds (uncapped), so licks for the last reward cannot carry into the next trial; null: no wait",
    )
    time_limit_seconds: Optional[float] = Field(
        default=None, gt=0, description="The longest a trial runs from its first tone pip, in seconds, after which it ends as a Timeout; null: no limit"
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

    modality: Literal["auditory", "visual", "audiovisual"] = Field(
        default="auditory", description="What the trial shows: the tone sweep (auditory), the grating (visual) or both (audiovisual)"
    )
    background_noise_level: float = Field(
        default=0.5, description="Constant background white noise RMS, as a fraction of the beep RMS (0 = off)", ge=0.0, le=1.0
    )
    position_step_cm: int = Field(default=1, description="The distance run is rounded to this before it sets the frequency, in cm", ge=1)
    trial_structure: TrialStructure = Field(default=TrialStructure(), description="Timing of each trial")
    punishment: Punishment = Field(default=Punishment(), description="The timeout and error sound a punished trial gets")
    truncation: Truncation = Field(default=Truncation(), description="Where the stimulus switches off on a truncated trial")
    optogenetics: OptogeneticsConfig = Field(default=OptogeneticsConfig(), description="Zapit optogenetic stimulation")
    training_stages: List[TrainingStage] = Field(
        description="Training stage table, indexed by the TrainingStage setting from 0; a setting past the end stops the workflow at Go"
    )

    @model_validator(mode="after")
    def _laser_max_outlasts_the_trial(self):
        # The laser is meant to stay on until the outcome: the cap must be longer than the longest timed trial, a stage's
        # time limit (timeLimitSeconds, counted from the first tone pip) plus laser_to_stimulus_seconds (zapit-v2)
        opto = self.optogenetics
        limits = [stage.time_limit_seconds for stage in self.training_stages if stage.time_limit_seconds is not None]
        if not opto.use_zapit or not limits:
            return self
        shortest = max(limits) + opto.laser_to_stimulus_seconds + LASER_MAX_MARGIN_SEC
        if opto.laser_max_seconds < shortest - 1e-9:
            raise ValueError(
                f"optogenetics.laser_max_seconds ({opto.laser_max_seconds} s) must be at least {shortest:g} s: the longest stage timeLimitSeconds "
                f"({max(limits):g} s) counts from the first tone pip, laser_to_stimulus_seconds ({opto.laser_to_stimulus_seconds} s) after onset"
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
    def _at_least_one_stage(cls, value: List[TrainingStage]) -> List[TrainingStage]:
        if not value:
            raise ValueError("training_stages must have at least one entry")
        return value


class UclOpenCoenDomeNavigationTaskLogic(Task):
    """Top-level task logic schema."""

    version: Literal[__semver__] = __semver__
    name: Literal["UclOpenCoenDomeNavigation"] = Field(default="UclOpenCoenDomeNavigation", description="Name of the task")
    task_parameters: UclOpenCoenDomeNavigationTaskParameters
