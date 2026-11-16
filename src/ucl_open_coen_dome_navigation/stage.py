"""The training stage to suggest for a mouse's next experiment, from its earlier ones of the same
modality: Tom Childs' determine_shaping_stage (auditory-vr-corridor gen-sessions/helpers.py), rules
unchanged, reading the mouse's session summaries (jacuzzi-ingest's `sub-<lab ID>/<ses folder>.json`, version 1:
claude-tools records.md, "Session summaries"). The old workflow's TrialLog files (ShapingStage,
WasRewarded) and folders named by eartag are not read: their readers were deleted on 15 Nov 2026 (Pip).

Stages are the task file's trainingStages rows, from 0 (Pip, 9 Nov 2026: as av-dome's): Tom's stages 1
to 6 are 0 to 5. rig-control asks for it when a mouse and modality
are chosen and fills the TrainingStage setting with it; the experimenter can change it before Go. As a
command:

    python -I stage.py --subject TC003 --modality auditory --summaries DIR --expected FILE.json

--summaries is the folder holding the sub-<lab ID> folders and --expected a JSON list of the run ids
the mouse has had (rig-control writes it). Prints one JSON line, {"value": 3, "why": "..."}: the contract
of rig-control's [task.suggest]. If the folder does not answer, an expected run has no file, or a file
cannot be read, value is null and why names it: no stage is guessed. A skipped file (a run too short or
old to count) is accounted for and otherwise ignored.

The rules: the latest experiment of the modality with trials decides. Under 50 trials keeps its stage.
Stage 1 advances only after two experiments in a row each with at least 50 rewarded trials, and falls
back to 0 under 50% rewarded. Stages 2 and 3 also advance on 70% over the first 100 or 150 trials
(mice stop licking late on). Otherwise 70% over the experiment advances, below keeps. Stage 4 never
advances on its own; 5 is only chosen by hand and then kept. An optogenetics experiment (zapit-v2) keeps
its stage, and its laser-on trials never count toward a decision.
"""

import argparse
import json
import os
import re
from dataclasses import dataclass, field
from pathlib import Path

MAX_AUTO_STAGE = 4
MIN_TRIALS = 50
ADVANCE_RATE = 0.7
TWO_IN_A_ROW_STAGE = 1  # Tom's stage 2
TWO_IN_A_ROW_REWARDED = 50
REGRESS_RATE = 0.5
EARLY_ADVANCE_FROM = 2  # Tom's stage 3: stages 2 and 3 also advance on their first 100 or 150 trials
SUMMARY_VERSION = 1


@dataclass
class Experiment:
    """One experiment's trials, in the columns the rules need, oldest trial first."""

    name: str
    when: str  # sortable: the folder name's date and time
    modality: list[str]
    stage: list[int]
    rewarded: list[bool]
    laser: list[bool] = field(default_factory=list)  # laser-on trials (laserPowerMw > 0); empty when there were none
    optogenetics: bool = False  # an optogenetics experiment: every trial had a Zapit condition, laser on or laser-off


def _laser(e: "Experiment") -> list[bool]:
    return e.laser or [False] * len(e.rewarded)


def _when(folder: str) -> str:
    """ses-005D1_2026-11-04T10-00-00 sorts by its date."""
    m = re.search(r"(\d{4}-\d{2}-\d{2}T\d{2}-\d{2}-\d{2})", folder)
    return m.group(1) if m else ""


def read_new(summary: dict) -> Experiment | None:
    """An experiment of this workflow from its summary: trialType the modality, outcome Correct is rewarded, its
    stage the TrainingStage setting in Settings_Experiment (fixed for the experiment, so not a trial column).
    None without trials or without a trialType column."""
    columns, rows = summary["trials"]["columns"], summary["trials"]["rows"]
    if not rows or "trialType" not in columns:
        return None
    settings = summary["Settings_Experiment"]
    if settings is None:
        raise KeyError("Settings_Experiment")
    stage = int(float(settings["parameters"]["TrainingStage"]))
    rows = [dict(zip(columns, row)) for row in rows]
    name = summary["session"]
    return Experiment(name, _when(name), [r["trialType"] for r in rows], [stage] * len(rows),
                      [r.get("outcome") == "Correct" for r in rows],
                      [float(r.get("laserPowerMw") or 0) > 0 for r in rows],
                      any(int(float(r.get("laserCondition") or 0)) > 0 for r in rows))


# This repo's names, now and before the 9 Nov 2026 rename, and Tom Childs' original task (Pip, 15 Nov 2026).
OURS = ("coen-dome-navigation", "coen-av-corridor", "auditory-vr-corridor")


def ours(summary: dict) -> bool:
    """Whether a summary is this workflow's, by the repo's name alone: any owner, fork or branch counts the same.
    A summary without source.repo raises KeyError, so it is reported as unreadable."""
    repo = summary["source"]["repo"]
    if not repo:
        raise KeyError("source.repo")
    return repo.rstrip("/").rsplit("/", 1)[-1].lower().removesuffix(".git") in OURS


