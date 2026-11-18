"""schemas/trial-columns.yml starts with the lab's shared block, its core with the optogenetics and quietWait blocks (open_rigs.rigs.trials):
the same names, order, types, units and descriptions in every dome task, so one analysis reads them all."""

import re
from pathlib import Path

import yaml
from open_rigs.rigs.trials import shared_block_problems


def test_the_trial_columns_start_with_the_shared_block():
    columns = yaml.safe_load((Path(__file__).parents[1] / "schemas" / "trial-columns.yml").read_text(encoding="utf-8"))
    assert shared_block_problems(columns, ("optogenetics", "quietWait")) == []

def test_every_outcome_reason_the_rules_write_is_listed():
    # The outcomeReason values trial-columns.yml lists are the ones TrialLogic.cs writes: an outcome's reason, a lick's
    # split into CorrectLick and WrongLickLimit (the response window's end is ResponseWindowEnd).
    root = Path(__file__).parents[1]
    listed = yaml.safe_load((root / "schemas" / "trial-columns.yml").read_text(encoding="utf-8"))["outcomeReason"]["values"]
    code = (root / "src" / "Extensions" / "TrialLogic.cs").read_text(encoding="utf-8")
    written = set(re.findall(r'End\(trial, "(\w+)"', code)) - {"Lick"} | {"CorrectLick", "WrongLickLimit"}
    assert sorted(listed) == sorted(written)


def test_the_combination_is_logged_beside_the_condition():
    # trialCombination, the index in the condition's combinations (TrialDraw.BuildConditions' order), follows trialCondition.
    root = Path(__file__).parents[1]
    names = list(yaml.safe_load((root / "schemas" / "trial-columns.yml").read_text(encoding="utf-8")))
    assert names[names.index("trialCondition") + 1] == "trialCombination"
    row = (root / "src" / "Extensions" / "TrialRow.cs").read_text(encoding="utf-8")
    assert re.search(r"int trialCondition;.*\n\s+public int trialCombination;", row)


def test_the_trial_types_are_the_conditions_and_decision_making_s():
    # trialType holds the drawn condition's trialType: its values are those trialConditions may hold, in
    # coen-dome-decision-making's words.
    root = Path(__file__).parents[1]
    listed = yaml.safe_load((root / "schemas" / "trial-columns.yml").read_text(encoding="utf-8"))["trialType"]["values"]
    from ucl_open_coen_dome_navigation.task import TrialType

    assert listed == [t.value for t in TrialType] == ["visual", "auditory", "audiovisualCoherent", "neutral"]

