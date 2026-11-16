import copy
import json
import math
import re
from pathlib import Path

import jsonschema
import numpy as np
import pydantic
import pytest
import yaml

from ucl_open_coen_dome_navigation import waveforms
from ucl_open_coen_dome_navigation.task import (
    BEEP_SECONDS,
    LASER_MAX_MARGIN_SECONDS,
    OptogeneticsSettings,
    UclOpenCoenDomeNavigationTaskLogic,
    UclOpenCoenDomeNavigationTaskParameters,
)

ROOT = Path(__file__).parent.parent
TASK = ROOT / "config" / "task-abstract-av-corridor.yml"
WAVES = ROOT / "src" / "waveforms"
SCHEMA = ROOT / "src" / "DataSchemas" / "ucl_open_coen_dome_navigation_task.json"


def task() -> dict:
    return yaml.safe_load(TASK.read_text(encoding="utf-8"))


def test_the_task_file_validates():
    UclOpenCoenDomeNavigationTaskLogic.model_validate(task())


def test_only_the_truncated_stage_has_a_name():
    labels = [row.get("label") for row in task()["taskParameters"]["trainingStages"]]
    assert labels == [None, None, None, None, None, "Truncated"]


def test_the_stage_rows_keep_tom_s_reward_zones():
    # Tom Childs' zones: from 18 kHz to the sweep's end at his stages 1-2, then a window centred on 18 kHz in
    # log space (6 kHz wide), its floor from stage 3 and its ceiling from stage 4 (rows 0-5 are his 1-6).
    floor, ceiling = 15248, 21248  # the 6 kHz log window centred on 18 kHz
    rows = task()["taskParameters"]["trainingStages"]
    assert len(rows) == 6
    zones = [(r["rewardFloorHz"], r["rewardCeilingHz"]) for r in rows]
    assert zones == [(18000, 25000)] * 2 + [(floor, 25000)] + [(floor, ceiling)] * 3


def test_the_response_window_and_quiet_wait_are_one_optional_field_each_per_stage():
    # A stage's responseWindowSeconds, null for none; the quiet waits (Pip, 13 Nov 2026), 0 for off.
    rows = task()["taskParameters"]["trainingStages"]
    assert [r["responseWindowSeconds"] for r in rows] == [None] * 3 + [12.0] * 3
    assert [r["lickQuietSeconds"] for r in rows] == [0] * 3 + [1.0] * 3
    assert all(r["wheelQuietSeconds"] == 0 and r["wheelQuietCm"] == 0 for r in rows)


def test_the_mouse_s_weights_are_auditory_alone_and_no_stage_sets_its_own():
    # Pip, 16 Nov 2026: top-level trialWeights, set per mouse on Setup and carried (a stage may set its own; none does).
    params = task()["taskParameters"]
    assert "modality" not in params
    assert params["trialWeights"] == {"visual": 0, "auditory": 1, "audiovisualCoherent": 0, "audiovisualConflict": 0, "neutral": 0}
    assert all("trialWeights" not in row for row in params["trainingStages"])
    model = UclOpenCoenDomeNavigationTaskParameters.model_validate(params)
    assert all(stage.trial_weights is None for stage in model.training_stages)
    assert "modality" not in SCHEMA.read_text(encoding="utf-8").lower()


WEIGHTS = {"visual": 0, "auditory": 1, "audiovisualCoherent": 0, "audiovisualConflict": 0, "neutral": 0}


@pytest.mark.parametrize(
    "change, match",
    [
        ({"auditory": 0}, "weight above 0"),
        ({"visual": -1}, "greater than or equal"),
        ({"audiovisualConflict": 1}, "audiovisual_conflict must be 0: that trial is not defined in this task yet"),
        ({"neutral": 2}, "neutral must be 0: that trial is not defined in this task yet"),
    ],
)
@pytest.mark.parametrize("where", ["task", "stage"])
def test_weights_all_zero_negative_or_for_an_undefined_trial_are_refused(change, match, where):
    params = task()["taskParameters"]
    weights = {**WEIGHTS, **change}
    if where == "task":
        params["trialWeights"] = weights
    else:
        params["trainingStages"][2]["trialWeights"] = weights
    with pytest.raises(pydantic.ValidationError, match=match):
        UclOpenCoenDomeNavigationTaskParameters.model_validate(params)


