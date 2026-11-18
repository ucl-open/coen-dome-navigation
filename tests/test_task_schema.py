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
    STEP_SECONDS,
    LASER_MAX_MARGIN_SECONDS,
    CONDITION_FIELDS,
    TRIAL_TYPES,
    OptogeneticsSettings,
    UclOpenCoenDomeNavigationTaskLogic,
    UclOpenCoenDomeNavigationTaskParameters,
)

ROOT = Path(__file__).parent.parent
TASK = ROOT / "config" / "task-abstract-corridor.yml"
WAVES = ROOT / "src" / "sounds"
SCHEMA = ROOT / "src" / "DataSchemas" / "ucl_open_coen_dome_navigation_task.json"


def task() -> dict:
    return yaml.safe_load(TASK.read_text(encoding="utf-8"))


def test_the_task_file_validates():
    UclOpenCoenDomeNavigationTaskLogic.model_validate(task())


def test_the_stages_are_the_pipeline_s_and_the_file_has_stage_0_s_values():
    # The stages live in the lab's training pipeline on the share (rig-control writes the chosen stage's values over these
    # at launch); the file holds the defaults, stage 0's (Zone Reward, Drift), and an empty trainingPipeline.
    params = task()["taskParameters"]
    assert "trainingStages" not in params
    assert params["trainingPipeline"] == {"id": None, "label": None, "sha256": None}
    stage_0 = {"rewardFloorHz": 18000, "rewardCeilingHz": 25000, "autoRewardProbability": 1.0, "wrongLickLimit": 0, "lickQuietSeconds": 0,
               "responseWindowSeconds": None, "wheelGain": 8.0, "autoProgressPerSecond": 0.04, "stimulusOffProbability": 0.0}
    assert {key: params[key] for key in stage_0} == stage_0
    assert (params["trialStructure"]["wheelQuietSeconds"], params["trialStructure"]["wheelQuietCm"]) == (0, 0)


@pytest.mark.parametrize("pipeline", ["absent", {}, {"id": None, "label": None, "sha256": None}, {"id": 3, "label": "Ceiling In", "sha256": "ab12"}])
def test_training_pipeline_is_optional_in_the_model_and_the_schema(pipeline):
    # rig-control fills it at launch; Go (TrialLogic.cs CheckTask) refuses it missing or naming another stage.
    doc = task()
    if pipeline == "absent":
        del doc["taskParameters"]["trainingPipeline"]
    else:
        doc["taskParameters"]["trainingPipeline"] = pipeline
    UclOpenCoenDomeNavigationTaskLogic.model_validate(doc)
    assert list(jsonschema.Draft202012Validator(_closed(json.loads(SCHEMA.read_text(encoding="utf-8")))).iter_errors(doc)) == []


def test_a_reward_zone_past_the_sweep_s_end_is_refused():
    doc = task()
    doc["taskParameters"]["rewardCeilingHz"] = 25001
    with pytest.raises(pydantic.ValidationError, match="not inside the sweep"):
        UclOpenCoenDomeNavigationTaskLogic.model_validate(doc)


def test_the_task_file_s_conditions_are_its_old_weights():
    # trialConditions replaced trialWeights { visual: 0, auditory: 1, audiovisualCoherent: 0, ... }: one condition per type the
    # corridor shows (Setup edits only the weights), each set empty. One whole value (x-whole), no condition fields yet.
    params = task()["taskParameters"]
    assert "modality" not in params and "trialWeights" not in params
    assert params["trialConditions"] == [
        {"type": "visual", "weight": 0, "set": {}}, {"type": "auditory", "weight": 1, "set": {}}, {"type": "audiovisualCoherent", "weight": 0, "set": {}}]
    schema = json.loads(SCHEMA.read_text(encoding="utf-8"))
    assert "modality" not in SCHEMA.read_text(encoding="utf-8").lower()
    params_schema = schema["$defs"]["UclOpenCoenDomeNavigationTaskParameters"]
    assert params_schema["x-conditionFields"] == {} == CONDITION_FIELDS
    assert params_schema["properties"]["trialConditions"]["x-whole"] is True
    assert list(TRIAL_TYPES) == [t["type"] for t in params["trialConditions"]]


@pytest.mark.parametrize(
    "conditions, match",
    [
        ([], "at least 1 item"),
        ([{"type": "auditory", "weight": 0}], "weight above 0"),
        ([{"type": "auditory", "weight": -1}], "greater than or equal"),
        ([{"type": "auditory", "weight": 1000001}], "less than or equal"),
        ([{"type": "auditory", "weight": 1}, {"type": "neutral", "weight": 0}], r"trial_conditions\[1\] \(neutral\): its type is not one of visual, auditory, audiovisualCoherent"),
        ([{"type": "audiovisualConflict", "weight": 1}], "its type is not one of"),
        ([{"type": "visual", "weight": 1, "set": {"rewardSizeUl": 2}}], r"sets rewardSizeUl, which is not a condition field of visual"),
        ([{"type": "visual", "weight": 1, "colour": 1}], "Extra inputs"),
    ],
)
def test_conditions_need_a_weight_a_shown_type_and_an_empty_set(conditions, match):
    params = task()["taskParameters"]
    params["trialConditions"] = conditions
    with pytest.raises(pydantic.ValidationError, match=match):
        UclOpenCoenDomeNavigationTaskParameters.model_validate(params)


