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
    LIST_FIELDS,
    REQUIRED,
    SESSION_FIELDS,
    RULES,
    OptogeneticsSettings,
    TRIAL_TYPES,
    sweep,
    track_end_hz,
    UclOpenCoenDomeNavigationTaskLogic,
    UclOpenCoenDomeNavigationTaskParameters,
)
from open_rigs.rigs import conditioned_task as ct
from open_rigs.rigs.conditioned_task import MAX_WEIGHT_SUM, session_field
from open_rigs.rigs.conditions import MAX_COMBINATIONS, flatten, unused_paths

ROOT = Path(__file__).parent.parent
TASK = ROOT / "config" / "task-abstract-corridor.yml"
WAVES = ROOT / "src" / "sounds"
SCHEMA = ROOT / "src" / "DataSchemas" / "ucl_open_coen_dome_navigation_task.json"


def task() -> dict:
    return yaml.safe_load(TASK.read_text(encoding="utf-8"))


def test_the_task_file_validates():
    UclOpenCoenDomeNavigationTaskLogic.model_validate(task())


def test_the_stages_are_the_curriculum_s_and_the_file_has_stage_0_s_values():
    # The stages live in the lab's Mouse Curriculum on the share (app-rig-control writes the chosen stage's values over these
    # at launch); the file holds the defaults, stage 0's (Zone Reward, Drift), and an empty curriculum.
    params = task()["taskParameters"]
    assert "trainingStages" not in params
    assert params["curriculum"] is None
    stage_0 = {"rewardZoneMinHz": 18000, "rewardZoneMaxHz": 24000, "autoRewardProbability": 1.0, "wrongLickLimit": 0}
    assert {key: params[key] for key in stage_0} == stage_0
    structure = params["trialStructure"]
    assert (structure["lickQuietSeconds"], structure["responseWindowSeconds"], structure["wheelQuietSeconds"], structure["wheelQuietCm"]) == (0, None, 0, 0)
    assert (params["track"]["wheelGain"], params["track"]["autoProgressPerSecond"], params["stimulusOff"]["stimuli"]) == (8.0, 0.04, "none")
    assert not {"rewardFloorHz", "rewardCeilingHz", "stimulusOffProbability", "contrasts", "wheelGain", "backgroundNoise"} & set(params)


def test_curriculum_comes_second_in_the_lab_s_block_order():
    # The lab's BLOCK_ORDER: app-rig-control's form and the task file list the blocks in it.
    assert ct.block_order_problems(UclOpenCoenDomeNavigationTaskParameters) == []
    assert list(task()["taskParameters"])[:2] == ["rngSeed", "curriculum"]


@pytest.mark.parametrize("curriculum", ["absent", None, {"id": 3, "code": "S03A", "version": "k7f2", "path": "A", "label": "Ceiling In", "sha256": "ab12"}])
def test_curriculum_is_optional_in_the_model_and_the_schema(curriculum):
    # app-rig-control fills it at launch; Go (TrialLogic.cs CheckTask) refuses it missing or malformed.
    doc = task()
    if curriculum == "absent":
        del doc["taskParameters"]["curriculum"]
    else:
        doc["taskParameters"]["curriculum"] = curriculum
    UclOpenCoenDomeNavigationTaskLogic.model_validate(doc)
    assert list(jsonschema.Draft202012Validator(_closed(json.loads(SCHEMA.read_text(encoding="utf-8")))).iter_errors(doc)) == []


def test_a_reward_zone_past_the_sweep_s_end_is_refused():
    doc = task()
    doc["taskParameters"]["rewardZoneMaxHz"] = 25001
    with pytest.raises(pydantic.ValidationError, match="not inside the track's tones"):
        UclOpenCoenDomeNavigationTaskLogic.model_validate(doc)


def test_a_reward_zone_above_the_track_s_top_is_refused():
    # The track's top is startHz * floor(endHz / startHz) = 24 kHz (Sweep.TrackEndHz), below the sweep's 25 kHz end: the tone
    # never gets above it, so a zone to 25 kHz could not be reached. 24 kHz itself is accepted.
    made = sweep()
    assert track_end_hz(made) == 24000 and made["endHz"] == 25000
    doc = task()
    doc["taskParameters"]["rewardZoneMaxHz"] = 24000
    UclOpenCoenDomeNavigationTaskLogic.model_validate(doc)
    doc["taskParameters"]["rewardZoneMaxHz"] = 25000
    with pytest.raises(pydantic.ValidationError, match="not inside the track's tones"):
        UclOpenCoenDomeNavigationTaskLogic.model_validate(doc)
    doc["taskParameters"].update(rewardZoneMinHz=made["startHz"], rewardZoneMaxHz=24000)
    with pytest.raises(pydantic.ValidationError, match="not inside the track's tones"):
        UclOpenCoenDomeNavigationTaskLogic.model_validate(doc)


