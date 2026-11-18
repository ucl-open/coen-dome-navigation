"""The task files app-rig-control writes at launch (tests/fixtures/handover, golden files its tests/test_handover.py writes
for every stage of the share's curricula): each one this repo's task model takes (the schema, x-rules,
x-session-fields, x-required-conditions and the x-max-combinations cap, all in model_validate), and each condition's
trialType the one x-trial-types derives. The C# draw of the same files is HandoverTests.cs."""

from pathlib import Path

import pytest
import yaml

from ucl_open_coen_dome_navigation.task import UclOpenCoenDomeNavigationTaskParameters, derive_trial_type

GOLDEN = sorted((Path(__file__).parent / "fixtures" / "handover").glob("*.yml"))
# (file, condition index): why its trialType is a person's name, not x-trial-types'. The curriculum keeps no override
# flag, so this table is the only record; an entry matching no golden condition fails.
OVERRIDDEN: dict[tuple[str, int], str] = {}


def test_there_are_golden_files():
    assert GOLDEN, "tests/fixtures/handover is empty: run app-rig-control's tests/test_handover.py with HANDOVER_REGEN=1"


@pytest.mark.parametrize("path", GOLDEN, ids=lambda p: p.stem)
def test_each_golden_file_is_a_task_this_repo_takes_and_names_each_condition_as_derived(path):
    task = UclOpenCoenDomeNavigationTaskParameters.model_validate(yaml.safe_load(path.read_text(encoding="utf-8"))["taskParameters"])
    for n, condition in enumerate(task.trial_conditions):
        if (path.name, n) not in OVERRIDDEN:
            assert derive_trial_type(task, condition) == condition.trial_type.value, (path.name, n)


def test_every_override_names_a_golden_condition():
    for name, n in OVERRIDDEN:
        path = GOLDEN[0].parent / name
        assert path.is_file() and n < len(yaml.safe_load(path.read_text(encoding="utf-8"))["taskParameters"]["trialConditions"])
