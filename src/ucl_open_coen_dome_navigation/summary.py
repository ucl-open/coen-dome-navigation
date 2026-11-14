"""One experiment summarised from its files alone, as an analyst would read it a year later.

Reads BonsaiData\\BonsaiData_Trial_*.csv (columns as schemas\\trial-columns.yml), BonsaiData\\BonsaiData_Async_*.csv
(each RewardDelivered row, Value the microlitres delivered) and manifest.json (how the run ended). Computes
performance, response times and intervals with no special cases, and checks the rows against each other:

- trial numbers run 1, 2, 3 ... with none missing;
- each stimulus came itiSeconds plus quietWaitSeconds after its interval started (itiStartTime), within 2 ms;
- each interval after the first started at the last trial's outcome plus its punishTimeoutSeconds, within 2 ms;
- each trial with a reward (rewardSizeUl above 0 and outcome Correct) has a RewardDelivered row at its outcome.

`summarise-experiment <experiment folder>` prints the summary and each failed check, exiting 1 when one fails.
"""

from __future__ import annotations

import argparse
import csv
import json
import statistics
import sys
from dataclasses import dataclass, field
from pathlib import Path

TOLERANCE_S = 0.002  # Harp-clock waits land within a tick or two of 1 ms


@dataclass
class Summary:
    folder: str
    outcome: str  # the manifest's run outcome (completed, stopped, ...), "not final" when it never closed; or unknown
    trials: int
    correct: int
    performance: float  # correct / trials; NaN with no trials
    response_seconds: list[float] = field(default_factory=list)  # each trial's outcome less its stimulus onset
    interval_seconds: list[float] = field(default_factory=list)  # each outcome to the next stimulus onset
    requested_ul: float = 0.0  # the task's rewards: rewardSizeUl of each Correct trial
    delivered_ul: float = 0.0  # every RewardDelivered row's Value (manual rewards included)
    problems: list[str] = field(default_factory=list)


def _rows(folder: Path, pattern: str) -> list[dict]:
    rows = []
    for log in sorted(folder.glob(pattern)):
        with open(log, newline="", encoding="utf-8-sig") as f:
            rows += list(csv.DictReader(f))
    return rows


def _f(row: dict, column: str) -> float:
    return float(row[column])


def summarise(folder: Path) -> Summary:
    folder = Path(folder)
    trials = _rows(folder, "BonsaiData/BonsaiData_Trial*.csv")
    events = _rows(folder, "BonsaiData/BonsaiData_Async*.csv")
    try:
        run = json.loads((folder / "manifest.json").read_text(encoding="utf-8-sig"))["run"]
        outcome = (run["outcome"] or "unknown") + (" (not final: the workflow never finished closing)" if run.get("final") is False else "")
    except (OSError, ValueError, KeyError, TypeError):
        outcome = "unknown"
    correct = sum(r["outcome"] == "Correct" for r in trials)
    s = Summary(folder.name, outcome, len(trials), correct, correct / len(trials) if trials else float("nan"))
    rewards = [e for e in events if e["Event"] == "RewardDelivered"]
    s.delivered_ul = sum(float(e["Value"] or 0) for e in rewards)
    reward_times = [float(e["Seconds"]) for e in rewards]
    for n, row in enumerate(trials):
        number = int(row["trialNumber"])
        if number != n + 1:
            s.problems.append(f"row {n + 1} is trial {number}: trial numbers should run 1, 2, 3 ...")
        onset, end = _f(row, "stimulusOnsetTime"), _f(row, "Seconds")
        s.response_seconds.append(end - onset)
        planned = _f(row, "itiSeconds") + _f(row, "quietWaitSeconds")
        if abs(onset - _f(row, "itiStartTime") - planned) > TOLERANCE_S:
            s.problems.append(f"trial {number}: stimulus {onset - _f(row, 'itiStartTime'):.4f} s after its interval started, "
                              f"not itiSeconds + quietWaitSeconds = {planned:.4f} s")
        if n:
            last = trials[n - 1]
            due = _f(last, "Seconds") + _f(last, "punishTimeoutSeconds")
            if abs(_f(row, "itiStartTime") - due) > TOLERANCE_S:
                s.problems.append(f"trial {number}: interval started {_f(row, 'itiStartTime') - due:+.4f} s from the last "
                                  "outcome plus its timeout")
            s.interval_seconds.append(onset - _f(last, "Seconds"))
        if row["outcome"] == "Correct" and _f(row, "rewardSizeUl") > 0:
            s.requested_ul += _f(row, "rewardSizeUl")
            if not any(abs(t - end) <= TOLERANCE_S for t in reward_times):
                s.problems.append(f"trial {number}: Correct with rewardSizeUl {row['rewardSizeUl']} but no RewardDelivered at its outcome")
    return s


def describe(s: Summary) -> str:
    def spread(values: list[float]) -> str:
        return f"median {statistics.median(values):.2f} s (range {min(values):.2f} to {max(values):.2f})" if values else "none"

    lines = [f"{s.folder}: {s.outcome}, {s.trials} trials, {s.correct} correct"
             + (f" ({s.performance:.0%})" if s.trials else ""),
             f"  response (outcome less stimulus onset): {spread(s.response_seconds)}",
             f"  outcome to next stimulus: {spread(s.interval_seconds)}",
             f"  water: {s.requested_ul:g} ul asked by the task, {s.delivered_ul:g} ul delivered (RewardDelivered)"]
    lines += [f"  PROBLEM {p}" for p in s.problems]
    return "\n".join(lines)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("folders", nargs="+", type=Path, help="experiment folders (ses-...)")
    args = parser.parse_args()
    failed = False
    for folder in args.folders:
        s = summarise(folder)
        print(describe(s))
        failed |= bool(s.problems)
    sys.exit(1 if failed else 0)


if __name__ == "__main__":
    main()