def test_the_task_file_s_conditions_show_what_their_old_types_did():
    # The trial-type split (18 Nov 2026): each condition says what it shows (visualStimulus.contrasts 0 no grating,
    # auditoryStimulus.levelDbfs mute no tone) over the task's contrast 1 and level, so each draws what its type drew; the label,
    # derived from them, names it. Weights as before (visual 0, auditory 1, audiovisualCoherent 0). One whole value (x-whole).
    params = task()["taskParameters"]
    assert "modality" not in params and "trialWeights" not in params
    assert params["trialConditions"] == [
        {"weight": 0, "set": {"auditoryStimulus.levelDbfs": "mute"}},
        {"weight": 1, "set": {"visualStimulus.contrasts": [0]}},
        {"weight": 0, "set": {}},
    ]
    assert params["visualStimulus"]["contrasts"] == [1.0]
    assert params["auditoryStimulus"] == {"levelDbfs": -55, "elevationDeg": 0, "initialAzimuthsDeg": ["all"]}
    model = UclOpenCoenDomeNavigationTaskParameters.model_validate(params)
    assert [model.derive_trial_type(c) for c in model.trial_conditions] == ["visual", "auditory", "audiovisualCoherent"]
    schema = json.loads(SCHEMA.read_text(encoding="utf-8"))
    assert "modality" not in SCHEMA.read_text(encoding="utf-8").lower()
    params_schema = schema["$defs"]["UclOpenCoenDomeNavigationTaskParameters"]
    # app-rig-control reads both dome tasks one way: x-session-fields what a condition may not set (TaskSchema.SessionFields,
    # a C# test holds it to the schema), x-rules the rules, x-trial-types the names, x-required-conditions none (no target),
    # x-max-combinations the cap; every array but an x-whole one is a list field, one entry per combination.
    assert params_schema["x-session-fields"] == list(SESSION_FIELDS) and "x-condition-fields" not in params_schema
    assert params_schema["x-required-conditions"] == list(REQUIRED) == []
    assert params_schema["x-max-combinations"] == MAX_COMBINATIONS == 1024
    assert list(LIST_FIELDS) == ["auditoryStimulus.initialAzimuthsDeg", "stimulusOff.trackFractions", "track.lengthsCm", "visualStimulus.contrasts"]
    assert "x-whole" not in schema["$defs"]["StimulusOff"]["properties"]["stimuli"]
    # Every path a rule or trial type names is a task field, and none a session field; every session field is one.
    leaves = set(flatten(model.model_dump(by_alias=True, mode="json")))
    named = {path for entry in [*RULES, *TRIAL_TYPES] for path in [*entry.get("if", {}).get("properties", {}), *entry.get("unused", [])]}
    assert named <= leaves and {session_field(path, SESSION_FIELDS) for path in named} == {None}
    assert {session_field(leaf, SESSION_FIELDS) for leaf in leaves} - {None} == set(SESSION_FIELDS)
    # initialAzimuthsDeg's items a number or "all", oneOf as decision-making's (Bonsai.Sgen's List<object>).
    azimuths = schema["$defs"]["AuditoryStimulus"]["properties"]["initialAzimuthsDeg"]
    assert azimuths["default"] == ["all"] and azimuths["items"] == {"oneOf": [{"type": "number"}, {"const": "all", "type": "string"}]}
    assert params_schema["x-rules"] == list(RULES)
    assert params_schema["x-trial-types"] == list(TRIAL_TYPES)
    assert params_schema["properties"]["trialConditions"]["x-whole"] is True
    # The trialType is derived, never stored: a condition has no trialType property and no TrialType enum is generated.
    assert "TrialType" not in schema["$defs"] and "trialType" not in schema["$defs"]["TrialCondition"]["properties"]


def test_a_task_file_expresses_a_visual_path_and_an_auditory_path():
    # The curriculum's path A visual, path B auditory: a stage's conditions say what its trials show, whatever their trialType.
    params = task()["taskParameters"]
    params["trialConditions"] = [{"weight": 1, "set": {"auditoryStimulus.levelDbfs": "mute", "visualStimulus.contrasts": [0.5, 1.0]}}]
    UclOpenCoenDomeNavigationTaskParameters.model_validate(params)
    params["trialConditions"] = [{"weight": 1, "set": {"visualStimulus.contrasts": [0], "auditoryStimulus.levelDbfs": [-50, -40]}}]
    model = UclOpenCoenDomeNavigationTaskParameters.model_validate(params)
    assert model.derive_trial_type(model.trial_conditions[0]) == "auditory"


@pytest.mark.parametrize("set_, trial_type", [
    ({}, "audiovisualCoherent"), ({"visualStimulus.contrasts": [0]}, "auditory"), ({"auditoryStimulus.levelDbfs": "mute"}, "visual"),
    ({"visualStimulus.contrasts": [0], "auditoryStimulus.levelDbfs": "mute"}, "neutral"),
    ({"visualStimulus.contrasts": [0, 0.5], "auditoryStimulus.levelDbfs": ["mute", -40]}, "audiovisualCoherent"),
    ({"visualStimulus.contrasts": [0, 0.5]}, "audiovisualCoherent"), ({"visualStimulus.contrasts": [0, 0], "auditoryStimulus.levelDbfs": ["mute", -40]}, "auditory"),
    ({"auditoryStimulus.initialAzimuthsDeg": [0]}, "audiovisualCoherent"), ({"visualStimulus.contrasts": [0], "auditoryStimulus.initialAzimuthsDeg": ["all"]}, "auditory"),
    ({"visualStimulus.contrasts": [0.5], "auditoryStimulus.levelDbfs": ["mute", "mute"]}, "visual"),
    ({"visualStimulus.contrasts": [0], "auditoryStimulus.levelDbfs": -30}, "auditory"),
    ({"visualStimulus.contrasts": [0.5], "auditoryStimulus.levelDbfs": -30}, "audiovisualCoherent"),
    ({"stimulusOff.stimuli": "both"}, "audiovisualCoherentTruncated"), ({"stimulusOff.stimuli": "auditory", "visualStimulus.contrasts": [0]}, "auditoryTruncated"),
    ({"stimulusOff.stimuli": "visual", "auditoryStimulus.levelDbfs": "mute"}, "visualTruncated"), ({"stimulusOff.stimuli": "none", "visualStimulus.contrasts": [0]}, "auditory"),
    ({"stimulusOff.stimuli": "both", "visualStimulus.contrasts": [0], "auditoryStimulus.levelDbfs": "mute"}, "neutral")])
