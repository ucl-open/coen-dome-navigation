"""The task file's schema: config/task.yml, which rig-control copies with the form's edits and the
workflow reads at Go (TaskPath). Ported from Tom Childs' auditory-vr-corridor task.py; what each
field does in the task is unchanged unless its description says otherwise."""

from typing import Literal, Optional

from pydantic import Field, model_validator
from swc.aeon.schema import BaseSchema

from ucl_open_coen_av_corridor import __semver__
from ucl_open_coen_av_corridor.helpers import calc_log_window

BEEP_INTERVAL_SEC = 0.05  # The tone pips are 50 ms apart on the Harp clock (every 50th Behavior board sample)
MAX_SHAPING_STAGE = 6


class Stage(BaseSchema):
    """Frequency boundaries for a single shaping stage."""

    floor: int = Field(description="Lower frequency threshold (Hz)", ge=1, le=25000)
    ceiling: int = Field(description="Upper frequency ceiling (Hz)", ge=1, le=25000)

    @model_validator(mode="after")
    def validate_floor_less_than_ceiling(self):
        if self.floor >= self.ceiling:
            raise ValueError("Reward freq floor must be less than ceiling")
        return self


class ThresholdFrequencies(BaseSchema):
    """Floor and ceiling frequencies of the reward zone for each shaping stage."""

    stage1: Stage
    stage2: Stage
    stage3: Stage
    stage4: Stage
    stage5: Stage


class PunishmentConfig(BaseSchema):
    """Punishment params."""

    timeout_sec: int = Field(default=2, description="Timeout duration in seconds")
    stage4_punished_lick: int = Field(default=8, description="Punished lick in stage 4", ge=1)
    stage5_punished_lick: int = Field(default=5, description="Punished lick in stage 5", ge=1)


class TruncationConfig(BaseSchema):
    """Truncated trials, used in stage 6 only.

    On a truncated trial the cue (tone and grating) switches off part way along the sweep. Everything else about
    the trial is unchanged, so it is still rewarded as normal: the animal has to keep going and find the reward
    zone without the cue.
    """

    fraction: float = Field(
        default=0.16,
        description="Fraction of stage 6 trials that are truncated, split evenly between the truncation points",
        ge=0.0,
        le=1.0,
    )
    points: list[float] = Field(
        default=[0.4, 0.6],
        description="Where the cue switches off, as a fraction of the sweep on a log scale. The reward floor is at about 0.8",
        min_length=1,
    )

    @model_validator(mode="after")
    def validate_points(self):
        if any(not 0 < point < 1 for point in self.points):
            raise ValueError("Truncation points must be between 0 and 1")
        return self


class EngagementConfig(BaseSchema):
    """How engagement is labelled: whether the animal is still attempting the task. Kept from Tom Childs'
    model, whose gen_session.py printed an engaged success rate beside the stage; the stage rules
    (stage.py) never used it, and nothing here reads it now.

    A mouse partway through a session often keeps running but stops licking. Those trials are not failures (it
    is not attempting the task), so they are labelled separately rather than scored as misses. Engagement is read
    from whether each trial contained a lick, smoothed over a window of trials.
    """

    roll_window: int = Field(
        default=25,
        description="Trials averaged around each trial when deciding engagement. Prefer an odd value",
        ge=1,
    )
    roll_threshold: float = Field(
        default=0.5, description="Lick fraction at or below which the animal counts as disengaged", ge=0.0, le=1.0
    )
    min_epoch: int = Field(
        default=10,
        description="Shortest run of trials that can be called engaged or disengaged; shorter runs are absorbed into their neighbours",
        ge=1,
        le=20,
    )


class OptogeneticsConfig(BaseSchema):
    """Zapit optogenetic stimulation. Field names follow the av-dome task schema.

    The stimulation sites, laser power and ramp-down are set in the Zapit stimulus config loaded in Zapit, not
    here. Bonsai reads the same file only to list its conditions (stimLocations01, stimLocations02, ...) and draws
    one per stimulated trial. On a stimulated trial the laser comes on lead_time before the first tone pip and is
    stopped at the trial outcome, or after stim_duration if that comes first. Zapit's host and port are rig.yml's
    zapit block.
    """

    enabled: bool = Field(
        default=False, description="Whether optogenetic stimulation runs this session. When false, Bonsai does not connect to Zapit"
    )
    zapit_config_path: str = Field(
        default="", description="Path to the Zapit stimulus config YAML loaded in Zapit, listing the stimulation conditions"
    )
    stim_probability: float = Field(default=0.75, ge=0.0, le=1.0, description="Probability that a trial receives stimulation")
    stim_duration: float = Field(
        default=12.0,
        gt=0,
        description="Longest the laser stays on in one trial, in seconds. It normally stops earlier, at the trial outcome",
    )
    lead_time: float = Field(
        default=0.1,
        description=(
            "How long before the first tone pip the laser comes on, in seconds. A multiple of the 50 ms beep interval, "
            "from 0.05 to 1. Applied to every trial of an optogenetics session, stimulated or not, so trial timing "
            "does not reveal which trials are stimulated"
        ),
    )
    pseudorandom_selection: bool = Field(
        default=True,
        description="Draw conditions from a shuffled queue, so each is used equally often, rather than independently each trial",
    )
    warmup_trials: int = Field(default=0, ge=0, description="Trials at session start with no stimulation")

    @model_validator(mode="after")
    def validate_lead_time(self):
        beeps = self.lead_time / BEEP_INTERVAL_SEC
        if abs(beeps - round(beeps)) > 1e-6 or not 1 <= round(beeps) <= 20:
            raise ValueError(
                f"lead_time ({self.lead_time}) must be a multiple of the {BEEP_INTERVAL_SEC} s beep interval, from 0.05 to 1 s"
            )
        if self.enabled and not self.zapit_config_path:
            raise ValueError("optogenetics is enabled but zapit_config_path is empty")
        return self


