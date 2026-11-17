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


def test_the_trial_types_are_the_draw_s_names_and_decision_making_s():
    # trialType holds the drawn type: its values are TrialDraw.TypeNames, the defined trialWeights keys,
    # in coen-dome-decision-making's names.
    root = Path(__file__).parents[1]
    listed = yaml.safe_load((root / "schemas" / "trial-columns.yml").read_text(encoding="utf-8"))["trialType"]["values"]
    code = (root / "src" / "Extensions" / "TrialLogic.cs").read_text(encoding="utf-8-sig")
    names = dict(re.findall(r'public const string (\w+) = "(\w+)";', code))
    drawn = [names[n] for n in re.search(r"TypeNames = \{ ([^}]*) \}", code).group(1).replace(" ", "").split(",")]
    from ucl_open_coen_dome_navigation.task import UNDEFINED_TYPES, TrialTypeWeights

    keys = [TrialTypeWeights.model_fields[f].alias or f for f in TrialTypeWeights.model_fields if f not in UNDEFINED_TYPES]
    assert listed == drawn == keys == ["visual", "auditory", "audiovisualCoherent"]