def test_a_condition_s_set_may_be_left_out_and_the_mix_is_required():
    params = task()["taskParameters"]
    params["trialConditions"] = [{"type": "audiovisualCoherent", "weight": 3}]
    assert UclOpenCoenDomeNavigationTaskParameters.model_validate(params).trial_conditions[0].set == {}
    del params["trialConditions"]
    with pytest.raises(pydantic.ValidationError, match="trialConditions"):
        UclOpenCoenDomeNavigationTaskParameters.model_validate(params)


def test_the_interval_runs_from_the_outcome_and_keeps_tom_s_timing():
    # As decision-making's, itiSeconds runs from the outcome (after any timeout) and includes the drink,
    # so Tom's fixed 2 s then 0 to 1 s is 2 to 3 s.
    structure = task()["taskParameters"]["trialStructure"]
    assert (structure["itiMinSeconds"], structure["itiMaxSeconds"]) == (2.0, 3.0) and "drinkSeconds" not in structure


def test_trial_structure_has_no_coded_default():
    # The task file gives the interval: a file without trialStructure is refused, not run on a default.
    params = task()["taskParameters"]
    del params["trialStructure"]
    with pytest.raises(pydantic.ValidationError, match="trialStructure"):
        UclOpenCoenDomeNavigationTaskParameters.model_validate(params)


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
    doc["taskParameters"]["penalty"]["sound"]["path"] = "sounds\\penalty.wav"
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
    assert params["reward"]["rewardSizeUl"] == 3.0


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
    [("trialStructure", "itiMaxSeconds", math.inf), ("stimulusOff", "trackFractions", [math.nan]), ("optogenetics", "laserMaxSeconds", math.inf)],
)
def test_the_task_s_own_blocks_refuse_inf_and_nan(block, key, value):
    # Each is a lib-open-rigs TaskBlock (allow_inf_nan off), as the lab's blocks are.
    doc = task()
    doc["taskParameters"][block][key] = value
    with pytest.raises(pydantic.ValidationError, match="finite number"):
        UclOpenCoenDomeNavigationTaskLogic.model_validate(doc)


@pytest.mark.parametrize(
    "key, value, match",
    [("stimuli", [], "at least 1"), ("stimuli", ["auditory", "auditory"], "must not repeat"), ("stimuli", ["tone"], "auditory"), ("trackFractions", [1.0], "between 0 and 1"), ("trackFractions", [], "at least 1")],
)
def test_a_bad_stimulus_off_block_is_refused(key, value, match):
    doc = task()
    doc["taskParameters"]["stimulusOff"][key] = value
    with pytest.raises(pydantic.ValidationError, match=match):
        UclOpenCoenDomeNavigationTaskLogic.model_validate(doc)


@pytest.mark.parametrize("key, value, pair", [("lickQuietSeconds", 10.5, None), ("wheelQuietSeconds", 10.5, ("wheelQuietCm", 1)), ("wheelQuietCm", 20.5, ("wheelQuietSeconds", 1))])
def test_a_quiet_wait_above_its_maximum_is_refused(key, value, pair):
    # A long wait and a licking mouse would hold the trial start with no message. The caps are the lab's (open_rigs.core.task
    # LickQuietWait at the top level, WheelQuietWait in trialStructure); TrialLogic.cs refuses them at Go too, through
    # OpenRigs.Core QuietWait.LickProblems and WheelProblems.
    doc = task()
    params = doc["taskParameters"]
    row = params if key == "lickQuietSeconds" else params["trialStructure"]
    row[key] = value
    if pair:
        row[pair[0]] = pair[1]
    with pytest.raises(pydantic.ValidationError, match="less than or equal to"):
        UclOpenCoenDomeNavigationTaskLogic.model_validate(doc)
    logic = (ROOT / "src" / "Extensions" / "TrialLogic.cs").read_text(encoding="utf-8-sig")
    assert "QuietWait.LickProblems" in logic and "QuietWait.WheelProblems" in logic


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


def test_training_stage_takes_any_pipeline_id_up_to_99():
    # TrainingStage carries the pipeline stage's id (ids are never reused); rig-control's page refuses one above 99.
    declared = re.search(r"<Name>TrainingStage</Name>.*?<Maximum>(\d+)</Maximum>", (ROOT / "src" / "main.bonsai").read_text(encoding="utf-8-sig"), re.S)
    assert int(declared.group(1)) == 99