def summaries_of(subject: str, folder: Path, expected: Path, unread: list[str]) -> list[dict]:
    """The mouse's full summaries (skipped and other workflows' left out). Every problem is added to `unread`: a
    folder that does not answer, an expected run id with no file, a file that cannot be read or is not version 1."""
    try:
        os.listdir(folder)
    except OSError:
        unread.append(f"{folder} (No Answer)")
        return []
    try:
        wanted = json.loads(Path(expected).read_text(encoding="utf-8-sig"))
        if not isinstance(wanted, list):
            raise ValueError(expected)
    except (OSError, ValueError):
        unread.append(f"{expected} (Unreadable)")
        return []
    runs: set = set()
    full: dict[str, dict] = {}
    for file in sorted((folder / f"sub-{subject}").glob("*.json")):
        try:
            with open(file, encoding="utf-8-sig") as f:
                s = json.load(f)
            if s["version"] != SUMMARY_VERSION:
                unread.append(f"{file.name} (Version {s['version']} Is Not Supported)")
                continue
            runs.add(s["run"])
            if not s.get("skipped") and ours(s):
                full[s["run"]] = s
        except (OSError, ValueError, KeyError, TypeError, AttributeError) as error:
            unread.append(f"{file.name} (Unreadable: {type(error).__name__})")
    unread.extend(f"Run {run} (No Summary File)" for run in wanted if run not in runs)
    return list(full.values())


def experiments(subject: str, folder: Path, expected: Path, unread: list[str]) -> list[Experiment]:
    """Every experiment of the mouse with trials, newest first. Problems are added to `unread`."""
    found = []
    for s in summaries_of(subject, folder, expected, unread):
        try:
            e = read_new(s)
        except (OSError, ValueError, KeyError, TypeError) as error:
            # Never passed over unseen: it may be the experiment that decides (fail loudly)
            unread.append(f"{s['session']} (Unreadable: {type(error).__name__})")
            continue
        if e is not None:
            found.append(e)
    return sorted(found, key=lambda e: e.when, reverse=True)


def _rate(rewarded: list[bool]) -> float:
    return sum(rewarded) / len(rewarded) if rewarded else 0.0


def suggest(history: list[Experiment], modality: str) -> tuple[int, str]:
    """(stage, why) for the next experiment of this modality, from the mouse's experiments newest first."""
    same = [e for e in history if e.rewarded and e.modality[-1] == modality]
    if not same:
        return 0, f"No Earlier {modality.title()} Experiment With Trials: Stage 0"
    last = same[0]
    stage = last.stage[-1]
    if last.optogenetics:
        # An optogenetics experiment never changes the stage (zapit-v2): the laser is meant to change behaviour
        on = [r for r, l in zip(last.rewarded, _laser(last)) if l]
        off = [r for r, l in zip(last.rewarded, _laser(last)) if not l]
        return stage, (f"Optogenetics Experiment ({last.name}: {_rate(on):.0%} of {len(on)} Laser-On and {_rate(off):.0%} of "
                       f"{len(off)} Laser-Off Trials Rewarded): Stage {stage} Again")
    # Trials of the experiment's last stage only (a stage changed mid-experiment uses the last)
    rewarded = [r for r, s in zip(last.rewarded, last.stage) if s == stage]
    total = len(last.rewarded)
    # Over every trial of the experiment, as Tom's: rewarded at the last stage / all trials
    rate = sum(rewarded) / total
    where = f"{last.name}: {total} Trials, {rate:.0%} Rewarded at Stage {stage}"
    if total < MIN_TRIALS:
        return stage, f"Under {MIN_TRIALS} Trials ({where}): Stage {stage} Again"
    if stage == TWO_IN_A_ROW_STAGE:
        s2, n = TWO_IN_A_ROW_STAGE, TWO_IN_A_ROW_REWARDED
        before = [r for r, s, l in zip(same[1].rewarded, same[1].stage, _laser(same[1])) if s == s2 and not l] if len(same) > 1 else []
        if sum(rewarded) >= n and sum(before) >= n:
            return s2 + 1, f"{n}+ Rewarded in Two Experiments in a Row ({where}): Stage {s2 + 1}"
        if rate < REGRESS_RATE:
            return s2 - 1, f"Under {REGRESS_RATE:.0%} Rewarded ({where}): Back to Stage {s2 - 1}"
        return s2, f"Stage {s2} Needs {n}+ Rewarded in Two Experiments in a Row ({where}, Before {sum(before)}): Stage {s2} Again"
    if EARLY_ADVANCE_FROM <= stage < MAX_AUTO_STAGE:
        for n in (100, 150):
            early = rewarded[:n]
            if _rate(early) >= ADVANCE_RATE:
                return stage + 1, f"{ADVANCE_RATE:.0%}+ Over the First {len(early)} Trials ({where}): Stage {stage + 1}"
    if rate >= ADVANCE_RATE and stage < MAX_AUTO_STAGE:
        return stage + 1, f"{ADVANCE_RATE:.0%}+ Rewarded ({where}): Stage {stage + 1}"
    if rate >= ADVANCE_RATE:
        return stage, f"Stage {stage} Is Not Advanced Automatically ({where}): Stage {stage} Again"
    return stage, f"Under {ADVANCE_RATE:.0%} Rewarded ({where}): Stage {stage} Again"


def main() -> None:
    """Print the suggestion for --subject and --modality as one JSON line."""
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--subject", required=True)
    parser.add_argument("--modality", required=True, choices=["auditory", "visual", "audiovisual"])
    parser.add_argument("--summaries", required=True, type=Path, help="the folder of sub-<lab ID> folders of session summaries")
    parser.add_argument("--expected", required=True, type=Path, help="a JSON file listing the run ids the mouse has had")
    args = parser.parse_args()
    unread: list[str] = []
    history = experiments(args.subject, args.summaries, args.expected, unread)
    if unread:
        # The mouse's history may be there alone: no guess, or a trained mouse would be offered Stage 1.
        print(json.dumps({"value": None, "why": f"Could Not Read {' and '.join(unread)}: Choose the Stage by Hand"}))
        return
    stage, why = suggest(history, args.modality)
    print(json.dumps({"value": stage, "why": why}))


if __name__ == "__main__":
    main()
