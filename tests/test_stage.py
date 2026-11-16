import json
import subprocess
import sys
from pathlib import Path

import pytest

from ucl_open_coen_dome_navigation.stage import Experiment, experiments, read_new, suggest

STAGE_PY = Path(__file__).parents[1] / "src" / "ucl_open_coen_dome_navigation" / "stage.py"

# Stages are trainingStages rows from 0: Tom Childs' stage n is n - 1 (Pip, 9 Nov 2026).


def exp(name, stage, n, rewarded, modality="auditory", stages=None):
    """An experiment of n trials at one stage, the first `rewarded` of them rewarded."""
    return Experiment(name, name, [modality] * n, stages or [stage] * n, [i < rewarded for i in range(n)])


@pytest.mark.parametrize(
    "history, modality, stage",
    [
        ([], "auditory", 0),
        ([exp("2026-01-02", 2, 40, 40)], "auditory", 2),  # under 50 trials keeps the stage
        ([exp("2026-01-02", 0, 100, 70)], "auditory", 1),  # 70% advances
        ([exp("2026-01-02", 0, 100, 69)], "auditory", 0),
        ([exp("2026-01-02", 2, 200, 100)], "auditory", 3),  # first 100 all rewarded
        ([exp("2026-01-02", 4, 100, 100)], "auditory", 4),  # 4 never advances on its own
        ([exp("2026-01-02", 5, 100, 10)], "auditory", 5),  # 5 is kept
        ([exp("2026-01-02", 1, 100, 60), exp("2026-01-01", 1, 100, 50)], "auditory", 2),
        ([exp("2026-01-02", 1, 100, 60), exp("2026-01-01", 1, 100, 49)], "auditory", 1),
        ([exp("2026-01-02", 1, 100, 40)], "auditory", 0),  # stage 1 under 50% falls back
        ([exp("2026-01-02", 3, 100, 100, "visual"), exp("2026-01-01", 1, 100, 60)], "auditory", 1),  # other modality ignored
        ([exp("2026-01-02", 0, 0, 0), exp("2026-01-01", 2, 40, 0)], "auditory", 2),  # an empty experiment is skipped
    ],
)
def test_the_rules_are_tom_s(history, modality, stage):
    assert suggest(history, modality)[0] == stage


@pytest.mark.parametrize("late, stage", [(40, 4), (39, 3)])
def test_stage_3_advances_on_its_first_150_trials(late, stage):
    # 65 of the first 100 (under 70%), `late` of the next 50: 105/150 is 70%, 104/150 is not.
    rewarded = [i < 65 for i in range(100)] + [i < late for i in range(50)] + [False] * 150
    e = Experiment("2026-01-02", "2026-01-02", ["auditory"] * 300, [3] * 300, rewarded)
    assert suggest([e], "auditory")[0] == stage


def test_the_rate_counts_every_trial_but_rewards_only_at_the_last_stage():
    # 60 trials at stage 0, all rewarded, then 40 at stage 1 none: 0/100 at stage 1, under 50%.
    e = exp("2026-01-02", 1, 100, 60, stages=[0] * 60 + [1] * 40)
    assert suggest([e], "auditory")[0] == 0


REPO = "https://github.com/ucl-open/coen-dome-navigation.git"
DROPPED = ("itiStartTime", "trialStartTime", "laserTriggerTime")  # jacuzzi-ingest's drop list (records.md, "Session summaries")
ROWS = [["auditory", "Correct"]] * 60


def summary(session: str, run: str, stage: int | None, rows: list[list], columns=("trialType", "outcome"), repo=REPO, **extra) -> dict:
    """A session summary as records.md v1 describes it, in the keys stage.py reads."""
    s = {"version": 1, "run": run, "subject": "TC9", "session": session, "source": {"repo": repo, "commit": "abc"},
         "trials": {"columns": list(columns), "rows": rows},
         "Settings_Experiment": None if stage is None else {"parameters": {"TrainingStage": str(stage)}}}
    return {**s, **extra}


def put(root: Path, s: dict, subject: str = "TC9") -> Path:
    path = root / f"sub-{subject}" / f"{s['session']}.json"
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(s), encoding="utf-8")
    return path


