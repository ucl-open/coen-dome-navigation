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
from ucl_open_coen_dome_navigation.task import UclOpenCoenDomeNavigationTaskLogic

ROOT = Path(__file__).parent.parent
TASK = ROOT / "config" / "task-abstract-av-corridor.yml"
WAVES = ROOT / "src" / "waveforms"
SCHEMA = ROOT / "src" / "DataSchemas" / "ucl_open_coen_dome_navigation_task.json"


def task() -> dict:
    return yaml.safe_load(TASK.read_text(encoding="utf-8"))


def test_the_task_file_validates():
    UclOpenCoenDomeNavigationTaskLogic.model_validate(task())


def test_the_stage_rows_keep_tom_s_reward_zones():
    # Tom Childs' zones: from 18 kHz to the sweep's end at his stages 1-2, then a window centred on 18 kHz in
    # log space (6 kHz wide), its floor from stage 3 and its ceiling from stage 4 (rows 0-5 are his 1-6).
    floor, ceiling = 15248, 21248  # the 6 kHz log window centred on 18 kHz
    rows = task()["taskParameters"]["trainingStages"]
    assert len(rows) == 6
    zones = [(r["rewardFloorHz"], r["rewardCeilingHz"]) for r in rows]
    assert zones == [(18000, 25000)] * 2 + [(floor, 25000)] + [(floor, ceiling)] * 3


def test_the_time_limit_and_quiet_wait_are_one_optional_field_each_per_stage():
    # Plan v11: a stage's timeLimitSeconds, null for none; the quiet waits (Pip, 13 Nov 2026), 0 for off, Tom's quietSeconds kept as lickQuietSeconds.
    rows = task()["taskParameters"]["trainingStages"]
    assert [r["timeLimitSeconds"] for r in rows] == [None] * 3 + [12.0] * 3
    assert [r["lickQuietSeconds"] for r in rows] == [0] * 3 + [1.0] * 3
    assert all(r["wheelQuietSeconds"] == 0 and r["wheelQuietCm"] == 0 and "quietSeconds" not in r for r in rows)
    assert not {"timeLimit", "quiescent"} & set(rows[0])
    assert not {"timeLimitSeconds", "quiescentSeconds"} & set(task()["taskParameters"]["trialStructure"])


def test_the_interval_runs_from_the_outcome_and_keeps_tom_s_timing():
    # Plan v11, as decision-making: itiSeconds runs from the outcome (after any timeout) and includes the drink,
    # so Tom's fixed 2 s then 0 to 1 s is 2 to 3 s.
    structure = task()["taskParameters"]["trialStructure"]
    assert (structure["itiMinSeconds"], structure["itiMaxSeconds"]) == (2.0, 3.0) and "drinkSeconds" not in structure


def test_the_sweep_and_the_wheel_are_not_task_fields():
    # Plan v11: the sweep is src\waveforms\sweep.json's, and the wheel diameter rig.yml's (75 mm on every dome).
    p = task()["taskParameters"]
    assert not {"sweepStartHz", "sweepEndHz", "sweepSteps", "toneAmplitude", "wheelDiameterMm"} & set(p)


def test_the_committed_waveforms_are_waveforms_py_s():
    assert json.loads((WAVES / "sweep.json").read_text(encoding="utf-8")) == waveforms.sweep()
    freqs = waveforms.tone_frequencies()
    assert [int(x) for x in (WAVES / "frequencies.txt").read_text(encoding="utf-8").split()] == list(freqs)
    committed = np.fromfile(WAVES / "all_waveforms.bin", dtype=np.float32).reshape(waveforms.SWEEP_STEPS, -1)
    assert np.array_equal(committed, waveforms.tones(freqs))
    assert committed.shape[1] == 2400  # the workflow's MatrixReader BufferLength
    assert np.array_equal(np.fromfile(WAVES / "reward_tone.bin", dtype=np.float32), waveforms.reward_tone()[0])
    # The noise is a 32-bit float WAV (OpenRigs.Audio BackgroundNoise reads it with ReadWave, samples as stored), at the rate
    # every waveform is made at; until 16 Nov 2026 the same samples were background_noise.bin.
    rate, noise = waveforms.read_wave(WAVES / "background_noise.wav")
    assert rate == waveforms.SAMPLE_RATE and np.array_equal(noise, waveforms.background_noise())
    assert not (WAVES / "background_noise.bin").exists()


def test_the_penalty_sound_is_the_lab_s():
    # Audit, 14 Nov 2026: the penalty sound is OpenRigs.Audio's (PenaltySoundBuffer), so the task file names no file and
    # waveforms.py makes none.
    assert set(task()["taskParameters"]["penalty"]["sound"]) == {"enabled", "levelDbfs"}
    schema = json.loads(SCHEMA.read_text(encoding="utf-8"))
    assert set(schema["$defs"]["PenaltySound"]["properties"]) == {"enabled", "levelDbfs"}
    # rig-control checks the task file against the closed schema, so a file naming a sound path is refused.
    doc = task()
    doc["taskParameters"]["penalty"]["sound"]["path"] = "waveforms\\penalty.wav"
    errors = [e.message for e in jsonschema.Draft202012Validator(_closed(schema)).iter_errors(doc)]
    assert len(errors) == 1 and "'path'" in errors[0]