class UclOpenCoenAvCorridorTaskParameters(BaseSchema):
    """Task params."""

    modality: Literal["A", "V", "AV"] = Field(
        default="A", description="Stimulus modality: auditory (A), visual (V), or audiovisual (AV)"
    )
    shaping_stage: int = Field(
        default=1,
        description=(
            "Shaping stage (1-6). rig-control suggests one from the mouse's earlier experiments of this modality "
            "(stage.py); stage 6 (stage 5 with truncated trials) is only ever chosen by hand"
        ),
        ge=1,
        le=MAX_SHAPING_STAGE,
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
    center_freq: int = Field(default=18000, description="Center frequency of all reward windows (Hz)", ge=1, le=25000)
    reward_window_size: int = Field(
        default=6000,
        ge=1,
        le=25000,
        description="Window size around center_freq, setting the reward floor for stages 3-5 and the ceiling for stages 4-5 (Hz)",
    )
    threshold_frequencies: Optional[ThresholdFrequencies] = None
    reward_size_ul: float = Field(
        default=2.0,
        gt=0,
        description=(
            "Microlitres each reward gives, on the left spout's valve. The valve's calibration plans the pulses; "
            "an uncalibrated valve gives one pulse of rig.yml's width per 2 ul (av-dome's RewardLine)"
        ),
    )
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
    punishment: PunishmentConfig = PunishmentConfig()
    truncation: TruncationConfig = TruncationConfig()
    engagement: EngagementConfig = EngagementConfig()
    optogenetics: OptogeneticsConfig = OptogeneticsConfig()
    amplitude: float = Field(
        default=0.05,
        description="Audio amplitude (0.0 to 1.0). The tone files in src/waveforms are made for this value: the workflow stops at Go if they differ",
        ge=0.0,
        le=1.0,
    )
    background_noise_level: float = Field(
        default=0.5, description="Constant background white noise RMS, as a fraction of the beep RMS (0 = off)", ge=0.0, le=1.0
    )
    quantize_bin_size: int = Field(default=1, description="Bin size for frequency quantization (Hz)", ge=1)

    @model_validator(mode="after")
    def validate_start_less_than_end_freq(self):
        if self.start_freq >= self.end_freq:
            raise ValueError("start_freq must be less than end_freq")

        # Reward window with center_freq in the middle of the window in log space
        reward_floor, reward_ceiling = calc_log_window(center_freq=self.center_freq, window_size=self.reward_window_size)
        if reward_ceiling > self.end_freq:
            raise ValueError(
                f"reward_ceiling ({reward_ceiling}) exceeds end_freq ({self.end_freq}) - reduce reward_window_size or increase end_freq."
            )
        if reward_floor <= self.start_freq:
            raise ValueError(
                f"reward_floor ({reward_floor}) is not above start_freq ({self.start_freq}) - reduce reward_window_size or increase center_freq."
            )

        if self.threshold_frequencies is None:
            # Windows 'perceptually balanced' around center_freq in log space. The reward floor is the same from
            # stage 3 onwards and only the ceiling comes down: mice lick in anticipation of the reward frequency, so
            # moving the floor between stages invalidates the timing they have already learnt.
            self.threshold_frequencies = ThresholdFrequencies(
                stage1=Stage(floor=self.center_freq, ceiling=self.end_freq),  # rewarded on entering the zone, no lick needed
                stage2=Stage(floor=self.center_freq, ceiling=self.end_freq),
                stage3=Stage(floor=reward_floor, ceiling=self.end_freq),  # must lick, anywhere above the floor
                stage4=Stage(floor=reward_floor, ceiling=reward_ceiling),  # ceiling comes in; wrong licks punished
                stage5=Stage(floor=reward_floor, ceiling=reward_ceiling),  # same window, fewer wrong licks tolerated
            )  # Stage 6 is stage 5 with truncated trials, so it uses the stage 5 window and punishment
        return self


class UclOpenCoenAvCorridorTaskLogic(BaseSchema):
    """Top-level task logic schema."""

    version: Literal[__semver__] = __semver__
    name: Literal["UclOpenCoenAvCorridor"] = Field(default="UclOpenCoenAvCorridor", description="Name of the task")
    task_parameters: UclOpenCoenAvCorridorTaskParameters = UclOpenCoenAvCorridorTaskParameters()
