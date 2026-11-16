"""schemas/trial-columns.yml starts with the lab's shared block, its core with the opto and quietWait blocks (open_rigs.rigs.trials):
the same names, order, types, units and descriptions in every dome task, so one analysis reads them all."""

import re
from pathlib import Path

import yaml
from open_rigs.rigs.trials import shared_block_problems


def test_the_trial_columns_start_with_the_shared_block():
    columns = yaml.safe_load((Path(__file__).parents[1] / "schemas" / "trial-columns.yml").read_text(encoding="utf-8"))
    assert shared_block_problems(columns, ("opto", "quietWait")) == []

def test_every_end_reason_the_rules_write_is_listed():
    # The endReason values trial-columns.yml lists are the ones TrialLogic.cs writes: an outcome's reason, a lick's
    # split into CorrectLick and WrongLickLimit (the response window's end is ResponseWindowEnd, 16 Nov 2026).
    root = Path(__file__).parents[1]
    listed = yaml.safe_load((root / "schemas" / "trial-columns.yml").read_text(encoding="utf-8"))["endReason"]["values"]
    code = (root / "src" / "Extensions" / "TrialLogic.cs").read_text(encoding="utf-8")
    written = set(re.findall(r'End\(trial, "(\w+)"', code)) - {"Lick"} | {"CorrectLick", "WrongLickLimit"}
    assert sorted(listed) == sorted(written)


def test_the_trial_types_are_the_weights_keys_the_draw_names_and_decision_making_s_where_they_overlap():
    # trialType holds the drawn type (16 Nov 2026): its values are TrialDraw.TypeNames and the stage's trialWeights keys.
    root = Path(__file__).parents[1]
    listed = yaml.safe_load((root / "schemas" / "trial-columns.yml").read_text(encoding="utf-8"))["trialType"]["values"]
    code = (root / "src" / "Extensions" / "TrialLogic.cs").read_text(encoding="utf-8")
    names = dict(re.findall(r'public const string (\w+) = "(\w+)";', code))
    drawn = [names[n] for n in re.search(r"TypeNames = \{ ([^}]*) \}", code).group(1).replace(" ", "").split(",")]
    from ucl_open_coen_dome_navigation.task import TrialTypeWeights

    assert listed == drawn == list(TrialTypeWeights.model_fields)
    assert {"auditory", "visual"} <= set(listed)  # coen-dome-decision-making's names for the one-modality trials