def expect(root: Path, *runs: str) -> Path:
    path = root.parent / f"{root.name}-expected.json"
    path.write_text(json.dumps(list(runs)), encoding="utf-8")
    return path


def read(root: Path, *runs: str):
    unread: list[str] = []
    return experiments("TC9", root, expect(root, *runs), unread), unread


def test_a_summary_is_read_as_the_rules_need_it(tmp_path):
    put(tmp_path, summary("ses-002D1_2026-11-10T09-00-00", "r2", 2, ROWS))
    found, unread = read(tmp_path, "r2")
    assert unread == [] and [e.name for e in found] == ["ses-002D1_2026-11-10T09-00-00"]
    assert found[0].stage == [2] * 60 and all(found[0].rewarded) and found[0].modality[0] == "auditory"


def test_the_newest_experiment_comes_first_and_a_mouse_with_no_expected_runs_has_no_history(tmp_path):
    assert read(tmp_path) == ([], [])
    put(tmp_path, summary("ses-001D1_2026-11-09T09-00-00", "r1", 1, ROWS))
    put(tmp_path, summary("ses-002D1_2026-11-10T09-00-00", "r2", 2, ROWS))
    found, unread = read(tmp_path, "r1", "r2")
    assert unread == [] and [e.stage[0] for e in found] == [2, 1]


def test_a_folder_that_does_not_answer_gives_no_stage(tmp_path, capsys, monkeypatch):
    # Audit, 9 Nov 2026: an unreachable share read as no history, and a trained mouse was offered Stage 1.
    from ucl_open_coen_dome_navigation import stage

    missing = tmp_path / "no-share"
    monkeypatch.setattr("sys.argv", ["stage.py", "--subject", "TC9", "--modality", "auditory",
                                     "--summaries", str(missing), "--expected", str(expect(tmp_path))])
    stage.main()
    answer = json.loads(capsys.readouterr().out)
    assert answer["value"] is None and str(missing) in answer["why"] and "No Answer" in answer["why"]


def test_an_expected_run_with_no_file_gives_no_stage(tmp_path):
    put(tmp_path, summary("ses-002D1_2026-11-10T09-00-00", "r2", 2, ROWS))
    _, unread = read(tmp_path, "r2", "r3")
    assert unread == ["Run r3 (No Summary File)"]


def test_a_file_that_cannot_be_read_gives_no_stage(tmp_path):
    put(tmp_path, summary("ses-002D1_2026-11-10T09-00-00", "r2", 2, ROWS))
    (tmp_path / "sub-TC9" / "ses-003D1_2026-11-11T09-00-00.json").write_text("{not json", encoding="utf-8")
    _, unread = read(tmp_path, "r2")
    assert unread == ["ses-003D1_2026-11-11T09-00-00.json (Unreadable: JSONDecodeError)"]
    # trials but no Settings_Experiment: it may be the experiment that decides
    put(tmp_path, summary("ses-004D1_2026-11-12T09-00-00", "r4", None, ROWS))
    _, unread = read(tmp_path, "r2", "r4")
    assert "ses-004D1_2026-11-12T09-00-00 (Unreadable: KeyError)" in unread


def test_a_settings_file_without_a_stage_gives_no_stage(tmp_path):
    # A null TrainingStage raised TypeError, which ended suggest-stage with a traceback and no JSON line (audit cycle 2).
    put(tmp_path, summary("ses-005D1_2026-11-13T09-00-00", "r5", 1, ROWS, Settings_Experiment={"parameters": {"TrainingStage": None}}))
    found, unread = read(tmp_path, "r5")
    assert found == [] and unread == ["ses-005D1_2026-11-13T09-00-00 (Unreadable: TypeError)"]


def test_a_skipped_file_is_accounted_for_and_ignored(tmp_path):
    put(tmp_path, summary("ses-002D1_2026-11-10T09-00-00", "r2", 2, ROWS))
    put(tmp_path, {"version": 1, "run": "r3", "subject": "TC9", "session": "ses-003D1_2026-11-11T09-00-00", "skipped": "short"})
    found, unread = read(tmp_path, "r2", "r3")
    assert unread == [] and [e.name for e in found] == ["ses-002D1_2026-11-10T09-00-00"]