def test_a_stage_may_set_its_own_weights_and_a_missing_key_is_refused():
    params = task()["taskParameters"]
    params["trainingStages"][2]["trialWeights"] = {**WEIGHTS, "auditory": 0, "audiovisualCoherent": 1}
    assert UclOpenCoenDomeNavigationTaskParameters.model_validate(params).training_stages[2].trial_weights.audiovisual_coherent == 1
    del params["trainingStages"][2]["trialWeights"]["neutral"]
    with pytest.raises(pydantic.ValidationError, match="neutral"):
        UclOpenCoenDomeNavigationTaskParameters.model_validate(params)


def test_the_interval_runs_from_the_outcome_and_keeps_tom_s_timing():
    # As decision-making's, itiSeconds runs from the outcome (after any timeout) and includes the drink,
    # so Tom's fixed 2 s then 0 to 1 s is 2 to 3 s.
    structure = task()["taskParameters"]["trialStructure"]
    assert (structure["itiMinSeconds"], structure["itiMaxSeconds"]) == (2.0, 3.0) and "drinkSeconds" not in structure


def test_the_committed_waveforms_are_waveforms_py_s():
    assert json.loads((WAVES / "sweep.json").read_text(encoding="utf-8")) == waveforms.sweep()
    freqs = waveforms.tone_frequencies()
    assert [int(x) for x in (WAVES / "frequencies.txt").read_text(encoding="utf-8").split()] == list(freqs)
    committed = np.fromfile(WAVES / "all_waveforms.bin", dtype=np.float32).reshape(waveforms.SWEEP_STEPS, -1)
    assert np.array_equal(committed, waveforms.tones(freqs))
    assert committed.shape[1] == 2400  # the workflow's MatrixReader BufferLength
    assert np.array_equal(np.fromfile(WAVES / "reward_tone.bin", dtype=np.float32), waveforms.reward_tone()[0])


def test_the_penalty_sound_is_the_lab_s():
    # The penalty sound is OpenRigs.Audio's (PenaltySoundBuffer), so the task file names no file and
    # waveforms.py makes none.
    assert set(task()["taskParameters"]["penalty"]["sound"]) == {"enabled", "levelDbfs"}
    schema = json.loads(SCHEMA.read_text(encoding="utf-8"))
    assert set(schema["$defs"]["PenaltySound"]["properties"]) == {"enabled", "levelDbfs"}
    # rig-control checks the task file against the closed schema, so a file naming a sound path is refused.
    doc = task()
    doc["taskParameters"]["penalty"]["sound"]["path"] = "waveforms\\penalty.wav"
    errors = [e.message for e in jsonschema.Draft202012Validator(_closed(schema)).iter_errors(doc)]
    assert len(errors) == 1 and "'path'" in errors[0]


def _closed(node):
    """rig-control's taskfile.closed: every object that lists properties refuses others."""
    if isinstance(node, dict):
        node = {k: _closed(v) for k, v in node.items()}
        if isinstance(node.get("properties"), dict) and "additionalProperties" not in node:
            node["additionalProperties"] = False
    elif isinstance(node, list):
        node = [_closed(v) for v in node]
    return node


def test_the_schema_refuses_zapit_ticked_with_no_conditions_path():
    # rig-control checks the task file against the schema, not the model.
    validator = jsonschema.Draft202012Validator(_closed(json.loads(SCHEMA.read_text(encoding="utf-8"))))

    def problems(**opto):
        doc = task()
        doc["taskParameters"]["optogenetics"].update(opto)
        return [e.message for e in validator.iter_errors(doc)]

    assert problems() == []
    assert problems(enabled=True, laserPowerMw=2.0) == ["'' should be non-empty"]
    assert problems(enabled=True, zapitConditionsPath=r"C:\Zapit\conditions.yml") == ["None is not of type 'number'"]
    assert problems(enabled=True, zapitConditionsPath=r"C:\Zapit\conditions.yml", laserPowerMw=2.0) == []


def test_the_optogenetics_block_has_pip_s_names():
    names = ["enabled", "zapitConditionsPath", "laserPowerMw", "laserOnProbability", "laserMaxSeconds", "laserToStimulusSeconds",
             "triggerHoldSeconds", "trialsBeforeLaser"]
    assert list(task()["taskParameters"]["optogenetics"]) == names


def test_rig_control_keeps_the_zapit_file():
    # rig-control copies a task field ending in ConfigPath beside the task file at Go (taskfile.keep_files).
    assert [k for k in task()["taskParameters"]["optogenetics"] if k.endswith("Path")] == ["zapitConditionsPath"]


def test_the_task_file_s_optogenetics_and_reward_are_the_agreed_ones():
    params = task()["taskParameters"]
    opto = params["optogenetics"]
    assert (opto["laserOnProbability"], opto["trialsBeforeLaser"], opto["laserToStimulusSeconds"], opto["laserMaxSeconds"], opto["triggerHoldSeconds"]) == (0.4, 20, 0.1, 12.5, 1.0)
    assert {row["rewardSizeUl"] for row in params["trainingStages"]} == {3.0}


