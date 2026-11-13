import json
from pathlib import Path

import jsonschema
import numpy as np
import pydantic
import pytest
import yaml
from scipy.io import wavfile

from ucl_open_coen_dome_navigation import waveforms
from ucl_open_coen_dome_navigation.helpers import calc_log_window
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
    floor, ceiling = calc_log_window(center_freq=18000, window_size=6000)
    rows = task()["taskParameters"]["trainingStages"]
    assert len(rows) == 6
    zones = [(r["rewardFloorHz"], r["rewardCeilingHz"]) for r in rows]
    assert zones == [(18000, 25000)] * 2 + [(floor, 25000)] + [(floor, ceiling)] * 3


def test_the_time_limit_and_quiet_wait_are_one_optional_field_each_per_stage():
    # Plan v11: a stage's timeLimitSeconds and quietSeconds, null for none, replace the booleans and the shared values.
    rows = task()["taskParameters"]["trainingStages"]
    assert [r["timeLimitSeconds"] for r in rows] == [None] * 3 + [12.0] * 3
    assert [r["quietSeconds"] for r in rows] == [None] * 3 + [1.0] * 3
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
    rate, error = wavfile.read(WAVES / "error_tone.wav")
    assert rate == 48000 and np.array_equal(error, waveforms.error_tone(float(freqs[0]), float(freqs[-1])))
    assert np.array_equal(np.fromfile(WAVES / "background_noise.bin", dtype=np.float32), waveforms.background_noise())


def test_every_sound_is_at_the_loudness_it_plays_at():
    # Tom's pips and reward tone peaked at amplitude squared (written at amplitude, scaled by it again); the noise has a
    # pip's RMS, so backgroundNoiseLevel is its gain; the error tone peaks at amplitude.
    assert abs(float(np.max(np.abs(waveforms.tones(np.array([18000]))))) - 0.05**2) < 1e-4
    assert abs(float(np.max(np.abs(waveforms.reward_tone()))) - 0.05**2) < 1e-6
    rms = float(np.sqrt(np.mean(waveforms.background_noise().astype(np.float64) ** 2)))
    assert abs(rms - 0.5 * 0.05**2) < 1e-5
    assert abs(float(np.max(np.abs(waveforms.error_tone(2000, 25000)))) / 32767 - 0.05) < 1e-4


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
    assert {row["rewardSizeUl"] for row in params["trainingStages"]} == {2.0}


def test_a_trigger_hold_as_long_as_the_shortest_interval_is_refused():
    # In an optogenetics experiment the line is held triggerHoldSeconds after every outcome; the next trial raises it no
    # sooner than itiMinSeconds later. Without optogenetics the line stays low, so the hold is not checked.
    doc = task()
    doc["taskParameters"]["optogenetics"]["triggerHoldSeconds"] = 2.0
    UclOpenCoenDomeNavigationTaskLogic.model_validate(doc)
    doc["taskParameters"]["optogenetics"].update(useZapit=True, zapitConfigPath=r"C:\Zapit\conditions.yml", laserPowerMw=2.0)
    with pytest.raises(pydantic.ValidationError, match="trigger_hold_seconds"):
        UclOpenCoenDomeNavigationTaskLogic.model_validate(doc)


def test_the_committed_task_schema_is_the_model_s():
    model = pydantic.RootModel[UclOpenCoenDomeNavigationTaskLogic].model_json_schema(
        by_alias=True, mode="serialization", union_format="primitive_type_array"
    )
    assert json.loads(SCHEMA.read_text(encoding="utf-8")) == json.loads(json.dumps(model))
