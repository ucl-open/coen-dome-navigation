"""The task files app-rig-control writes at launch (tests/fixtures/handover, golden files its tests/test_handover.py writes
for every stage of the share's curricula): each one this repo's task model takes (the schema, x-rules,
x-session-fields, x-required-conditions and the x-max-combinations cap, all in model_validate), and each condition's
trialType the one x-trial-types derives (open_rigs.rigs.conditioned_task handover_problems). The C# draw of the same
files is HandoverTests.cs."""

from pathlib import Path

from open_rigs.rigs import conditioned_task as ct

from ucl_open_coen_dome_navigation.task import UclOpenCoenDomeNavigationTaskParameters

FIXTURES = Path(__file__).parent / "fixtures"
# (file, condition index): why its trialType is a person's name, not x-trial-types'. The curriculum keeps no override
# flag, so this table is the only record; an entry matching no golden condition fails.
OVERRIDDEN: dict[tuple[str, int], str] = {}


def test_each_golden_file_is_a_task_this_repo_takes_and_names_each_condition_as_derived():
    assert ct.handover_problems(UclOpenCoenDomeNavigationTaskParameters, FIXTURES, OVERRIDDEN) == []
