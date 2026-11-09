"""The optogenetics settings' rules (zapit-v2): lead_time in whole beeps, a stim_duration cap longer than the
longest trial, stim_delay fixed at 0, and the constants the rules share with main.bonsai and InitialiseOptoTrial.cs."""
import re
import xml.etree.ElementTree as ET
from pathlib import Path

import pydantic
import pytest

from ucl_open_coen_dome_navigation.task import (
    BEEP_INTERVAL_SEC,
    MAX_TRIAL_TIME_SEC,
    STIM_DURATION_MARGIN_SEC,
    OptogeneticsConfig,
)

ROOT = Path(__file__).parent.parent


@pytest.mark.parametrize("lead_time", [0.05, 0.1, 0.15, 0.2])
def test_lead_time_in_whole_beeps_is_accepted(lead_time):
    assert OptogeneticsConfig(lead_time=lead_time, stim_duration=13.5).lead_time == lead_time


@pytest.mark.parametrize("lead_time", [0.0, 0.07, 0.125, 0.25, 0.5, 1.0])
def test_other_lead_times_are_refused(lead_time):
    with pytest.raises(pydantic.ValidationError, match="lead_time"):
        OptogeneticsConfig(lead_time=lead_time, stim_duration=13.5)


@pytest.mark.parametrize("lead_time, stim_duration", [(0.1, 12.35), (0.1, 12.5), (0.2, 12.45), (0.2, 13.0)])
def test_a_cap_longer_than_the_longest_trial_is_accepted(lead_time, stim_duration):
    assert OptogeneticsConfig(lead_time=lead_time, stim_duration=stim_duration).stim_duration == stim_duration


@pytest.mark.parametrize("lead_time, stim_duration", [(0.1, 12.0), (0.2, 12.4), (0.15, 12.3), (0.1, float("inf"))])
def test_a_cap_that_would_end_a_timeout_trial_early_is_refused(lead_time, stim_duration):
    with pytest.raises(pydantic.ValidationError, match="stim_duration"):
        OptogeneticsConfig(lead_time=lead_time, stim_duration=stim_duration)


def test_stim_delay_must_be_0():
    with pytest.raises(pydantic.ValidationError):
        OptogeneticsConfig(stim_delay=0.2)


def test_the_workflow_timeout_is_max_trial_time_sec():
    """The TimeSpanProperty feeding the MaxTrialTimeSec subject is the timeout the stim_duration rule assumes."""
    xsi = "{http://www.w3.org/2001/XMLSchema-instance}type"
    local = lambda tag: tag.rsplit("}", 1)[-1]
    found = []
    for wf in ET.parse(ROOT / "src" / "main.bonsai").getroot().iter():
        nodes = next((c for c in wf if local(c.tag) == "Nodes"), None)
        edges = next((c for c in wf if local(c.tag) == "Edges"), None)
        if nodes is None or edges is None:
            continue
        nodes = list(nodes)
        for i, node in enumerate(nodes):
            name = node.find("{*}Name")
            if "Subject" in (node.get(xsi) or "") and name is not None and name.text == "MaxTrialTimeSec":
                for e in edges:
                    if int(e.get("To")) == i:
                        value = nodes[int(e.get("From"))].find(".//{*}Value")
                        if value is not None:
                            found.append(float(re.fullmatch(r"PT([\d.]+)S", value.text).group(1)))
    assert found == [MAX_TRIAL_TIME_SEC]


def test_initialise_opto_trial_checks_the_same_rules():
    text = (ROOT / "src" / "Extensions" / "InitialiseOptoTrial.cs").read_text(encoding="utf-8")
    assert float(re.search(r"const double MaxTrialTimeSec = ([\d.]+);", text).group(1)) == MAX_TRIAL_TIME_SEC
    assert float(re.search(r"const double StimDurationMarginSec = ([\d.]+);", text).group(1)) == STIM_DURATION_MARGIN_SEC
    assert float(re.search(r"const double BeepIntervalSec = ([\d.]+);", text).group(1)) == BEEP_INTERVAL_SEC


def test_the_stimulus_config_is_saved_as_json():
    # LogDataSchema writes Settings\<Name>_<time>.json and parses its input as JSON, so the YAML goes in wrapped
    text = (ROOT / "src" / "main.bonsai").read_text(encoding="utf-8-sig")
    block = text[text.index("TaskParameters.Optogenetics.ZapitConfigPath"):text.index("<Name>Settings_ZapitStimConfig</Name>")]
    assert "ext:ZapitStimConfigSnapshot" in block and "ReadTextFile" not in block
