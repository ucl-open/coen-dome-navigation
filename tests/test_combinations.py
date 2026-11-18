"""Settings_Combinations as the C# writes it (tests/fixtures/combinations, written by HandoverTests.cs from each golden task
file in tests/fixtures/handover) equals task.py's condition_combinations of the same file, after the same normalisation:
a list field's one-entry pick unwrapped (an x-whole list such as stimulusOff.stimuli stays a list), a path kept where it
differs between rows or from the task's own value. The C# draw is the rule the rig runs; this holds the Python copy,
which the task model validates with, to it."""

import json
from pathlib import Path

import pytest
import yaml

from ucl_open_coen_dome_navigation.task import LIST_FIELDS, UclOpenCoenDomeNavigationTaskParameters, _get, condition_combinations

FIXTURES = Path(__file__).parent / "fixtures"
GOLDEN = sorted((FIXTURES / "handover").glob("*.yml"))


def _normal(path, value):
    return value[0] if path in LIST_FIELDS and isinstance(value, list) and len(value) == 1 else value


def expected_rows(task: dict) -> list[dict]:
    """File A's rows as task.py builds them: every condition's combinations in turn, values kept by the normalisation rule."""
    base = UclOpenCoenDomeNavigationTaskParameters.model_validate(task).model_dump(by_alias=True, mode="json")
    rows = []
    for i, condition in enumerate(task["trialConditions"]):
        for combo in condition_combinations(base, condition.get("set") or {}, condition.get("pairs") or []):
            rows.append({"trialCombinationId": len(rows), "trialCondition": i, "trialType": condition["trialType"],
                         "conditionWeight": condition["weight"], "values": {path: _normal(path, value) for path, value in combo.items()}})
    paths = {path for row in rows for path in row["values"]}
    own = {path: _normal(path, _get(base, path)) for path in paths}
    value = lambda row, path: row["values"].get(path, own[path])  # noqa: E731
    kept = {path for path in paths if any(value(row, path) != own[path] or value(row, path) != value(rows[0], path) for row in rows)}
    for row in rows:
        row["values"] = {path: value(row, path) for path in sorted(kept)}
    return rows


def test_there_are_combinations_for_every_golden_file():
    assert GOLDEN and {p.stem for p in GOLDEN} == {p.stem for p in (FIXTURES / "combinations").glob("*.json")}


@pytest.mark.parametrize("path", GOLDEN, ids=lambda p: p.stem)
def test_the_csharps_settings_combinations_equal_task_pys(path):
    task = yaml.safe_load(path.read_text(encoding="utf-8"))["taskParameters"]
    written = json.loads((FIXTURES / "combinations" / f"{path.stem}.json").read_text(encoding="utf-8"))["rows"]
    assert written == expected_rows(task)