def test_a_trigger_hold_as_long_as_the_shortest_interval_is_refused():
    # In an optogenetics experiment the line is held triggerHoldSeconds after every outcome; the next trial raises it no
    # sooner than itiMinSeconds later. Without optogenetics the line stays low, so the hold is not checked.
    doc = task()
    doc["taskParameters"]["optogenetics"]["triggerHoldSeconds"] = 2.0
    UclOpenCoenDomeNavigationTaskLogic.model_validate(doc)
    doc["taskParameters"]["optogenetics"].update(enabled=True, zapitConditionsPath=r"C:\Zapit\conditions.yml", laserPowerMw=2.0)
    with pytest.raises(pydantic.ValidationError, match="trigger_hold_seconds"):
        UclOpenCoenDomeNavigationTaskLogic.model_validate(doc)


@pytest.mark.parametrize(
    "block, key, value",
    [("trialStructure", "itiMaxSeconds", math.inf), ("truncation", "sweepFractions", [math.nan]), ("optogenetics", "laserMaxSeconds", math.inf)],
)
def test_the_task_s_own_blocks_refuse_inf_and_nan(block, key, value):
    # Each is a lib-open-rigs TaskBlock (allow_inf_nan off), as the lab's blocks are.
    doc = task()
    doc["taskParameters"][block][key] = value
    with pytest.raises(pydantic.ValidationError, match="finite number"):
        UclOpenCoenDomeNavigationTaskLogic.model_validate(doc)


@pytest.mark.parametrize("key, value, pair", [("lickQuietSeconds", 10.5, None), ("wheelQuietSeconds", 10.5, ("wheelQuietCm", 1)), ("wheelQuietCm", 20.5, ("wheelQuietSeconds", 1))])
def test_a_quiet_wait_above_its_maximum_is_refused(key, value, pair):
    # Pip, 15 Nov 2026: a long wait and a licking mouse would hold the trial start with no message. The caps are the lab's
    # (open_rigs.core.task QuietWait); TrialLogic.cs refuses them at Go too, through OpenRigs.Core QuietWait.SettingsProblems.
    doc = task()
    row = doc["taskParameters"]["trainingStages"][4]
    row[key] = value
    if pair:
        row[pair[0]] = pair[1]
    with pytest.raises(pydantic.ValidationError, match="less than or equal to"):
        UclOpenCoenDomeNavigationTaskLogic.model_validate(doc)
    assert "QuietWait.SettingsProblems" in (ROOT / "src" / "Extensions" / "TrialLogic.cs").read_text(encoding="utf-8-sig")


def test_the_committed_task_schema_is_the_model_s():
    model = pydantic.RootModel[UclOpenCoenDomeNavigationTaskLogic].model_json_schema(
        by_alias=True, mode="serialization", union_format="primitive_type_array"
    )
    assert json.loads(SCHEMA.read_text(encoding="utf-8")) == json.loads(json.dumps(model))


def _property_names(node) -> set[str]:
    if isinstance(node, dict):
        return set(node.get("properties", {})) | {n for v in node.values() for n in _property_names(v)}
    return {n for v in node for n in _property_names(v)} if isinstance(node, list) else set()


def test_the_generated_csharp_has_every_schema_field():
    # A field added to the model and schema but not regenerated into C# stops the workflow at Go: run regenerate-schemas.
    generated = (ROOT / "src" / "Extensions" / "UclOpenCoenDomeNavigationTask.Generated.cs").read_text(encoding="utf-8")
    names = set(re.findall(r'JsonPropertyAttribute\("([^"]+)"', generated))
    assert names == _property_names(json.loads(SCHEMA.read_text(encoding="utf-8")))


def test_training_stage_s_maximum_is_the_task_file_s_last_row():
    # A row added to trainingStages is reachable only once ExperimentControl's TrainingStage Maximum (main.bonsai) says so.
    declared = re.search(r"<Name>TrainingStage</Name>.*?<Maximum>(\d+)</Maximum>", (ROOT / "src" / "main.bonsai").read_text(encoding="utf-8-sig"), re.S)
    assert int(declared.group(1)) == len(task()["taskParameters"]["trainingStages"]) - 1