def test_the_trial_type_is_derived_from_what_the_condition_shows(set_, trial_type):
    # A grating shows at a contrast above 0 and a tone sounds at a number (ct.SOUNDING), not at mute: both audiovisualCoherent,
    # one alone visual or auditory, neither neutral; a list counts as shown when any entry shows.
    model = UclOpenCoenDomeNavigationTaskParameters.model_validate({**task()["taskParameters"], "trialConditions": [{"weight": 1, "set": set_}]})
    assert model.derive_trial_type(model.trial_conditions[0]) == trial_type


def test_a_stored_trial_type_is_refused():
    # P9: the trialType is derived at Go, never stored, so a condition naming one is an unknown key.
    params = task()["taskParameters"]
    params["trialConditions"] = [{"trialType": "audiovisualConflict", "weight": 1, "set": {}}]
    with pytest.raises(pydantic.ValidationError, match="trialType"):
        UclOpenCoenDomeNavigationTaskParameters.model_validate(params)


def test_the_rules_say_what_goes_unread():
    # The corridor's rules only say what goes unread (open_rigs.rigs.conditions evaluates them).
    values = UclOpenCoenDomeNavigationTaskParameters.model_validate(task()["taskParameters"]).model_dump(by_alias=True, mode="json")
    values["visualStimulus"]["contrasts"] = [0, 0]
    values.update(autoRewardProbability=0.5)
    assert unused_paths(RULES, values) == {"visualStimulus.spatialFrequencyStartCpd", "visualStimulus.spatialFrequencyEndCpd", "stimulusOff.trackFractions"}
    values["auditoryStimulus"]["levelDbfs"] = "mute"
    values["stimulusOff"]["stimuli"] = "both"
    values["autoRewardProbability"] = 1
    assert unused_paths(RULES, values) == {"visualStimulus.spatialFrequencyStartCpd", "visualStimulus.spatialFrequencyEndCpd", "auditoryStimulus.initialAzimuthsDeg",
                                           "auditoryStimulus.elevationDeg", "wrongLickLimit", "rewardZoneMaxHz"}


@pytest.mark.parametrize("stimuli", ["none", "auditory", "visual", "both"])
def test_a_condition_may_set_what_the_stimulus_off_trials_switch_off(stimuli):
    # stimulusOff.stimuli is one condition-level value (no stimulusOffProbability): only at none are the track fractions unread (x-rules).
    params = task()["taskParameters"]
    params["trialConditions"] = [{"weight": 1, "set": {"stimulusOff.stimuli": stimuli}}]
    model = UclOpenCoenDomeNavigationTaskParameters.model_validate(params)
    values = model.model_dump(by_alias=True, mode="json")
    values["stimulusOff"]["stimuli"] = stimuli
    assert ("stimulusOff.trackFractions" in unused_paths(RULES, values)) == (stimuli == "none")
    rule = next(r for r in RULES if r["unused"] == ["stimulusOff.trackFractions"])
    assert rule["if"] == ct.if_({"stimulusOff.stimuli": {"const": "none"}})
    assert "stimulusOff.stimuli" not in LIST_FIELDS and "stimulusOffProbability" not in task()["taskParameters"]


def test_a_stimulus_off_value_outside_the_four_is_refused():
    params = task()["taskParameters"]
    params["trialConditions"] = [{"weight": 1, "set": {"stimulusOff.stimuli": "tone"}}]
    with pytest.raises(pydantic.ValidationError, match="stimulusOff"):
        UclOpenCoenDomeNavigationTaskParameters.model_validate(params)


