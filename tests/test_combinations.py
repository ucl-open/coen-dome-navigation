"""Settings_Combinations as the C# writes it (tests/fixtures/combinations, written by HandoverTests.cs from each golden task
file in tests/fixtures/handover) equals the Python build of the same file (open_rigs.rigs.conditioned_task
expected_combination_rows). The C# draw is the rule the rig runs; this holds the Python copy, which the task model
validates with, to it."""

from pathlib import Path

from open_rigs.rigs import conditioned_task as ct

from ucl_open_coen_dome_navigation.task import UclOpenCoenDomeNavigationTaskParameters

FIXTURES = Path(__file__).parent / "fixtures"


def test_the_csharps_settings_combinations_equal_the_python_build():
    assert ct.combination_agreement_problems(UclOpenCoenDomeNavigationTaskParameters, FIXTURES) == []
