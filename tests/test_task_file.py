import json
from pathlib import Path

import jsonschema
import numpy as np
import pydantic
import yaml
from scipy.io import wavfile

from ucl_open_coen_av_corridor import waveforms
from ucl_open_coen_av_corridor.task import UclOpenCoenAvCorridorTaskLogic, UclOpenCoenAvCorridorTaskParameters

ROOT = Path(__file__).parent.parent
TASK = ROOT / "config" / "task.yml"
WAVES = ROOT / "src" / "waveforms"


def task() -> dict:
    return yaml.safe_load(TASK.read_text(encoding="utf-8"))


def test_the_task_file_validates():
    UclOpenCoenAvCorridorTaskLogic.model_validate(task())


def test_the_task_file_s_reward_zones_are_the_ones_the_model_derives():
    # The workflow reads thresholdFrequencies as written; rig-control does not run the model.
    params = task()["taskParameters"]
    written = params.pop("thresholdFrequencies")
    derived = UclOpenCoenAvCorridorTaskParameters.model_validate(params).threshold_frequencies
    assert written == derived.model_dump(by_alias=True)


def test_the_committed_waveforms_are_the_task_file_s():
    p = UclOpenCoenAvCorridorTaskLogic.model_validate(task()).task_parameters
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
    schema = _closed(json.loads((ROOT / "src" / "DataSchemas" / "ucl_open_coen_av_corridor_task.json").read_text(encoding="utf-8")))
    validator = jsonschema.Draft202012Validator(schema)

    def problems(**opto):
        doc = task()
        doc["taskParameters"]["optogenetics"].update(opto)
        return [e.message for e in validator.iter_errors(doc)]

    assert problems() == []
    assert problems(enabled=True) == ["'' should be non-empty"]
    assert problems(enabled=True, zapitConfigPath=r"C:\Zapit\conditions.yml") == []


def test_the_committed_task_schema_is_the_model_s():
    committed = json.loads((ROOT / "src" / "DataSchemas" / "ucl_open_coen_av_corridor_task.json").read_text(encoding="utf-8"))
    model = pydantic.RootModel[UclOpenCoenAvCorridorTaskLogic].model_json_schema(
        by_alias=True, mode="serialization", union_format="primitive_type_array"
    )
    assert committed == json.loads(json.dumps(model))