@pytest.mark.parametrize(
    "conditions, match",
    [
        ([], "at least 1 item"),
        ([{"weight": 0}], "weight above 0"),
        ([{"weight": -1}], "greater than or equal"),
        ([{"weight": 1000001}], "less than or equal"),
        ([{"weight": 1, "set": {"sizeUl": 2}}], r"sets or pairs sizeUl, which is not a task field"),
        ([{"weight": 1, "set": {"rngSeed": 2}}], r"sets or pairs rngSeed, a session field \(x-session-fields: rngSeed "),
        ([{"weight": 1, "set": {"optogenetics.enabled": True}}], r"sets or pairs optogenetics.enabled, a session field \(x-session-fields: optogenetics.enabled "),
        ([{"weight": 1, "set": {"optogenetics.laserMaxSeconds": 1}}], r"sets or pairs optogenetics.laserMaxSeconds, which is not a task field"),
        ([{"weight": 1, "set": {"optogenetics.laserOnProbability": 1}}], r"sets optogenetics.laserOnProbability with optogenetics off"),
        ([{"weight": 1, "set": {"reward.sizeUl": 0}}], r"(?s)with \{.*'reward.sizeUl': 0.*greater than 0"),
        ([{"weight": 1, "set": {"autoRewardProbability": 2}}], r"less than or equal to 1"),
        ([{"weight": 1, "set": {"auditoryStimulus.initialAzimuthsDeg": [200]}}], r"outside \[-180, 180\]"),
        ([{"weight": 1, "set": {"auditoryStimulus.initialAzimuthsDeg": ["left"]}}], r"Input should be 'all'"),
        ([{"weight": 1, "colour": 1}], "Extra inputs"),
        ([{"weight": 1, "set": {"auditoryStimulus.levelDbfs": 0}}], r"(?s)trial_conditions\[0\] \(audiovisualCoherent\) with .*less than or equal to -1"),
        ([{"weight": 1, "set": {"visualStimulus.contrasts": [0.5, -1]}}], r"(?s)with \{.*'visualStimulus.contrasts': \[-1\].*contrasts must each be 0 or more"),
        ([{"weight": 1, "set": {"visualStimulus.contrasts": []}}], "visualStimulus.contrasts is an empty list"),
        ([{"weight": 1, "set": {"visualStimulus.contrasts": [0.5, 1], "auditoryStimulus.levelDbfs": [-40]}, "pairs": [["visualStimulus.contrasts", "auditoryStimulus.levelDbfs"]]}],
         "paired lists must be equally long"),
        ([{"weight": 1, "pairs": [["visualStimulus.contrasts"]]}], "each pairs group needs two paths or more"),
        # Every list field expands too: the task's 3 track lengths and 2 stimulus-off fractions times the set's 1 x 171.
        ([{"weight": 1, "set": {"visualStimulus.contrasts": [1], "auditoryStimulus.levelDbfs": [-120 + i / 100 for i in range(171)]}}],
         r"expands into 1026 combinations, more than 1024 \(x-max-combinations\)"),
        # Counted before any is built, as TrialDraw.BuildConditions does: 6 x 1000^3 combinations would take hours and all
        # memory to build.
        ([{"weight": 1, "set": {"auditoryStimulus.levelDbfs": [-120 + i / 1000 for i in range(1000)], "autoRewardProbability": [i / 1000 for i in range(1000)],
                                                      "reward.sizeUl": [1 + i / 1000 for i in range(1000)]}}],
         r"expands into 6000000000 combinations"),
    ],
)
def test_conditions_need_a_weight_and_what_they_set_to_run(conditions, match):
    params = task()["taskParameters"]
    params["trialConditions"] = conditions
    with pytest.raises(pydantic.ValidationError, match=match):
        UclOpenCoenDomeNavigationTaskParameters.model_validate(params)


def test_a_condition_may_set_any_field_but_a_session_field_and_every_list_expands():
    params = task()["taskParameters"]
    params["trialConditions"] = [{"weight": 1, "set": {"track.wheelGain": 2, "track.lengthsCm": [40, 60], "penalty.timeoutSeconds": 2,
                                                                              "visualStimulus.spatialFrequencyEndCpd": 0.6, "rewardZoneMinHz": 15000}}]
    UclOpenCoenDomeNavigationTaskParameters.model_validate(params)
    # 171 levels times the task's 6 track and fraction picks is 1026; 170 is 1020, within the cap.
    params["trialConditions"] = [{"weight": 1, "set": {"auditoryStimulus.levelDbfs": [-120 + i / 100 for i in range(170)]}}]
    UclOpenCoenDomeNavigationTaskParameters.model_validate(params)


def test_combinations_are_ordered_by_path_the_first_changing_fastest():
    # TrialDraw.BuildConditions' order, Settings_Combinations' rows.
    model = UclOpenCoenDomeNavigationTaskParameters.model_validate(task()["taskParameters"])
    combos = ct.condition_combinations(model.model_dump(by_alias=True, mode="json"), {"visualStimulus.contrasts": [0.5, 1]}, [], LIST_FIELDS)
    assert len(combos) == 12 and combos[:3] == [
        {"auditoryStimulus.initialAzimuthsDeg": ["all"], "stimulusOff.trackFractions": [0.4], "track.lengthsCm": [80.0], "visualStimulus.contrasts": [0.5]},
        {"auditoryStimulus.initialAzimuthsDeg": ["all"], "stimulusOff.trackFractions": [0.6], "track.lengthsCm": [80.0], "visualStimulus.contrasts": [0.5]},
        {"auditoryStimulus.initialAzimuthsDeg": ["all"], "stimulusOff.trackFractions": [0.4], "track.lengthsCm": [100.0], "visualStimulus.contrasts": [0.5]}]


def test_the_committed_task_schema_cs_is_the_schema_s():
    # TrialDraw reads x-session-fields, x-max-combinations, the list fields and x-trial-types from TaskSchema, generated with the schema.
    assert (ROOT / "src" / "Extensions" / "TaskSchema.Generated.cs").read_text(encoding="utf-8") == ct.task_schema_cs(SESSION_FIELDS, LIST_FIELDS, TRIAL_TYPES)


def test_weights_may_add_to_the_draw_s_int_and_no_more():
    # Go refuses the same sum (OpenRigs.Trials TrialConditions.Build's int.MaxValue): 2147 conditions at the cap and one of 483647 make it exactly.
    assert MAX_WEIGHT_SUM == 2**31 - 1
    params = task()["taskParameters"]
    params["trialConditions"] = [{"weight": 1000000}] * 2147 + [{"weight": 483647}]
    UclOpenCoenDomeNavigationTaskParameters.model_validate(params)
    params["trialConditions"][-1]["weight"] = 483648
    with pytest.raises(pydantic.ValidationError, match=f"weights add to more than {MAX_WEIGHT_SUM}"):
        UclOpenCoenDomeNavigationTaskParameters.model_validate(params)


