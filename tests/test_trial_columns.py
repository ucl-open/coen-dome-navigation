"""schemas/trial-columns.yml starts with the lab's shared block, its core with the opto and quietWait blocks (open_rigs.rigs.trials):
the same names, order, types, units and descriptions in every dome task, so one analysis reads them all."""

from pathlib import Path

import yaml
from open_rigs.rigs.trials import shared_block_problems


def test_the_trial_columns_start_with_the_shared_block():
    columns = yaml.safe_load((Path(__file__).parents[1] / "schemas" / "trial-columns.yml").read_text(encoding="utf-8"))
    assert shared_block_problems(columns, ("opto", "quietWait")) == []
