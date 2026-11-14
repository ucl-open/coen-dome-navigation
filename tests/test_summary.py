"""summary.py: one experiment from its files, with its consistency checks."""

import csv
import json
import math

from ucl_open_coen_dome_navigation.summary import summarise

COLUMNS = ["Seconds", "trialNumber", "trialType", "outcome", "endReason", "punishSound", "punishTimeoutSeconds",
           "itiStartTime", "itiSeconds", "quietWaitSeconds", "trialStartTime", "stimulusOnsetTime", "rewardSizeUl"]


def _trial(n, start, iti, response, outcome="Correct", timeout=0.0, quiet=0.0, reward=2.0):
    onset = start + iti + quiet
    return {"Seconds": onset + response, "trialNumber": n, "trialType": "auditory", "outcome": outcome,
            "endReason": "FreqReached", "punishSound": False, "punishTimeoutSeconds": timeout, "itiStartTime": start,
            "itiSeconds": iti, "quietWaitSeconds": quiet, "trialStartTime": onset - 0.1, "stimulusOnsetTime": onset,
            "rewardSizeUl": reward}


def _experiment(tmp_path, trials, rewards, outcome="completed", final=True):
    folder = tmp_path / "ses-x_2026-11-14T01-00-00"
    (folder / "BonsaiData").mkdir(parents=True)
    with open(folder / "BonsaiData" / "BonsaiData_Trial_2026-11-14T01-00-00.csv", "w", newline="") as f:
        w = csv.DictWriter(f, COLUMNS)
        w.writeheader()
        w.writerows(trials)
    with open(folder / "BonsaiData" / "BonsaiData_Async_2026-11-14T01-00-00.csv", "w", newline="") as f:
        w = csv.writer(f)
        w.writerow(["Seconds", "Event", "Detail", "Value"])
        w.writerows([[t, "RewardDelivered", "left DO1", ul] for t, ul in rewards])
    (folder / "manifest.json").write_text(json.dumps({"run": {"outcome": outcome, "final": final}}))
    return folder


def _chain(n):
    """n consistent trials: each interval starts at the last outcome plus its timeout."""
    trials, start = [], 1000.0
    for i in range(1, n + 1):
        t = _trial(i, start, 2.5, 3.0, outcome="Correct" if i % 2 else "Incorrect", timeout=0.0 if i % 2 else 4.0)
        trials.append(t)
        start = t["Seconds"] + t["punishTimeoutSeconds"]
    return trials


def test_a_consistent_experiment_has_no_problems(tmp_path):
    trials = _chain(4)
    rewards = [(t["Seconds"], 2.0) for t in trials if t["outcome"] == "Correct"]
    s = summarise(_experiment(tmp_path, trials, rewards))
    assert (s.outcome, s.trials, s.correct, s.performance) == ("completed", 4, 2, 0.5)
    assert s.problems == []
    assert s.response_seconds == [3.0] * 4
    assert [round(x, 6) for x in s.interval_seconds] == [2.5, 6.5, 2.5]
    assert (s.requested_ul, s.delivered_ul) == (4.0, 4.0)


def test_an_uncalibrated_line_asks_water_but_delivers_none(tmp_path):
    trials = _chain(2)
    s = summarise(_experiment(tmp_path, trials, [(trials[0]["Seconds"], 0.0)]))
    assert (s.requested_ul, s.delivered_ul, s.problems) == (2.0, 0.0, [])


def test_each_inconsistency_is_named(tmp_path):
    trials = _chain(3)
    trials[1]["stimulusOnsetTime"] += 0.01  # the stimulus 10 ms late
    trials[2]["itiStartTime"] += 0.5  # the interval not from the outcome
    trials[2]["stimulusOnsetTime"] += 0.5
    trials[2]["trialNumber"] = 4  # a trial missing
    s = summarise(_experiment(tmp_path, trials, []))  # and no reward logged for trial 1
    text = "\n".join(s.problems)
    assert "trial 2: stimulus" in text
    assert "trial 4: interval started +0.5000 s" in text
    assert "row 3 is trial 4" in text
    assert "trial 1: Correct with rewardSizeUl 2.0 but no RewardDelivered" in text


def test_no_trials_and_no_manifest(tmp_path):
    folder = _experiment(tmp_path, [], [])
    (folder / "manifest.json").unlink()
    s = summarise(folder)
    assert (s.outcome, s.trials) == ("unknown", 0) and math.isnan(s.performance) and s.problems == []


def test_a_run_that_never_closed_says_so(tmp_path):
    s = summarise(_experiment(tmp_path, _chain(1), [], outcome="stopped", final=False))
    assert s.outcome == "stopped (not final: the workflow never finished closing)"