def test_a_condition_s_set_may_be_left_out_and_the_mix_is_required():
    params = task()["taskParameters"]
    params["trialConditions"] = [{"weight": 3}]
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
    freqs = waveforms.pip_frequencies()
    assert not (WAVES / "frequencies.txt").exists() and not (WAVES / "all_waveforms.bin").exists()
    committed = np.fromfile(WAVES / "pips.bin", dtype=np.float32).reshape(waveforms.SWEEP_PIP_COUNT, -1)
    assert np.array_equal(committed, waveforms.pips(freqs))
    assert committed.shape[1] == 2400  # the workflow's MatrixReader BufferLength
    # Each pip is stored at sweep.json's pipLevelDbfs, -58.1 dBFS RMS: the level the pips played at before the
    # trial-type split. Then SpeakerBuffer scaled Tom's pips (0.05 squared peak, -58.06) to -58.1; the files are those
    # samples now, to float32's precision (PipSetTests holds the laid-out buffers equal too).
    rms_dbfs = 20 * np.log10(np.sqrt(np.mean(committed.astype(np.float64) ** 2, axis=1)))
    assert np.allclose(rms_dbfs, -58.1, atol=1e-5)
    duration = waveforms.PIP_MS / 1000
    t = np.linspace(0, duration, 2400, endpoint=False)
    toms = np.stack([(np.sin(2 * np.pi * f * t) * np.sin(np.pi * t / duration) * 0.05**2).astype(np.float32) for f in freqs]).astype(np.float64)
    before = toms * 10 ** ((-58.1 - 20 * np.log10(np.sqrt(np.mean(toms**2, axis=1, keepdims=True)))) / 20)
    assert np.max(np.abs(committed - before)) < 1e-6 * np.max(np.abs(before))
    assert np.array_equal(np.fromfile(WAVES / "reward_tone.bin", dtype=np.float32), waveforms.reward_tone()[0])


def test_the_sweep_s_frequencies_are_the_c_sharp_sweep_s():
    # The pips' frequencies (no frequencies.txt now): Sweep.cs computes round(startHz * (endHz / startHz) ** (i / (pipCount - 1))),
    # and the committed pips are waveforms.pips() of waveforms.pip_frequencies(); sweep.json holds the rest of the sweep.
    made = sweep()
    assert set(made) == {"startHz", "endHz", "pipCount", "pipLevelDbfs", "sampleRate"}
    freqs = [round(made["startHz"] * (made["endHz"] / made["startHz"]) ** (i / (made["pipCount"] - 1))) for i in range(made["pipCount"])]
    assert list(waveforms.pip_frequencies()) == freqs and freqs[0] == made["startHz"] and freqs[-1] == made["endHz"]
    committed = np.fromfile(WAVES / "pips.bin", dtype=np.float32).reshape(made["pipCount"], -1)
    assert np.array_equal(committed, waveforms.pips(np.array(freqs), made["pipLevelDbfs"]))
    assert committed.shape[1] == made["sampleRate"] * waveforms.PIP_MS // 1000


def test_the_penalty_sound_is_the_lab_s():
    # The penalty sound is OpenRigs.Audio's (PenaltySoundBuffer), so the task file names no file and
    # waveforms.py makes none; it plays at levelDbfs (mute: none), with no enabled switch.
    assert set(task()["taskParameters"]["penalty"]["sound"]) == {"levelDbfs"}
    schema = json.loads(SCHEMA.read_text(encoding="utf-8"))
    assert set(schema["$defs"]["PenaltySound"]["properties"]) == {"levelDbfs"}
    # app-rig-control checks the task file against the closed schema, so a file naming a sound path is refused.
    doc = task()
    doc["taskParameters"]["penalty"]["sound"]["path"] = "sounds\\penalty.wav"
    errors = [e.message for e in jsonschema.Draft202012Validator(_closed(schema)).iter_errors(doc)]
    assert len(errors) == 1 and "'path'" in errors[0]


def _closed(node):
    """app-rig-control's taskfile.closed: every object that lists properties refuses others."""
    if isinstance(node, dict):
        node = {k: _closed(v) for k, v in node.items()}
        if isinstance(node.get("properties"), dict) and "additionalProperties" not in node:
            node["additionalProperties"] = False
    elif isinstance(node, list):
        node = [_closed(v) for v in node]
    return node


def test_the_schema_refuses_zapit_ticked_with_no_conditions_path():
    # app-rig-control checks the task file against the schema, not the model.
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
    # No laserMaxSeconds (the cap is derived at Go) and no triggerHoldSeconds (read from the Zapit conditions file in C#).
    names = ["enabled", "zapitConditionsPath", "laserPowerMw", "laserOnProbability", "laserToStimulusSeconds", "trialsBeforeLaser"]
    assert list(task()["taskParameters"]["optogenetics"]) == names
    schema = json.loads(SCHEMA.read_text(encoding="utf-8"))
    assert not {"laserMaxSeconds", "triggerHoldSeconds"} & set(schema["$defs"]["OptogeneticsSettings"]["properties"])


def test_rig_control_keeps_the_zapit_file():
    # app-rig-control copies a task field ending in ConfigPath beside the task file at Go (taskfile.keep_files).
    assert [k for k in task()["taskParameters"]["optogenetics"] if k.endswith("Path")] == ["zapitConditionsPath"]