def _dbfs_rms(sound: np.ndarray) -> float:
    return 20 * math.log10(float(np.sqrt(np.mean(sound.astype(np.float64) ** 2))))


def test_the_task_file_states_the_levels_played_until_15_nov_2026():
    # Until 15 Nov 2026 every file played as stored (Tom's pips and reward tone peaking at 0.05 squared, written at the
    # amplitude and scaled by it again) and the noise at backgroundNoiseLevel 0.5 as its gain. Each level is now the task
    # file's levelDbfs, in dBFS RMS of the sound as stored (OpenRigs.Audio SoundLevel), so it states those, within 0.05 dB,
    # measured on the committed files the workflow plays.
    p = task()["taskParameters"]
    pips = np.fromfile(WAVES / "all_waveforms.bin", dtype=np.float32).reshape(waveforms.SWEEP_STEPS, -1)
    assert all(abs(p["tonePips"]["levelDbfs"] - _dbfs_rms(row)) < 0.05 for row in pips)
    assert abs(p["rewardTone"]["levelDbfs"] - _dbfs_rms(np.fromfile(WAVES / "reward_tone.bin", dtype=np.float32))) < 0.05
    noise = waveforms.read_wave(WAVES / "background_noise.wav")[1]
    assert abs(p["backgroundNoise"]["levelDbfs"] - (_dbfs_rms(noise) + 20 * math.log10(0.5))) < 0.05
    assert p["backgroundNoise"]["enabled"] and p["penalty"]["sound"]["levelDbfs"] == -44.6
    assert "backgroundNoiseLevel" not in p


def _closed(node):
    """rig-control's taskfile.closed: every object that lists properties refuses others."""
    if isinstance(node, dict):
        node = {k: _closed(v) for k, v in node.items()}
        if isinstance(node.get("properties"), dict) and "additionalProperties" not in node:
            node["additionalProperties"] = False
    elif isinstance(node, list):
        node = [_closed(v) for v in node]
    return node


def test_the_schema_refuses_zapit_ticked_with_no_config_path():
    # rig-control checks the task file against the schema, not the model.
    validator = jsonschema.Draft202012Validator(_closed(json.loads(SCHEMA.read_text(encoding="utf-8"))))

    def problems(**opto):
        doc = task()
        doc["taskParameters"]["optogenetics"].update(opto)
        return [e.message for e in validator.iter_errors(doc)]

    assert problems() == []
    assert problems(useZapit=True, laserPowerMw=2.0) == ["'' should be non-empty"]
    assert problems(useZapit=True, zapitConfigPath=r"C:\Zapit\conditions.yml") == ["None is not of type 'number'"]
    assert problems(useZapit=True, zapitConfigPath=r"C:\Zapit\conditions.yml", laserPowerMw=2.0) == []


def test_the_optogenetics_block_has_pip_s_names():
    names = ["useZapit", "zapitConfigPath", "laserPowerMw", "laserOnProbability", "laserMaxSeconds", "laserToStimulusSeconds",
             "triggerHoldSeconds", "trialsBeforeLaser"]
    assert list(task()["taskParameters"]["optogenetics"]) == names


def test_rig_control_keeps_the_zapit_file():
    # rig-control copies a task field ending in ConfigPath beside the task file at Go (taskfile.keep_files).
    assert [k for k in task()["taskParameters"]["optogenetics"] if k.endswith("Path")] == ["zapitConfigPath"]


@pytest.mark.parametrize("old", ["stimDelay", "excludeRepeats", "laserLeadSeconds", "enabled"])
def test_the_schema_refuses_old_names(old):
    validator = jsonschema.Draft202012Validator(_closed(json.loads(SCHEMA.read_text(encoding="utf-8"))))
    doc = task()
    doc["taskParameters"]["optogenetics"][old] = ""
    assert any("Additional properties" in e.message for e in validator.iter_errors(doc))


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
    doc["taskParameters"]["optogenetics"].update(useZapit=True, zapitConfigPath=r"C:\Zapit\conditions.yml", laserPowerMw=2.0)
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
    # (open_rigs.core.task QuietWait); Corridor.cs refuses them at Go too, through OpenRigs.Core QuietWait.SettingsProblems.
    doc = task()
    row = doc["taskParameters"]["trainingStages"][4]
    row[key] = value
    if pair:
        row[pair[0]] = pair[1]
    with pytest.raises(pydantic.ValidationError, match="less than or equal to"):
        UclOpenCoenDomeNavigationTaskLogic.model_validate(doc)
    assert "QuietWait.SettingsProblems" in (ROOT / "src" / "Extensions" / "Corridor.cs").read_text(encoding="utf-8-sig")


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
    # Audit round 4: lib-open-rigs' MAX_REWARD_UL, DeliverReward's MaxRewardUl.
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
