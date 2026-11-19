"""schemas/trial-columns.yml starts with the lab's shared block, its core with the optogenetics and quietWait blocks (open_rigs.rigs.trials):
the same names, order, types, units and descriptions in every dome task, so one analysis reads them all."""

import re
from pathlib import Path

import yaml
from open_rigs.rigs.trials import shared_block_problems


def test_the_trial_columns_start_with_the_shared_block():
    columns = yaml.safe_load((Path(__file__).parents[1] / "schemas" / "trial-columns.yml").read_text(encoding="utf-8"))
    assert shared_block_problems(columns, ("quietWait", "optogenetics", "conditions", "stimulus", "autoReward")) == []
    # The columns the shared blocks replaced are gone, and trial columns are camelCase outcomes.
    gone = {"penaltySoundOn", "wheelGain", "autoRewarded", "trialCondition", "auditoryAmplitude", "auditoryAzimuthDeg"}
    assert not gone & set(columns)
    assert columns["outcome"]["values"] == ["correct", "incorrect", "noResponse"]

def test_every_outcome_reason_the_rules_write_is_listed():
    # The outcomeReason values trial-columns.yml lists are the ones TrialLogic.cs writes: an outcome's reason, a lick's
    # split into CorrectLick and WrongLickLimit (the response window's end is ResponseWindowEnd).
    root = Path(__file__).parents[1]
    listed = yaml.safe_load((root / "schemas" / "trial-columns.yml").read_text(encoding="utf-8"))["outcomeReason"]["values"]
    code = (root / "src" / "Extensions" / "TrialLogic.cs").read_text(encoding="utf-8")
    written = set(re.findall(r'End\(trial, "(\w+)"', code)) - {"Lick"} | {"CorrectLick", "WrongLickLimit"}
    assert sorted(listed) == sorted(written)


def test_the_combination_is_logged_beside_the_condition():
    # trialCombinationId, the trial's row of Settings_Combinations (TrialDraw.BuildConditions' order), follows trialConditionId.
    root = Path(__file__).parents[1]
    names = list(yaml.safe_load((root / "schemas" / "trial-columns.yml").read_text(encoding="utf-8")))
    assert names[names.index("trialConditionId") + 1] == "trialCombinationId"
    row = (root / "src" / "Extensions" / "TrialRow.cs").read_text(encoding="utf-8")
    assert re.search(r"int trialConditionId;.*\n\s+public int trialCombinationId;", row)


def test_the_trial_types_are_the_conditions_and_decision_making_s():
    # trialType holds the drawn condition's trialType: its values are those trialConditions may hold, in
    # coen-dome-decision-making's words.
    root = Path(__file__).parents[1]
    listed = yaml.safe_load((root / "schemas" / "trial-columns.yml").read_text(encoding="utf-8"))["trialType"]["values"]
    from ucl_open_coen_dome_navigation.task import TrialType

    assert listed == [t.value for t in TrialType] == ["visual", "auditory", "audiovisualCoherent", "audiovisualConflict", "neutral"]


def test_the_stimulus_state_columns_are_the_row_s_fields():
    # schemas/stimulus-state-columns.yml describes WorkflowData_StimulusState: harpTime, then StimulusStateRow's public fields
    # (src/Extensions/Sweep.cs) in order, each with a type and a description.
    root = Path(__file__).parents[1]
    columns = yaml.safe_load((root / "schemas" / "stimulus-state-columns.yml").read_text(encoding="utf-8"))
    code = (root / "src" / "Extensions" / "Sweep.cs").read_text(encoding="utf-8-sig")
    body = re.search(r"public class StimulusStateRow\s*\{(.*?)\n\}", code, re.S).group(1)
    fields = re.findall(r"^\s*public \w+ (\w+);", body, re.M)
    assert fields and list(columns) == ["harpTime", *fields]
    for name, column in columns.items():
        assert column["type"] in ("int", "double", "bool", "string") and column.get("description"), name
    assert columns["harpTime"]["unit"] == "s"