def test_the_task_file_s_optogenetics_and_reward_are_the_agreed_ones():
    params = task()["taskParameters"]
    opto = params["optogenetics"]
    assert (opto["laserOnProbability"], opto["trialsBeforeLaser"], opto["laserToStimulusSeconds"]) == (0.4, 20, 0.1)
    assert params["reward"] == {"sizeUl": 3.0, "largeMultiplier": 2, "largeProbability": 0}


def test_optogenetics_without_a_response_window_is_refused():
    # The laser's cap is derived at Go from responseWindowSeconds (TrialRules' LaserMaxSeconds): a window that never closes has none.
    doc = task()
    assert doc["taskParameters"]["trialStructure"]["responseWindowSeconds"] is None
    doc["taskParameters"]["optogenetics"].update(enabled=True, zapitConditionsPath=r"C:\Zapit\conditions.yml", laserPowerMw=2.0)
    with pytest.raises(pydantic.ValidationError, match="optogenetics needs trial_structure.response_window_seconds"):
        UclOpenCoenDomeNavigationTaskLogic.model_validate(doc)
    doc["taskParameters"]["trialStructure"]["responseWindowSeconds"] = 12
    UclOpenCoenDomeNavigationTaskLogic.model_validate(doc)


@pytest.mark.parametrize(
    "block, key, value",
    [("trialStructure", "itiMaxSeconds", math.inf), ("stimulusOff", "trackFractions", [math.nan]), ("optogenetics", "laserToStimulusSeconds", math.inf)],
)
def test_the_task_s_own_blocks_refuse_inf_and_nan(block, key, value):
    # Each is a lib-open-rigs TaskBlock (allow_inf_nan off), as the lab's blocks are.
    doc = task()
    doc["taskParameters"][block][key] = value
    with pytest.raises(pydantic.ValidationError, match="finite number"):
        UclOpenCoenDomeNavigationTaskLogic.model_validate(doc)


@pytest.mark.parametrize(
    "key, value, match",
    [("stimuli", ["auditory"], "'none', 'auditory', 'visual' or 'both'"), ("stimuli", "tone", "'none', 'auditory', 'visual' or 'both'"),
     ("trackFractions", [1.0], "between 0 and 1"), ("trackFractions", [], "at least 1")],
)
def test_a_bad_stimulus_off_block_is_refused(key, value, match):
    doc = task()
    doc["taskParameters"]["stimulusOff"][key] = value
    with pytest.raises(pydantic.ValidationError, match=match):
        UclOpenCoenDomeNavigationTaskLogic.model_validate(doc)


@pytest.mark.parametrize("key, value, pair", [("lickQuietSeconds", 10.5, None), ("wheelQuietSeconds", 10.5, ("wheelQuietCm", 1)), ("wheelQuietCm", 20.5, ("wheelQuietSeconds", 1))])
def test_a_quiet_wait_above_its_maximum_is_refused(key, value, pair):
    # A long wait and a licking mouse would hold the trial start with no message. The caps are the lab's (open_rigs.core.task
    # LickQuietWait and WheelQuietWait, both in trialStructure); TrialLogic.cs refuses them at Go too, through
    # OpenRigs.Core QuietWait.LickProblems and WheelProblems.
    doc = task()
    row = doc["taskParameters"]["trialStructure"]
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
        # x-rules and x-trial-types hold subschemas over dotted paths, not fields.
        return set(node.get("properties", {})) | {n for k, v in node.items() if not k.startswith("x-") for n in _property_names(v)}
    return {n for v in node for n in _property_names(v)} if isinstance(node, list) else set()


def test_the_generated_csharp_has_every_schema_field():
    # A field added to the model and schema but not regenerated into C# stops the workflow at Go: run regenerate-schemas.
    generated = (ROOT / "src" / "Extensions" / "UclOpenCoenDomeNavigationTask.Generated.cs").read_text(encoding="utf-8")
    names = set(re.findall(r'JsonPropertyAttribute\("([^"]+)"', generated))
    assert names == _property_names(json.loads(SCHEMA.read_text(encoding="utf-8")))


@pytest.mark.parametrize("key, bad", [("id", -1), ("code", "S3A"), ("code", "s03a"), ("code", "S03AB"), ("version", "k7f"), ("version", "k7f1"), ("version", "K7F2"),
                                      ("path", "a"), ("path", "AB"), ("label", None), ("sha256", None)])
def test_a_malformed_curriculum_is_refused_by_the_model_and_the_schema(key, bad):
    # The only record of the stage, exactly the fields app-rig-control writes, each checked; no TrainingStage parameter.
    doc = task()
    doc["taskParameters"]["curriculum"] = {**{"id": 3, "code": "S03A", "version": "k7f2", "path": "A", "label": "Ceiling In", "sha256": "ab12"}, key: bad}
    with pytest.raises(pydantic.ValidationError):
        UclOpenCoenDomeNavigationTaskLogic.model_validate(doc)
    assert list(jsonschema.Draft202012Validator(_closed(json.loads(SCHEMA.read_text(encoding="utf-8")))).iter_errors(doc)) != []
    assert "TrainingStage" not in (ROOT / "src" / "main.bonsai").read_text(encoding="utf-8-sig")