def test_a_reward_over_the_cap_is_refused():
    # lib-open-rigs' MAX_REWARD_UL, RewardValve's MaxRewardUl.
    import copy

    from open_rigs.core.task import MAX_REWARD_UL

    from ucl_open_coen_dome_navigation.task import UclOpenCoenDomeNavigationTaskParameters

    params = yaml.safe_load((ROOT / "config" / "task-abstract-corridor.yml").read_text(encoding="utf-8"))["taskParameters"]
    ok = copy.deepcopy(params)
    ok["reward"]["rewardSizeUl"] = MAX_REWARD_UL
    UclOpenCoenDomeNavigationTaskParameters.model_validate(ok)
    ok["reward"]["rewardSizeUl"] = MAX_REWARD_UL + 1
    with pytest.raises(pydantic.ValidationError, match="less than or equal to 20"):
        UclOpenCoenDomeNavigationTaskParameters.model_validate(ok)

# The optogenetics settings' rules: laser_to_stimulus_seconds in whole steps up to 0.2 s, a laser_max_seconds cap
# longer than the longest trial (responseWindowSeconds + laser_to_stimulus_seconds), and TrialLogic.cs checking the same rules.

PARAMS = yaml.safe_load(TASK.read_text(encoding="utf-8"))["taskParameters"]
TRIAL_LOGIC = (ROOT / "src" / "Extensions" / "TrialLogic.cs").read_text(encoding="utf-8-sig")
FILE_OPTO = OptogeneticsSettings.model_validate(PARAMS["optogenetics"]).model_dump()


def opto(**fields) -> OptogeneticsSettings:
    """The task file's optogenetics block with fields changed: laserOnProbability, laserToStimulusSeconds and trialsBeforeLaser have no default."""
    return OptogeneticsSettings(**{**FILE_OPTO, **fields})


def params(window=None, **opto):
    """The task file's parameters with optogenetics on and this response window (None: the file's, none)."""
    doc = copy.deepcopy(PARAMS)
    doc["optogenetics"].update({"enabled": True, "zapitConditionsPath": r"C:\Zapit\conditions.yml", "laserPowerMw": 2.0, **opto})
    if window is not None:
        doc["responseWindowSeconds"] = window
    return UclOpenCoenDomeNavigationTaskParameters.model_validate(doc)


@pytest.mark.parametrize("seconds", [0.05, 0.1, 0.15, 0.2, 0.0, -0.05, -1.0])
def test_a_signed_lead_in_whole_steps_up_to_0_2_is_accepted(seconds):
    # Signed; negative puts the trigger after the first pip.
    assert opto(laser_to_stimulus_seconds=seconds).laser_to_stimulus_seconds == seconds


@pytest.mark.parametrize("seconds", [0.07, 0.125, -0.07, 0.25, 1.0])
def test_other_lead_times_are_refused(seconds):
    with pytest.raises(pydantic.ValidationError, match="laser_to_stimulus_seconds"):
        opto(laser_to_stimulus_seconds=seconds)


def test_a_trigger_past_the_response_window_is_refused():
    # A 12 s window from the first pip: the trigger at most 12 s after it; with no window, at most 2 s.
    assert params(12, laserToStimulusSeconds=-12.0).optogenetics.laser_to_stimulus_seconds == -12.0
    with pytest.raises(pydantic.ValidationError, match="after the first pip"):
        params(12, laserToStimulusSeconds=-12.05)
    assert params(laserToStimulusSeconds=-2.0).optogenetics.laser_to_stimulus_seconds == -2.0
    with pytest.raises(pydantic.ValidationError, match="after the first pip"):
        params(laserToStimulusSeconds=-2.05)


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
    doc["responseWindowSeconds"] = 30
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
    assert float(re.search(r"const double StepSeconds = ([\d.]+);", TRIAL_LOGIC).group(1)) == STEP_SECONDS
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
    ((), "wheelGain", 0), ((), "autoProgressPerSecond", -0.1), (("visualStimulus",), "spatialFrequencyEndCpd", 0),
    ((), "autoRewardProbability", 1.5), ((), "autoRewardProbability", -0.1),
    ((), "wrongLickLimit", -1), ((), "rewardFloorHz", 25000), (("reward",), "rewardSizeUl", 0),
    (("trialConditions", 0), "weight", -1)])
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


def test_the_task_file_dumps_back_unchanged():
    # The shared blocks (Iti, LickQuietWait, WheelQuietWait, ResponseWindow, AutoReward, RewardSize) keep every key where the file has it: the dump
    # holds each of the file's values (the model adds defaults the file leaves out), and dumps back the same again.
    def held(file, dumped, path=""):
        if isinstance(file, dict):
            for key, value in file.items():
                held(value, dumped[key], f"{path}.{key}")
        elif isinstance(file, list):
            assert len(file) == len(dumped), path
            for i, (f, d) in enumerate(zip(file, dumped)):
                held(f, d, f"{path}[{i}]")
        else:
            assert dumped == file, path

    dumped = UclOpenCoenDomeNavigationTaskLogic.model_validate(task()).model_dump(by_alias=True)
    held(task(), dumped)
    assert UclOpenCoenDomeNavigationTaskLogic.model_validate(dumped).model_dump(by_alias=True) == dumped
