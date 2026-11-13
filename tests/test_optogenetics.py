"""The optogenetics settings' rules (zapit-v2): laser_to_stimulus_seconds in whole beeps up to 0.2 s, a laser_max_seconds cap longer than
the longest timed trial (a stage's timeLimitSeconds + laser_to_stimulus_seconds), and Corridor.cs checking the same rules."""
import copy
import re
from pathlib import Path

import pydantic
import pytest
import yaml

from ucl_open_coen_dome_navigation.task import (
    BEEP_INTERVAL_SEC,
    LASER_MAX_MARGIN_SEC,
    OptogeneticsConfig,
    UclOpenCoenDomeNavigationTaskParameters,
)

ROOT = Path(__file__).parent.parent
TASK = yaml.safe_load((ROOT / "config" / "task-abstract-av-corridor.yml").read_text(encoding="utf-8"))["taskParameters"]
CORRIDOR = (ROOT / "src" / "Extensions" / "Corridor.cs").read_text(encoding="utf-8-sig")


def params(time_limit=None, **opto):
    """The task file's parameters with optogenetics on, every timed stage's limit set to time_limit."""
    doc = copy.deepcopy(TASK)
    doc["optogenetics"].update({"useZapit": True, "zapitConfigPath": r"C:\Zapit\conditions.yml", "laserPowerMw": 2.0, **opto})
    if time_limit is not None:
        for row in doc["trainingStages"]:
            if row["timeLimitSeconds"] is not None:
                row["timeLimitSeconds"] = time_limit
    return UclOpenCoenDomeNavigationTaskParameters.model_validate(doc)


@pytest.mark.parametrize("seconds", [0.05, 0.1, 0.15, 0.2])
def test_lead_time_in_whole_beeps_up_to_0_2_is_accepted(seconds):
    assert OptogeneticsConfig(laser_to_stimulus_seconds=seconds).laser_to_stimulus_seconds == seconds


@pytest.mark.parametrize("seconds", [0.0, 0.07, 0.125, 0.25, 1.0])
def test_other_lead_times_are_refused(seconds):
    with pytest.raises(pydantic.ValidationError, match="laser_to_stimulus_seconds"):
        OptogeneticsConfig(laser_to_stimulus_seconds=seconds)


@pytest.mark.parametrize("limit, lead, cap", [(12, 0.1, 12.35), (12, 0.1, 12.5), (12, 0.2, 12.45), (20, 0.1, 20.35)])
def test_a_cap_longer_than_the_longest_trial_is_accepted(limit, lead, cap):
    assert params(limit, laserToStimulusSeconds=lead, laserMaxSeconds=cap).optogenetics.laser_max_seconds == cap


@pytest.mark.parametrize("limit, lead, cap", [(12, 0.1, 12.0), (12, 0.2, 12.4), (15, 0.1, 12.5)])
def test_a_cap_that_would_end_a_timed_out_trial_early_is_refused(limit, lead, cap):
    with pytest.raises(pydantic.ValidationError, match="laser_max_seconds"):
        params(limit, laserToStimulusSeconds=lead, laserMaxSeconds=cap)


def test_the_cap_rule_only_applies_with_optogenetics_on():
    doc = copy.deepcopy(TASK)
    doc["trainingStages"][3]["timeLimitSeconds"] = 30
    assert UclOpenCoenDomeNavigationTaskParameters.model_validate(doc).optogenetics.use_zapit is False


def test_laser_power_is_required_with_optogenetics_on_and_above_0():
    with pytest.raises(pydantic.ValidationError, match="laser_power_mw"):
        params(laserPowerMw=None)
    with pytest.raises(pydantic.ValidationError):
        params(laserPowerMw=0)
    assert OptogeneticsConfig().laser_power_mw is None


def test_the_trigger_hold_outlasts_the_longest_ramp_down():
    with pytest.raises(pydantic.ValidationError, match="trigger_hold_seconds"):
        OptogeneticsConfig(trigger_hold_seconds=0.4)


def test_corridor_cs_checks_the_same_rules():
    assert float(re.search(r"const double LaserMaxMarginSec = ([\d.]+);", CORRIDOR).group(1)) == LASER_MAX_MARGIN_SEC
    assert float(re.search(r"const double BeepSeconds = ([\d.]+);", CORRIDOR).group(1)) == BEEP_INTERVAL_SEC
    assert "Math.Round(beeps) <= 4" in CORRIDOR and "TimeLimitSeconds.HasValue" in CORRIDOR


def test_every_optogenetics_trial_has_a_condition_and_laser_off_is_0_mw():
    # Pip's decision 11: laser-off trials send their condition at 0 mW (galvos move, masking light on).
    assert "t.laserCondition = random.Next(setup.ConditionCount) + 1;" in CORRIDOR
    assert "t.laserPowerMw = t.laserOn ? opto.LaserPowerMw.Value : 0;" in CORRIDOR
    assert "LaserPower = (float)trial.laserPowerMw," in CORRIDOR and "LaserOn = trial.laserOn," in CORRIDOR


def test_the_laser_cap_s_code_says_laser_not_stimulus():
    # "Stimulus" is the tone and grating only (Pip, 9 Nov 2026); Zapit's own StimDuration wire name is exempt.
    for text in ((ROOT / "src" / "ucl_open_coen_dome_navigation" / "task.py").read_text(encoding="utf-8"), CORRIDOR):
        assert not re.search(r"stim_duration|StimDurationMargin|STIM_DURATION", text, re.IGNORECASE)