def test_a_reward_over_the_cap_is_refused():
    # lib-open-rigs' MAX_REWARD_UL, RewardValve's MaxRewardUl.
    import copy

    from open_rigs.core.task import MAX_REWARD_UL

    from ucl_open_coen_dome_navigation.task import UclOpenCoenDomeNavigationTaskParameters

    params = yaml.safe_load((ROOT / "config" / "task-abstract-corridor.yml").read_text(encoding="utf-8"))["taskParameters"]
    ok = copy.deepcopy(params)
    ok["reward"]["sizeUl"] = MAX_REWARD_UL
    UclOpenCoenDomeNavigationTaskParameters.model_validate(ok)
    ok["reward"]["sizeUl"] = MAX_REWARD_UL + 1
    with pytest.raises(pydantic.ValidationError, match="less than or equal to 20"):
        UclOpenCoenDomeNavigationTaskParameters.model_validate(ok)


def test_a_large_reward_over_the_cap_is_refused():
    # sizeUl x largeMultiplier is what a large-reward trial asks the valve for: over MAX_REWARD_UL (20 ul) it is refused,
    # unless largeProbability is 0 and no trial is a large one.
    from open_rigs.core.task import MAX_REWARD_UL

    assert MAX_REWARD_UL == 20
    params = task()["taskParameters"]
    params["reward"] = {"sizeUl": 15, "largeMultiplier": 2, "largeProbability": 0.5}
    with pytest.raises(pydantic.ValidationError, match=r"reward.size_ul \(15.0 ul\) x reward.large_multiplier \(2.0\) is more than 20"):
        UclOpenCoenDomeNavigationTaskParameters.model_validate(params)
    params["reward"]["largeProbability"] = 0
    UclOpenCoenDomeNavigationTaskParameters.model_validate(params)
    params["reward"].update(largeProbability=0.5, largeMultiplier=20 / 15)
    UclOpenCoenDomeNavigationTaskParameters.model_validate(params)
    params["reward"]["largeMultiplier"] = 1
    with pytest.raises(pydantic.ValidationError, match="greater than 1"):
        UclOpenCoenDomeNavigationTaskParameters.model_validate(params)

# The optogenetics settings' rules: laser_to_stimulus_seconds in whole steps up to 0.2 s, a response window for the laser's
# derived cap, and TrialLogic.cs checking the same rules.

PARAMS = yaml.safe_load(TASK.read_text(encoding="utf-8"))["taskParameters"]
TRIAL_LOGIC = (ROOT / "src" / "Extensions" / "TrialLogic.cs").read_text(encoding="utf-8-sig")
FILE_OPTO = OptogeneticsSettings.model_validate(PARAMS["optogenetics"]).model_dump()


def opto(**fields) -> OptogeneticsSettings:
    """The task file's optogenetics block with fields changed: laserOnProbability, laserToStimulusSeconds and trialsBeforeLaser have no default."""
    return OptogeneticsSettings(**{**FILE_OPTO, **fields})


def params(window=None, **opto):
    """The task file's parameters with optogenetics on and this response window (None: the file's, none: refused)."""
    doc = copy.deepcopy(PARAMS)
    doc["optogenetics"].update({"enabled": True, "zapitConditionsPath": r"C:\Zapit\conditions.yml", "laserPowerMw": 2.0, **opto})
    if window is not None:
        doc["trialStructure"]["responseWindowSeconds"] = window
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
    # A 12 s window from the first pip: the trigger at most 12 s after it; with no window (optogenetics off), at most 2 s.
    assert params(12, laserToStimulusSeconds=-12.0).optogenetics.laser_to_stimulus_seconds == -12.0
    with pytest.raises(pydantic.ValidationError, match="after the first pip"):
        params(12, laserToStimulusSeconds=-12.05)
    doc = copy.deepcopy(PARAMS)
    doc["optogenetics"]["laserToStimulusSeconds"] = -2.0
    assert UclOpenCoenDomeNavigationTaskParameters.model_validate(doc).optogenetics.laser_to_stimulus_seconds == -2.0
    doc["optogenetics"]["laserToStimulusSeconds"] = -2.05
    with pytest.raises(pydantic.ValidationError, match="after the first pip"):
        UclOpenCoenDomeNavigationTaskParameters.model_validate(doc)


def test_a_lead_needs_zapit_s_reply_margin_inside_the_shortest_interval():
    doc = copy.deepcopy(PARAMS)
    doc["trialStructure"]["itiMinSeconds"] = 0.35
    doc["optogenetics"]["laserToStimulusSeconds"] = 0.2
    with pytest.raises(pydantic.ValidationError, match="reply margin"):
        UclOpenCoenDomeNavigationTaskParameters.model_validate(doc)
    doc["optogenetics"]["laserToStimulusSeconds"] = 0.15
    UclOpenCoenDomeNavigationTaskParameters.model_validate(doc)


def test_the_window_rule_only_applies_with_optogenetics_on():
    # The laser cap, derived at Go from the window, needs one only when the laser runs.
    doc = copy.deepcopy(PARAMS)
    assert doc["trialStructure"]["responseWindowSeconds"] is None
    assert UclOpenCoenDomeNavigationTaskParameters.model_validate(doc).optogenetics.enabled is False
    doc["trialStructure"]["responseWindowSeconds"] = 30
    assert UclOpenCoenDomeNavigationTaskParameters.model_validate(doc).optogenetics.enabled is False


def test_laser_power_is_required_with_optogenetics_on_and_above_0():
    with pytest.raises(pydantic.ValidationError, match="laser_power_mw"):
        params(laserPowerMw=None)
    with pytest.raises(pydantic.ValidationError):
        params(laserPowerMw=0)
    assert opto().laser_power_mw is None


