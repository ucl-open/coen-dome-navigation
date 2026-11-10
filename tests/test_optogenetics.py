"""The optogenetics settings' rules (zapit-v2): laser_to_stimulus_seconds in whole beeps up to 0.2 s, a laser_max_seconds cap longer than the
longest trial (trialStructure.timeLimitSeconds + laser_to_stimulus_seconds), and the places main.bonsai and
InitialiseOptoTrial.cs share those rules."""
import copy
import re
from pathlib import Path

import pydantic
import pytest
import yaml

from ucl_open_coen_dome_navigation.task import (
    BEEP_INTERVAL_SEC,
    STIM_DURATION_MARGIN_SEC,
    OptogeneticsConfig,
    UclOpenCoenDomeNavigationTaskParameters,
)

ROOT = Path(__file__).parent.parent
TASK = yaml.safe_load((ROOT / "config" / "task-abstract-av-corridor.yml").read_text(encoding="utf-8"))["taskParameters"]


def params(closed_loop=None, **opto):
    """The task file's parameters with optogenetics on and the given changes."""
    doc = copy.deepcopy(TASK)
    doc["optogenetics"].update({"useZapit": True, "zapitConfigPath": r"C:\Zapit\conditions.yml", "laserPowerMw": 2.0, **opto})
    if closed_loop is not None:
        doc["trialStructure"]["timeLimitSeconds"] = closed_loop
    return UclOpenCoenDomeNavigationTaskParameters.model_validate(doc)


@pytest.mark.parametrize("laser_to_stimulus_seconds", [0.05, 0.1, 0.15, 0.2])
def test_lead_time_in_whole_beeps_up_to_0_2_is_accepted(laser_to_stimulus_seconds):
    assert OptogeneticsConfig(laser_to_stimulus_seconds=laser_to_stimulus_seconds).laser_to_stimulus_seconds == laser_to_stimulus_seconds


@pytest.mark.parametrize("laser_to_stimulus_seconds", [0.0, 0.07, 0.125, 0.25, 0.5, 1.0])
def test_other_lead_times_are_refused(laser_to_stimulus_seconds):
    with pytest.raises(pydantic.ValidationError, match="laser_to_stimulus_seconds"):
        OptogeneticsConfig(laser_to_stimulus_seconds=laser_to_stimulus_seconds)


@pytest.mark.parametrize("closed_loop, laser_to_stimulus_seconds, laser_max_seconds", [(12, 0.1, 12.35), (12, 0.1, 12.5), (12, 0.2, 12.45), (20, 0.1, 20.35)])
def test_a_cap_longer_than_the_longest_trial_is_accepted(closed_loop, laser_to_stimulus_seconds, laser_max_seconds):
    assert params(closed_loop, laserToStimulusSeconds=laser_to_stimulus_seconds, laserMaxSeconds=laser_max_seconds).optogenetics.laser_max_seconds == laser_max_seconds


@pytest.mark.parametrize("closed_loop, laser_to_stimulus_seconds, laser_max_seconds", [(12, 0.1, 12.0), (12, 0.2, 12.4), (15, 0.1, 12.5)])
def test_a_cap_that_would_end_a_timed_out_trial_early_is_refused(closed_loop, laser_to_stimulus_seconds, laser_max_seconds):
    with pytest.raises(pydantic.ValidationError, match="laser_max_seconds"):
        params(closed_loop, laserToStimulusSeconds=laser_to_stimulus_seconds, laserMaxSeconds=laser_max_seconds)


def test_the_cap_rule_only_applies_with_optogenetics_on():
    doc = copy.deepcopy(TASK)
    doc["trialStructure"]["timeLimitSeconds"] = 30
    assert UclOpenCoenDomeNavigationTaskParameters.model_validate(doc).optogenetics.use_zapit is False


def test_laser_max_seconds_must_be_finite():
    with pytest.raises(pydantic.ValidationError, match="laser_max_seconds"):
        OptogeneticsConfig(laser_max_seconds=float("inf"))


def test_the_workflow_time_limit_is_closed_loop_duration():
    """MaxTrialTimeSec, the timeout the cap rule assumes, comes from trialStructure.timeLimitSeconds."""
    text = (ROOT / "src" / "main.bonsai").read_text(encoding="utf-8-sig")
    assert re.search(r"TimeSpan\.FromSeconds\(it\.TrialStructure\.TimeLimitSeconds\)", text)


def test_initialise_opto_trial_checks_the_same_rules():
    text = (ROOT / "src" / "Extensions" / "InitialiseOptoTrial.cs").read_text(encoding="utf-8")
    assert float(re.search(r"const double StimDurationMarginSec = ([\d.]+);", text).group(1)) == STIM_DURATION_MARGIN_SEC
    assert float(re.search(r"const double BeepIntervalSec = ([\d.]+);", text).group(1)) == BEEP_INTERVAL_SEC
    assert "TrialStructure.TimeLimitSeconds" in text and "Math.Round(beeps) <= 4" in text


def test_the_stimulus_config_is_saved_as_json():
    # LogDataSchema writes Settings\<Name>_<time>.json and parses its input as JSON, so the YAML goes in wrapped
    text = (ROOT / "src" / "main.bonsai").read_text(encoding="utf-8-sig")
    block = text[text.index("TaskParameters.Optogenetics.ZapitConfigPath"):text.index("<Name>Settings_ZapitConditions</Name>")]
    assert "ext:ZapitConditionsSnapshot" in block and "ReadTextFile" not in block


def test_laser_power_is_required_with_optogenetics_on_and_above_0():
    with pytest.raises(pydantic.ValidationError, match="laser_power_mw"):
        params(laserPowerMw=None)
    with pytest.raises(pydantic.ValidationError):
        params(laserPowerMw=0)
    assert params(laserPowerMw=3.5).optogenetics.laser_power_mw == 3.5
    assert OptogeneticsConfig().laser_power_mw is None  # off: not needed


def test_both_start_stimulus_commands_send_the_trial_s_power_and_the_row_logs_it():
    text = (ROOT / "src" / "main.bonsai").read_text(encoding="utf-8")
    assert text.count('<Property Name="LaserPower" Selector="OptogeneticLaserPowerMw" />') == 1
    assert text.count('<Property Name="LaserPower" Selector="Item1.OptogeneticLaserPowerMw" />') == 1
    assert text.count("<Combinator xsi:type=\"ext:CreateStartStimulus\">") == 2
    assert "OptogeneticLaserPowerMw as laserPowerMw" in text and "optoStimulated" not in text
