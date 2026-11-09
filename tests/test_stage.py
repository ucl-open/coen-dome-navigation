import csv
import json
from pathlib import Path

import pytest

from ucl_open_coen_av_corridor.stage import Experiment, experiments, suggest


def exp(name, stage, n, rewarded, modality="A", stages=None):
    """An experiment of n trials at one stage, the first `rewarded` of them rewarded."""
    return Experiment(name, name, [modality] * n, stages or [stage] * n, [i < rewarded for i in range(n)])


@pytest.mark.parametrize(
    "history, modality, stage",
    [
        ([], "A", 1),
        ([exp("2026-01-02", 3, 40, 40)], "A", 3),  # under 50 trials keeps the stage
        ([exp("2026-01-02", 1, 100, 70)], "A", 2),  # 70% advances
        ([exp("2026-01-02", 1, 100, 69)], "A", 1),
        ([exp("2026-01-02", 3, 200, 100)], "A", 4),  # first 100 all rewarded
        ([exp("2026-01-02", 5, 100, 100)], "A", 5),  # 5 never advances on its own
        ([exp("2026-01-02", 6, 100, 10)], "A", 6),  # 6 is kept
        ([exp("2026-01-02", 2, 100, 60), exp("2026-01-01", 2, 100, 50)], "A", 3),
        ([exp("2026-01-02", 2, 100, 60), exp("2026-01-01", 2, 100, 49)], "A", 2),
        ([exp("2026-01-02", 2, 100, 40)], "A", 1),  # stage 2 under 50% falls back
        ([exp("2026-01-02", 4, 100, 100, "V"), exp("2026-01-01", 2, 100, 60)], "A", 2),  # other modality ignored
        ([exp("2026-01-02", 1, 0, 0), exp("2026-01-01", 3, 40, 0)], "A", 3),  # an empty experiment is skipped
    ],
)
def test_the_rules_are_tom_s(history, modality, stage):
    assert suggest(history, modality)[0] == stage


@pytest.mark.parametrize("late, stage", [(40, 5), (39, 4)])
def test_stage_4_advances_on_its_first_150_trials(late, stage):
    # 65 of the first 100 (under 70%), `late` of the next 50: 105/150 is 70%, 104/150 is not.
    rewarded = [i < 65 for i in range(100)] + [i < late for i in range(50)] + [False] * 150
    e = Experiment("2026-01-02", "2026-01-02", ["A"] * 300, [4] * 300, rewarded)
    assert suggest([e], "A")[0] == stage


def test_the_rate_counts_every_trial_but_rewards_only_at_the_last_stage():
    # 60 trials at stage 1, all rewarded, then 40 at stage 2 none: 0/100 at stage 2, under 50%.
    e = exp("2026-01-02", 2, 100, 60, stages=[1] * 60 + [2] * 40)
    assert suggest([e], "A")[0] == 1


def write(path: Path, header: list[str], rows: list[list]):
    path.parent.mkdir(parents=True, exist_ok=True)
    with open(path, "w", newline="", encoding="utf-8") as f:
        w = csv.writer(f)
        w.writerow(header)
        w.writerows(rows)


def test_new_and_old_experiments_are_read_newest_first(tmp_path):
    new, old = tmp_path / "data", tmp_path / "Logs"
    write(
        new / "sub-TC9" / "ses-002D1_2026-11-10T09-00-00" / "BonsaiData" / "BonsaiData_Trial_2026-11-10T09-00-00.csv",
        ["Seconds", "trialNumber", "modality", "shapingStage", "outcome"],
        [[1.0, i + 1, "A", 3, "Rewarded"] for i in range(60)],
    )
    write(
        old / "sub-TC9" / "ses-A6_date-2026-10-01T09-00-00" / "TrialLog_A6_TC9" / "TrialLog_A6_TC9_x.csv",
        ["Modality", "ShapingStage", "WasRewarded"],
        [["A", 2, "True"] for _ in range(60)],
    )
    found = experiments("TC9", [new], [old])
    assert [e.name for e in found] == ["ses-002D1_2026-11-10T09-00-00", "ses-A6_date-2026-10-01T09-00-00"]
    assert suggest(found, "A")[0] == 4


def test_a_share_that_does_not_answer_gives_no_stage(tmp_path, monkeypatch, capsys):
    # Audit, 9 Nov 2026: an unreachable share read as no history, and a trained mouse was offered Stage 1.
    from ucl_open_coen_av_corridor import stage

    share = Path(r"\\server\rigs\staging")
    monkeypatch.setattr(stage, "unanswered", lambda root: root == share)
    monkeypatch.setattr("sys.argv", ["suggest-stage", "--subject", "TC9", "--modality", "A",
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
    write(destination / "sub-TC9" / name / "BonsaiData" / "BonsaiData_Trial_x.csv",
          ["modality", "shapingStage", "outcome"], [["A", 3, "Rewarded"]] * 100)
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
    from ucl_open_coen_av_corridor.stage import unanswered

    assert not unanswered(tmp_path / "missing")


def test_a_rig_folder_under_staging_is_read(tmp_path):
    write(
        tmp_path / "BLUE-OPTO-2" / "sub-TC9" / "ses-001O2_2026-11-10T09-00-00" / "BonsaiData" / "BonsaiData_Trial_x.csv",
        ["modality", "shapingStage", "outcome"],
        [["V", 1, "Unrewarded"]] * 50,
    )
    assert len(experiments("TC9", [tmp_path], [])) == 1