def test_any_version_but_1_is_refused(tmp_path):
    put(tmp_path, summary("ses-002D1_2026-11-10T09-00-00", "r2", 2, ROWS, version=2))
    found, unread = read(tmp_path, "r2")
    assert found == [] and "Version 2 Is Not Supported" in unread[0] and "No Summary File" in unread[1]
    put(tmp_path, {"run": "r3", "session": "ses-003D1_2026-11-11T09-00-00", "skipped": "short"})  # no version at all
    assert any("(Unreadable: KeyError)" in u for u in read(tmp_path, "r3")[1])


def test_another_workflow_s_summary_never_decides_but_is_accounted_for(tmp_path):
    # Audit, 10 Nov 2026, round 5: an av-dome experiment of the same mouse (80 visual Correct at stage 3)
    # was read as this task's and suggested Stage 4.
    other = "https://github.com/ucl-open/av-dome.git"
    put(tmp_path, summary("ses-010D1_2026-11-12T09-00-00", "r10", 3, [["visual", "Correct"]] * 80, repo=other))
    assert read(tmp_path, "r10") == ([], [])
    put(tmp_path, summary("ses-011D1_2026-11-13T09-00-00", "r11", 3, [["visual", "Correct"]] * 80))
    assert [e.name for e in read(tmp_path, "r10", "r11")[0]] == ["ses-011D1_2026-11-13T09-00-00"]


@pytest.mark.parametrize("repo", ["https://github.com/Coen-Lab/coen-dome-navigation", "https://github.com/someone/Coen-Dome-Navigation.git/",
                                  "https://github.com/ucl-open/coen-av-corridor.git", "https://github.com/ucl-open/auditory-vr-corridor"])
def test_the_repo_name_alone_decides_whatever_the_owner_fork_or_former_name(tmp_path, repo):
    # Pip, 15 Nov 2026: the newest session that matches, not identically; branches and forks count the same.
    put(tmp_path, summary("ses-002D1_2026-11-10T09-00-00", "r2", 2, ROWS, repo=repo))
    assert [e.name for e in read(tmp_path, "r2")[0]] == ["ses-002D1_2026-11-10T09-00-00"]


def test_a_name_that_only_contains_ours_is_another_workflow_and_no_repo_gives_no_stage(tmp_path):
    put(tmp_path, summary("ses-002D1_2026-11-10T09-00-00", "r2", 2, ROWS, repo="https://github.com/x/coen-dome-navigation-2p"))
    assert read(tmp_path, "r2") == ([], [])
    put(tmp_path, summary("ses-003D1_2026-11-11T09-00-00", "r3", 2, ROWS, repo=None))
    assert read(tmp_path, "r2", "r3")[1] == ["ses-003D1_2026-11-11T09-00-00.json (Unreadable: KeyError)"]


def test_a_summary_with_no_trials_or_no_trial_type_is_not_an_experiment(tmp_path):
    put(tmp_path, summary("ses-002D1_2026-11-10T09-00-00", "r2", 2, []))
    put(tmp_path, summary("ses-003D1_2026-11-11T09-00-00", "r3", 2, [[1]], columns=("trialNumber",)))
    assert read(tmp_path, "r2", "r3") == ([], [])


def test_the_opto_columns_are_read():
    # NaN is written as null
    s = summary("ses-005D1_2026-11-04T10-00-00", "r5", 4, [["auditory", "Correct", 2.5, 3], ["auditory", "Incorrect", None, None]],
                columns=("trialType", "outcome", "laserPowerMw", "laserCondition"))
    e = read_new(s)
    assert e.laser == [True, False] and e.optogenetics is True and e.rewarded == [True, False]


def test_the_script_runs_with_the_standard_library_alone(tmp_path):
    # rig-control runs it with its own Python, -I, outside this project: no package import.
    put(tmp_path, summary("ses-002D1_2026-11-10T09-00-00", "r2", 2, [["auditory", "Correct"]] * 100))
    done = subprocess.run([sys.executable, "-I", str(STAGE_PY), "--subject=TC9", "--modality=auditory",
                           f"--summaries={tmp_path}", f"--expected={expect(tmp_path, 'r2')}"],
                          capture_output=True, text=True, check=True)
    assert json.loads(done.stdout.strip().splitlines()[-1])["value"] == 3
