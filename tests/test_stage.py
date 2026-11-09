import csv
import json
from pathlib import Path

import pytest

from ucl_open_coen_dome_navigation.stage import Experiment, experiments, suggest

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


def write(path: Path, header: list[str], rows: list[list]):
    path.parent.mkdir(parents=True, exist_ok=True)
    with open(path, "w", newline="", encoding="utf-8") as f:
        w = csv.writer(f)
        w.writerow(header)
        w.writerows(rows)


def write_new(folder: Path, stage: int, rows: list[list], header=("trialType", "outcome")):
    """An experiment of this workflow: its trial log, and its stage in Settings_Experiment as ExperimentSettings writes it."""
    write(folder / "BonsaiData" / "BonsaiData_Trial_x.csv", list(header), rows)
    (folder / "Settings").mkdir()
    settings = {"subject_id": "TC9", "parameters": {"TrainingStage": str(stage), "ManualRewardUl": "2"}}
    (folder / "Settings" / "Settings_Experiment_x.json").write_text(json.dumps(settings), encoding="utf-8")


def test_new_and_old_experiments_are_read_newest_first(tmp_path):
    new, old = tmp_path / "data", tmp_path / "Logs"
    write_new(new / "sub-TC9" / "ses-002D1_2026-11-10T09-00-00", 2,
              [[1.0, i + 1, "auditory", "Correct"] for i in range(60)], ("Seconds", "trialNumber", "trialType", "outcome"))
    write(
        old / "sub-TC9" / "ses-A6_date-2026-10-01T09-00-00" / "TrialLog_A6_TC9" / "TrialLog_A6_TC9_x.csv",
        ["Modality", "ShapingStage", "WasRewarded"],
        [["A", 2, "True"] for _ in range(60)],
    )
    found = experiments("TC9", [new], [old])
    assert [e.name for e in found] == ["ses-002D1_2026-11-10T09-00-00", "ses-A6_date-2026-10-01T09-00-00"]
    assert found[1].stage[0] == 1 and found[1].modality[0] == "auditory"  # Tom's stage 2, his A, read as row 1
    assert suggest(found, "auditory")[0] == 3


def test_a_share_that_does_not_answer_gives_no_stage(tmp_path, monkeypatch, capsys):
    # Audit, 9 Nov 2026: an unreachable share read as no history, and a trained mouse was offered Stage 1.
    from ucl_open_coen_dome_navigation import stage

    share = Path(r"\\server\rigs\staging")
    monkeypatch.setattr(stage, "unanswered", lambda root: root == share)
    monkeypatch.setattr("sys.argv", ["suggest-stage", "--subject", "TC9", "--modality", "auditory",
                                     "--root", str(tmp_path), "--root", str(share), "--legacy-root", str(tmp_path)])
    stage.main()
    answer = json.loads(capsys.readouterr().out)
    assert answer["value"] is None and str(share) in answer["why"]


def test_an_experiment_with_only_its_manifest_left_gives_no_stage(tmp_path):
    # Audit round 2, 9 Nov 2026: ingest clears staging but the manifest, so an older session would decide.
    folder = tmp_path / "sub-TC9" / "ses-003D1_2026-11-12T09-00-00"
    folder.mkdir(parents=True)
    (folder / "manifest.json").write_text("{}", encoding="utf-8")
    unread = []
    assert experiments("TC9", [tmp_path], [], unread) == []
    assert unread == ["ses-003D1_2026-11-12T09-00-00 (Only Its Manifest Is Left)"]


@pytest.mark.parametrize("destination_first", [True, False])
def test_an_ingested_experiment_whose_data_is_in_another_root_is_read_in_any_order(tmp_path, destination_first):
    # Audit round 3, 9 Nov 2026: with ingest's destination added after staging, staging's manifest
    # answered "Only Its Manifest Is Left" first and every suggestion stayed null.
    name = "ses-003D1_2026-11-12T09-00-00"
    staging, destination = tmp_path / "staging", tmp_path / "destination"
    (staging / "sub-TC9" / name).mkdir(parents=True)
    (staging / "sub-TC9" / name / "manifest.json").write_text("{}", encoding="utf-8")
    write_new(destination / "sub-TC9" / name, 3, [["auditory", "Correct"]] * 100)
    unread = []
    roots = [destination, staging] if destination_first else [staging, destination]
    found = experiments("TC9", roots, [], unread)
    assert unread == [] and [e.name for e in found] == [name]


def test_another_workflow_s_ingested_experiment_never_decides(tmp_path):
    # Audit round 3, 9 Nov 2026: an av-dome experiment of the same mouse, ingested, blocked the suggestion.
    folder = tmp_path / "sub-TC9" / "ses-009_2026-11-12T09-00-00"
    folder.mkdir(parents=True)
    (folder / "manifest.json").write_text(json.dumps({"source": {"repo": "https://github.com/ucl-open/av-dome.git"}}), encoding="utf-8")
    unread = []
    assert experiments("TC9", [tmp_path], [], unread) == [] and unread == []


def test_a_local_folder_that_is_not_there_is_no_history(tmp_path):
    from ucl_open_coen_dome_navigation.stage import unanswered

    assert not unanswered(tmp_path / "missing")


def test_a_rig_folder_under_staging_is_read(tmp_path):
    write_new(tmp_path / "BLUE-OPTO-2" / "sub-TC9" / "ses-001O2_2026-11-10T09-00-00", 1, [["visual", "Incorrect"]] * 50)
    found = experiments("TC9", [tmp_path], [])
    assert len(found) == 1 and found[0].modality[0] == "visual" and not any(found[0].rewarded)
    assert found[0].stage == [1] * 50


def test_an_optogenetics_experiment_keeps_its_stage():
    # zapit-v2: 100% rewarded would advance stage 3 (Tom's 4), but an optogenetics experiment never changes the stage
    e = Experiment("2026-01-02", "2026-01-02", ["auditory"] * 200, [3] * 200, [True] * 200, [i % 3 == 0 for i in range(200)], True)
    stage, why = suggest([e], "auditory")
    assert stage == 3 and why.startswith("Optogenetics Experiment") and "of 67 laser-on" in why


def test_laser_on_trials_do_not_count_in_stage_1_s_two_experiments_in_a_row():
    # The earlier experiment has 50 rewarded stage-1 trials only if its laser-on ones count
    from ucl_open_coen_dome_navigation.stage import TWO_IN_A_ROW_STAGE
    now = exp("2026-01-02", TWO_IN_A_ROW_STAGE, 100, 60)
    before = Experiment("2026-01-01", "2026-01-01", ["auditory"] * 100, [TWO_IN_A_ROW_STAGE] * 100,
                        [i < 50 for i in range(100)], [i < 10 for i in range(100)], True)
    assert suggest([now, before], "auditory")[0] == TWO_IN_A_ROW_STAGE


def test_the_opto_columns_are_read(tmp_path):
    from ucl_open_coen_dome_navigation.stage import read_new
    folder = tmp_path / "ses-005D1_2026-11-04T10-00-00"
    write_new(folder, 4, [["auditory", "Correct", "True", 3], ["auditory", "Incorrect", "False", 1]],
              header=("trialType", "outcome", "optoStimulated", "optoCondition"))
    e = read_new(folder)
    assert e.laser == [True, False] and e.optogenetics is True and e.rewarded == [True, False]
