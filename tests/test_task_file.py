import json
from pathlib import Path

import jsonschema
import numpy as np
import pydantic
import yaml
from scipy.io import wavfile

from ucl_open_coen_dome_navigation import waveforms
from ucl_open_coen_dome_navigation.helpers import calc_log_window
from ucl_open_coen_dome_navigation.task import UclOpenCoenDomeNavigationTaskLogic

ROOT = Path(__file__).parent.parent
TASK = ROOT / "config" / "task-abstract-av-corridor.yml"
WAVES = ROOT / "src" / "waveforms"


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


def test_the_shared_blocks_have_av_dome_s_names():
    # Pip, 9 Nov 2026: the lab's tasks name the same things the same way.
    p = task()["taskParameters"]
    assert {"itiMin", "itiMax", "closedLoopDuration"} <= set(p["trialStructure"])
    assert list(p["punishment"]) == ["timeoutSeconds", "sound"] and {"enabled", "path", "gain"} <= set(p["punishment"]["sound"])
    assert "rngSeed" in p and "rewardSizeUl" in p["trainingStages"][0]


def test_the_committed_waveforms_are_the_task_file_s():
    p = UclOpenCoenDomeNavigationTaskLogic.model_validate(task()).task_parameters
    assert (WAVES / "sweep.txt").read_text(encoding="utf-8") == waveforms.sweep_line(
        p.start_freq, p.end_freq, p.n_freq_bins, p.amplitude
    )
    freqs = waveforms.tone_frequencies(p.start_freq, p.end_freq, p.n_freq_bins)
    assert [int(x) for x in (WAVES / "frequencies.txt").read_text(encoding="utf-8").split()] == list(freqs)
    committed = np.fromfile(WAVES / "all_waveforms.bin", dtype=np.float32).reshape(p.n_freq_bins, -1)
    assert np.array_equal(committed, waveforms.tones(freqs, p.amplitude))
    assert committed.shape[1] == 2400  # the workflow's MatrixReader BufferLength
    rate, error = wavfile.read(WAVES / "error_tone.wav")
    assert rate == 48000 and np.array_equal(error, waveforms.error_tone(float(freqs[0]), float(freqs[-1]), p.amplitude))
    noise = np.fromfile(WAVES / "background_noise.bin", dtype=np.float32)
    assert np.array_equal(noise, waveforms.background_noise())


def test_a_tone_pip_keeps_the_old_loudness():
    # gen_session wrote pips at amplitude and the workflow scales them by amplitude again.
    pips = waveforms.tones(np.array([18000]), 0.05)
    assert abs(float(np.max(np.abs(pips))) - 0.05) < 1e-3


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
    # rig-control checks the task file against the schema, not the model; this used to reach Go and
    # stop the workflow at trial 1 (audit, 9 Nov 2026, round 3).
    schema = _closed(json.loads((ROOT / "src" / "DataSchemas" / "ucl_open_coen_dome_navigation_task.json").read_text(encoding="utf-8")))
    validator = jsonschema.Draft202012Validator(schema)

    def problems(**opto):
        doc = task()
        doc["taskParameters"]["optogenetics"].update(opto)
        return [e.message for e in validator.iter_errors(doc)]

    assert problems() == []
    assert problems(enabled=True) == ["'' should be non-empty"]
    assert problems(enabled=True, zapitConfigPath=r"C:\Zapit\conditions.yml") == []


def test_the_optogenetics_block_is_av_dome_s():
    # Pip, 9 Nov 2026: the same parameter names as av-dome's OptogeneticsSettings (pip/main-switchover 6c5a885),
    # plus leadTime (zapit-v2): how long the stimulus runs before the first tone pip.
    av_dome = ["enabled", "zapitConfigPath", "stimProbability", "stimDelay", "stimDuration", "leadTime", "excludeRepeats",
               "pseudorandomSelection", "warmupTrials"]
    assert list(task()["taskParameters"]["optogenetics"]) == av_dome


def test_the_schema_refuses_a_stim_delay():
    # Zapit ignores a start delay for the continuous stimulus this task uses, so only 0 is accepted (zapit-v2)
    schema = _closed(json.loads((ROOT / "src" / "DataSchemas" / "ucl_open_coen_dome_navigation_task.json").read_text(encoding="utf-8")))
    validator = jsonschema.Draft202012Validator(schema)
    doc = task()
    assert list(validator.iter_errors(doc)) == []
    doc["taskParameters"]["optogenetics"]["stimDelay"] = 0.2
    assert [e.message for e in validator.iter_errors(doc)] == ["0.2 is greater than the maximum of 0"]


def test_the_task_file_s_optogenetics_and_reward_are_the_agreed_ones():
    # zapit-v2: Tom's design (40% laser after 20 sham warmup trials, 0.1 s lead, 12.5 s cap). The reward is meant to be
    # 3 ul, but 2.0 on every stage until the left valves are calibrated: an uncalibrated valve is taken as 2 ul a pulse,
    # so 2 ul is one opening of Tom's width (42 ms on BLUE-OPTO-2, measured as about 3 ul) and 3 ul was two (BACKLOG).
    params = task()["taskParameters"]
    opto = params["optogenetics"]
    assert (opto["stimProbability"], opto["warmupTrials"], opto["leadTime"], opto["stimDuration"], opto["stimDelay"]) == (0.4, 20, 0.1, 12.5, 0.0)
    assert {row["rewardSizeUl"] for row in params["trainingStages"]} == {2.0}


def test_the_committed_task_schema_is_the_model_s():
    committed = json.loads((ROOT / "src" / "DataSchemas" / "ucl_open_coen_dome_navigation_task.json").read_text(encoding="utf-8"))
    model = pydantic.RootModel[UclOpenCoenDomeNavigationTaskLogic].model_json_schema(
        by_alias=True, mode="serialization", union_format="primitive_type_array"
    )
    assert committed == json.loads(json.dumps(model))
