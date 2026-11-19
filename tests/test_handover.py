"""The task files app-rig-control writes at launch (tests/fixtures/handover, golden files its tests/test_handover.py writes
for every stage of the share's curricula): each one this repo's task model takes (the schema, x-rules,
x-session-fields, x-required-conditions and the x-max-combinations cap, all in model_validate; a stored trialType is
refused, as it is derived: open_rigs.rigs.conditioned_task handover_problems). The C# draw of the same files is
HandoverTests.cs."""

from pathlib import Path

import pytest
import yaml
from open_rigs.rigs import conditioned_task as ct
from open_rigs.rigs import conditions

from ucl_open_coen_dome_navigation.task import TRIAL_TYPES, UclOpenCoenDomeNavigationTaskParameters

FIXTURES = Path(__file__).parent / "fixtures"
TASK = Path(__file__).parents[1] / "config" / "task-abstract-corridor.yml"
# The share's curriculum, read only: until step 6's migration its stages still store each condition's trialType, in the
# old names.
CURRICULUM = Path(r"K:\config\curricula\coen-dome-navigation\task-abstract-corridor.conditions.yml")
# The old paths of what TRIAL_TYPES reads (the K: migration's map): a contrast list as is, an amplitude 0 as mute and any
# other as a level (the trial type asks only whether a number sounds).
OLD_PATHS = {"contrasts": "visualStimulus.contrasts", "auditoryStimulus.amplitude": "auditoryStimulus.levelDbfs"}


def test_each_golden_file_is_a_task_this_repo_takes():
    assert ct.handover_problems(UclOpenCoenDomeNavigationTaskParameters, FIXTURES) == []


def _new(values: dict) -> dict:
    def level(a):
        return "mute" if a == 0 else -55.0

    new = {}
    for path, value in values.items():
        if path == "auditoryStimulus.amplitude":
            value = [level(a) for a in value] if isinstance(value, list) else level(value)
        new[OLD_PATHS.get(path, path)] = value
    return new


def test_the_derived_trial_type_is_the_one_each_share_stage_stores():
    # P9 before step 6 drops the stored names: on every stage of the share's curriculum, each condition's trialType is the
    # one x-trial-types derives from the stage's values and the condition's over this repo's task file.
    if not CURRICULUM.is_file():
        pytest.skip(f"{CURRICULUM} is not reachable (the share)")
    read = {path for entry in TRIAL_TYPES for path in entry.get("if", {}).get("properties", {})}
    assert read <= set(OLD_PATHS.values()), "a path TRIAL_TYPES reads has no old name here"
    base = UclOpenCoenDomeNavigationTaskParameters.model_validate(yaml.safe_load(TASK.read_text(encoding="utf-8"))["taskParameters"])
    task = base.model_dump(by_alias=True, mode="json")
    stages = yaml.safe_load(CURRICULUM.read_text(encoding="utf-8"))["stages"]
    compared = []
    for stage in stages:
        for i, condition in enumerate(stage.get("conditions") or []):
            if "trialType" not in condition:
                continue
            values = {**_new(stage.get("set") or {}), **_new(condition.get("set") or {})}
            derived = conditions.derive_trial_type(list(TRIAL_TYPES), task, values, condition.get("pairs") or [])
            compared.append((stage["id"], i, condition["trialType"], derived))
    assert compared, "no share stage stores a trialType: step 6 has run, so this test can go"
    assert [c for c in compared if c[2] != c[3]] == []