def test_a_reward_over_the_cap_is_refused():
    # lib-open-rigs' MAX_REWARD_UL, RewardValve's MaxRewardUl.
    import copy

    from open_rigs.rigs.lab_rig import MAX_REWARD_UL

    from ucl_open_coen_dome_navigation.task import UclOpenCoenDomeNavigationTaskParameters

    params = yaml.safe_load((ROOT / "config" / "task-abstract-av-corridor.yml").read_text(encoding="utf-8"))["taskParameters"]
    ok = copy.deepcopy(params)
    ok["trainingStages"][0]["rewardSizeUl"] = MAX_REWARD_UL
    UclOpenCoenDomeNavigationTaskParameters.model_validate(ok)
    ok["trainingStages"][0]["rewardSizeUl"] = MAX_REWARD_UL + 1
    with pytest.raises(pydantic.ValidationError, match="less than or equal to 20"):
        UclOpenCoenDomeNavigationTaskParameters.model_validate(ok)

# The optogenetics settings' rules: laser_to_stimulus_seconds in whole beeps up to 0.2 s, a laser_max_seconds cap
# longer than the longest trial (a stage's responseWindowSeconds + laser_to_stimulus_seconds), and TrialLogic.cs checking the same rules.

PARAMS = yaml.safe_load(TASK.read_text(encoding="utf-8"))["taskParameters"]
TRIAL_LOGIC = (ROOT / "src" / "Extensions" / "TrialLogic.cs").read_text(encoding="utf-8-sig")
FILE_OPTO = OptogeneticsSettings.model_validate(PARAMS["optogenetics"]).model_dump()


def opto(**fields) -> OptogeneticsSettings:
    """The task file's optogenetics block with fields changed: laserOnProbability, laserToStimulusSeconds and trialsBeforeLaser have no default."""
    return OptogeneticsSettings(**{**FILE_OPTO, **fields})


def params(window=None, **opto):
    """The task file's parameters with optogenetics on, every stage with a response window given this one."""
    doc = copy.deepcopy(PARAMS)
    doc["optogenetics"].update({"enabled": True, "zapitConditionsPath": r"C:\Zapit\conditions.yml", "laserPowerMw": 2.0, **opto})
    if window is not None:
        for row in doc["trainingStages"]:
            if row["responseWindowSeconds"] is not None:
                row["responseWindowSeconds"] = window
    return UclOpenCoenDomeNavigationTaskParameters.model_validate(doc)


@pytest.mark.parametrize("seconds", [0.05, 0.1, 0.15, 0.2, 0.0, -0.05, -1.0])
def test_a_signed_lead_in_whole_beeps_up_to_0_2_is_accepted(seconds):
    # Pip, 13 Nov 2026: signed; negative puts the trigger after the first pip.
    assert opto(laser_to_stimulus_seconds=seconds).laser_to_stimulus_seconds == seconds


@pytest.mark.parametrize("seconds", [0.07, 0.125, -0.07, 0.25, 1.0])
def test_other_lead_times_are_refused(seconds):
    with pytest.raises(pydantic.ValidationError, match="laser_to_stimulus_seconds"):
        opto(laser_to_stimulus_seconds=seconds)


def test_a_trigger_past_every_stage_s_limit_is_refused():
    # Stages 3-5 limit trials to 12 s from the first pip; the furthest after it is the longest limit.
    assert params(laserToStimulusSeconds=-12.0).optogenetics.laser_to_stimulus_seconds == -12.0
    with pytest.raises(pydantic.ValidationError, match="past every stage"):
        params(laserToStimulusSeconds=-12.05)


def test_a_lead_needs_zapit_s_reply_margin_inside_the_shortest_interval():
    doc = copy.deepcopy(PARAMS)
    doc["trialStructure"]["itiMinSeconds"] = 0.35
    doc["optogenetics"]["laserToStimulusSeconds"] = 0.2
    with pytest.raises(pydantic.ValidationError, match="reply margin"):
        UclOpenCoenDomeNavigationTaskParameters.model_validate(doc)
    doc["optogenetics"]["laserToStimulusSeconds"] = 0.15
    UclOpenCoenDomeNavigationTaskParameters.model_validate(doc)


def test_the_trigger_line_falls_in_time_to_arm_the_next_trial():
    # itiMinSeconds 2.0 - laserToStimulusSeconds 0.1 - 0.2 s reply margin = 1.7 s.
    assert params(triggerHoldSeconds=1.7).optogenetics.trigger_hold_seconds == 1.7
    with pytest.raises(pydantic.ValidationError, match="trigger_hold_seconds"):
        params(triggerHoldSeconds=1.75)


@pytest.mark.parametrize("limit, lead, cap", [(12, 0.1, 12.35), (12, 0.1, 12.5), (12, 0.2, 12.45), (20, 0.1, 20.35)])
def test_a_cap_longer_than_the_longest_trial_is_accepted(limit, lead, cap):
    assert params(limit, laserToStimulusSeconds=lead, laserMaxSeconds=cap).optogenetics.laser_max_seconds == cap