def test_trial_logic_cs_uses_the_same_constants():
    # TrialLogic.cs's own checks of these rules, and the laser-off trials' 0 mW, are tests\Extensions.Tests' (TrialDrawTests.cs).
    assert float(re.search(r"const double LaserMaxMarginSeconds = ([\d.]+);", TRIAL_LOGIC).group(1)) == LASER_MAX_MARGIN_SECONDS
    assert float(re.search(r"const double StepSeconds = ([\d.]+);", TRIAL_LOGIC).group(1)) == STEP_SECONDS
    from ucl_open_coen_dome_navigation import task

    for py, cs in (("MAX_LEAD_SECONDS", "MaxLeadSeconds"), ("UNLIMITED_WINDOW_TRIGGER_AFTER_SECONDS", "UnlimitedWindowTriggerAfterSeconds")):
        assert float(re.search(rf"const double {cs} = ([\d.]+);", TRIAL_LOGIC).group(1)) == getattr(task, py)
    # Zapit's reply margin is lib-open-rigs' alone (ZapitTrial.ReplyMarginSeconds, ZAPIT_REPLY_MARGIN_SECONDS).
    assert "ZapitTrial.ReplyMarginSeconds" in TRIAL_LOGIC


def test_the_laser_cap_and_trigger_hold_are_not_task_settings():
    # The cap is derived at Go (TrialDraw.LaserMaxSeconds, LASER_MAX_MARGIN_SECONDS past the longest window plus lead) and the
    # trigger hold is read from the Zapit conditions file (its longest ramp-down) in C#: neither is a model field or a Python rule.
    assert not {"laser_max_seconds", "trigger_hold_seconds"} & set(OptogeneticsSettings.model_fields)
    assert "LaserMaxSeconds" in TRIAL_LOGIC and "draw.MaxRampDownSeconds" in TRIAL_LOGIC


@pytest.mark.parametrize("where, key, value", [
    (("track",), "wheelGain", 0), (("track",), "autoProgressPerSecond", -0.1), (("visualStimulus",), "spatialFrequencyEndCpd", 0),
    ((), "autoRewardProbability", 1.5), ((), "autoRewardProbability", -0.1),
    ((), "wrongLickLimit", -1), ((), "rewardZoneMinHz", 0), ((), "rewardZoneMaxHz", 0), (("reward",), "sizeUl", 0),
    (("reward",), "largeMultiplier", 1), (("reward",), "largeProbability", 1.5), (("auditoryStimulus",), "levelDbfs", 0),
    (("auditoryStimulus",), "levelDbfs", -121), (("auditoryStimulus",), "levelDbfs", "loud"),
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
    # The shared blocks (Iti, LickQuietWait, WheelQuietWait, ResponseWindow, Reward) keep every key where the file has it: the dump
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


def test_a_condition_sets_its_reward_auto_reward_and_where_the_pips_play():
    params = task()["taskParameters"]
    params["trialConditions"] = [{"weight": 1, "set": {
        "visualStimulus.contrasts": [0], "reward.sizeUl": [2, 4], "autoRewardProbability": 0, "auditoryStimulus.initialAzimuthsDeg": [-60, "all"]}}]
    UclOpenCoenDomeNavigationTaskParameters.model_validate(params)
    assert params["auditoryStimulus"]["initialAzimuthsDeg"] == ["all"]   # the task file's: every speaker, as before


def test_every_sound_s_level_is_a_condition_s_and_the_plan_s_values():
    # Every sound's level is condition-level (P19), none a session field; the task file's levels are the lab's 5 dB steps
    # (stimulus -55, reward tone -55, penalty -45) and the background noise has no task setting (the lab's -60).
    assert not {"rewardTone", "penalty", "auditoryStimulus"} & {path.split(".")[0] for path in SESSION_FIELDS}
    params = task()["taskParameters"]
    assert (params["auditoryStimulus"]["levelDbfs"], params["rewardTone"]["levelDbfs"], params["penalty"]["sound"]["levelDbfs"]) == (-55, -55, -45)
    assert "backgroundNoise" not in params
    conditions = [{"weight": 1, "set": {"visualStimulus.contrasts": [0], "rewardTone.levelDbfs": -50, "penalty.sound.levelDbfs": "mute"}},
                  {"weight": 1, "set": {"visualStimulus.contrasts": [0], "rewardTone.levelDbfs": "mute"}}]
    UclOpenCoenDomeNavigationTaskParameters.model_validate({**params, "trialConditions": conditions})


def test_the_pips_level_is_measured_by_the_lab_s_rule():
    # sweep.json's pipLevelDbfs is the pips' level by OpenRigs.Audio SoundLevel's rule: the RMS outside runs of exactly-zero
    # samples at least 1 ms long (a lone zero counts), to 0.01 dB; the pips have no such run, so it is their RMS, -58.1.
    assert waveforms.sweep()["pipLevelDbfs"] == json.loads((WAVES / "sweep.json").read_text(encoding="utf-8"))["pipLevelDbfs"] == -58.1
    on = np.full(480, 0.5)
    assert waveforms.level_dbfs(np.concatenate([on, np.zeros(48), on])) == waveforms.level_dbfs(on)
    assert waveforms.level_dbfs(np.concatenate([on, np.zeros(47), on])) < waveforms.level_dbfs(on)
    assert waveforms.level_dbfs(np.concatenate([on, [0.0], on])) < waveforms.level_dbfs(on)