@pytest.mark.parametrize("limit, lead, cap", [(12, 0.1, 12.0), (12, 0.2, 12.4), (15, 0.1, 12.5)])
def test_a_cap_that_would_end_the_laser_before_the_window_closes_is_refused(limit, lead, cap):
    with pytest.raises(pydantic.ValidationError, match="laser_max_seconds"):
        params(limit, laserToStimulusSeconds=lead, laserMaxSeconds=cap)


def test_the_cap_rule_only_applies_with_optogenetics_on():
    doc = copy.deepcopy(PARAMS)
    doc["trainingStages"][3]["responseWindowSeconds"] = 30
    assert UclOpenCoenDomeNavigationTaskParameters.model_validate(doc).optogenetics.enabled is False


def test_laser_power_is_required_with_optogenetics_on_and_above_0():
    with pytest.raises(pydantic.ValidationError, match="laser_power_mw"):
        params(laserPowerMw=None)
    with pytest.raises(pydantic.ValidationError):
        params(laserPowerMw=0)
    assert opto().laser_power_mw is None


def test_the_trigger_hold_outlasts_the_longest_ramp_down():
    with pytest.raises(pydantic.ValidationError, match="trigger_hold_seconds"):
        opto(trigger_hold_seconds=0.4)


def test_trial_logic_cs_uses_the_same_constants():
    # TrialLogic.cs's own checks of these rules, and the laser-off trials' 0 mW, are tests\Extensions.Tests' (TrialDrawTests.cs).
    assert float(re.search(r"const double LaserMaxMarginSeconds = ([\d.]+);", TRIAL_LOGIC).group(1)) == LASER_MAX_MARGIN_SECONDS
    assert float(re.search(r"const double BeepSeconds = ([\d.]+);", TRIAL_LOGIC).group(1)) == BEEP_SECONDS
    from ucl_open_coen_dome_navigation import task

    for py, cs in (("MAX_LEAD_SECONDS", "MaxLeadSeconds"), ("UNLIMITED_WINDOW_TRIGGER_AFTER_SECONDS", "UnlimitedWindowTriggerAfterSeconds")):
        assert float(re.search(rf"const double {cs} = ([\d.]+);", TRIAL_LOGIC).group(1)) == getattr(task, py)
    # Zapit's reply margin is lib-open-rigs' alone (ZapitTrial.ReplyMarginSeconds, ZAPIT_REPLY_MARGIN_SECONDS).
    assert "ZapitTrial.ReplyMarginSeconds" in TRIAL_LOGIC


def test_the_trigger_hold_is_at_least_lib_open_rigs_ramp_down():
    # The longest ramp-down is lib-open-rigs' ZAPIT_MAX_RAMP_DOWN_SECONDS (ZapitConditions.MaximumRampDownSeconds), not a typed 0.5.
    from open_rigs.components.zapit import ZAPIT_MAX_RAMP_DOWN_SECONDS

    assert OptogeneticsSettings.model_fields["trigger_hold_seconds"].metadata[0].ge == ZAPIT_MAX_RAMP_DOWN_SECONDS
    with pytest.raises(pydantic.ValidationError, match="greater than or equal to 0.5"):
        UclOpenCoenDomeNavigationTaskParameters.model_validate(params(triggerHoldSeconds=ZAPIT_MAX_RAMP_DOWN_SECONDS - 0.1))


@pytest.mark.parametrize("where, key, value", [
    ((), "positionStepCm", 0), (("trainingStages", 0), "wrongLickLimit", -1), (("trainingStages", 0), "rewardSizeUl", 0),
    (("trialWeights",), "visual", -1)])
def test_the_bounds_hold_with_the_lab_s_types(where, key, value):
    # The task model's types are decision-making's (Double, Bool, String; int, as open_rigs' Int drops a ge).
    data = task()["taskParameters"]
    block = data
    for step in where:
        block = block[step]
    block[key] = value
    with pytest.raises(pydantic.ValidationError):
        UclOpenCoenDomeNavigationTaskParameters.model_validate(data)
    source = (ROOT / "src" / "ucl_open_coen_dome_navigation" / "task.py").read_text(encoding="utf-8")
    assert not re.search(r"\w: (float|bool|str|Int)\b", source)


def test_the_backlog_has_the_pupil_gain_decided():
    # The Pupil camera's gain is 0 (Pip).
    text = (ROOT / "BACKLOG.md").read_text(encoding="utf-8")
    assert "gain stays 20" not in text and "Pupil camera's gain is 0" in text
