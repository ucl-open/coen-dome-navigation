"""The shaping stage to suggest for a mouse's next experiment, from its earlier ones of the same
modality: Tom Childs' determine_shaping_stage (auditory-vr-corridor gen-sessions/helpers.py), rules
unchanged, reading both this workflow's BonsaiData_Trial and the old workflow's TrialLog files.

rig-control asks for it when a mouse and modality are chosen and fills the task's shapingStage with
it; the experimenter can change it before Go. As a command:

    uv run suggest-stage --subject TC003 --modality A [--root DIR ...] [--legacy-root DIR ...]

prints one JSON line, {"value": 3, "why": "..."}: the contract of rig-control's [task.suggest]. If
a share among the roots does not answer, or an experiment there has only its manifest left (its data
ingested elsewhere), value is null and why names it: no stage is guessed, since the mouse's history
may be only there (rig-sync deletes a rig's copy after 72 h).

The rules: the latest experiment of the modality with trials decides. Under 50 trials keeps its stage.
Stage 2 advances only after two experiments in a row each with at least 50 rewarded trials, and falls
back to 1 under 50% rewarded. Stages 3 and 4 also advance on 70% over the first 100 or 150 trials
(mice stop licking late on). Otherwise 70% over the experiment advances, below keeps. Stage 5 never
advances on its own; 6 is only chosen by hand and then kept.
"""

import argparse
import csv
import json
import re
from dataclasses import dataclass
from pathlib import Path

MAX_SHAPING_STAGE = 6
MAX_AUTO_STAGE = 5
MIN_TRIALS = 50
ADVANCE_RATE = 0.7
STAGE2_REWARDED = 50
STAGE2_REGRESS_RATE = 0.5

# This workflow's experiments: on the rig until rig-sync copies them, then in the server's staging.
ROOTS = [Path(r"C:\Coen-Lab-Local\Acquired-Data"), Path(r"\\128.40.51.157\rigs\staging")]
# The old workflow's TrialLogs: each rig's clone wrote Logs\, which was pushed to the project share.
LEGACY_ROOTS = [
    Path(r"C:\Github\Coen-Lab\auditory-vr-corridor\Logs"),
    Path(r"\\rdp.arc.ucl.ac.uk\ritd-ag-project-rd01n9-pcoen00\AV_VR_Corridor"),
]


@dataclass
class Experiment:
    """One experiment's trials, in the columns the rules need, oldest trial first."""

    name: str
    when: str  # sortable: the folder name's date and time
    modality: list[str]
    stage: list[int]
    rewarded: list[bool]


def _bool(text: str) -> bool:
    return text.strip().lower() in ("true", "1")


def _when(folder: str) -> str:
    """ses-005D1_2026-11-04T10-00-00 and the old ses-A6_date-2026-08-05T10-00-00 both sort by their date."""
    m = re.search(r"(\d{4}-\d{2}-\d{2}T\d{2}-\d{2}-\d{2})", folder)
    return m.group(1) if m else ""


def _read(csv_file: Path) -> list[dict]:
    with open(csv_file, newline="", encoding="utf-8-sig") as f:
        return list(csv.DictReader(f))


def read_new(folder: Path) -> Experiment | None:
    """An experiment of this workflow: BonsaiData\\BonsaiData_Trial_*.csv."""
    logs = sorted(folder.glob("BonsaiData/BonsaiData_Trial*.csv"))
    if not logs:
        return None
    rows = [r for log in logs for r in _read(log)]
    if not rows or "modality" not in rows[0]:
        return None
    return Experiment(
        folder.name,
        _when(folder.name),
        [r["modality"] for r in rows],
        [int(r["shapingStage"]) for r in rows],
        [r["outcome"] == "Rewarded" for r in rows],
    )


def read_legacy(folder: Path) -> Experiment | None:
    """A session of the old workflow: its latest TrialLog_*.csv (columns Modality, ShapingStage, WasRewarded)."""
    logs = list(folder.glob("**/TrialLog_*.csv"))
    if not logs:
        return None
    rows = _read(max(logs, key=lambda p: p.stat().st_mtime))
    if not rows or "Modality" not in rows[0]:
        return None
    return Experiment(
        folder.name,
        _when(folder.name),
        [r["Modality"] for r in rows],
        [int(r["ShapingStage"]) for r in rows],
        [_bool(r["WasRewarded"]) for r in rows],
    )


def unanswered(root: Path) -> bool:
    """A share (\\\\server\\...) that cannot be listed: glob reads it as empty, which would look like a
    mouse with no history. A local folder that is not there just has none."""
    if not str(root).startswith("\\\\"):
        return False
    try:
        return not root.is_dir()
    except OSError:
        return True


def experiments(subject: str, roots: list[Path], legacy_roots: list[Path], unread: list[str] | None = None) -> list[Experiment]:
    """Every experiment of the mouse in the roots, newest first; a folder seen twice (on the rig and in
    staging) counts once. A share that does not answer is added to `unread`."""
    found: dict[str, Experiment] = {}
    for reader, bases in ((read_new, roots), (read_legacy, legacy_roots)):
        for base in bases:
            try:
                if unanswered(base):
                    raise OSError(base)
                folders = [*base.glob(f"sub-{subject}/ses-*"), *base.glob(f"*/sub-{subject}/ses-*")]
            except OSError:
                if unread is not None:
                    unread.append(f"{base} (No Answer)")
                continue
            for folder in folders:
                if folder.name in found or not folder.is_dir():
                    continue
                if reader is read_new and (folder / "manifest.json").is_file() and not (folder / "BonsaiData").is_dir():
                    # Ingested: staging keeps only the manifest, so an older experiment would decide
                    # (audit round 2, 9 Nov 2026). Every experiment writes BonsaiData (Go is logged).
                    if unread is not None:
                        unread.append(f"{folder.name} (Only Its Manifest Is Left)")
                    continue
                try:
                    e = reader(folder)
                except (OSError, ValueError, KeyError):
                    continue
                if e is not None:
                    found[folder.name] = e
    return sorted(found.values(), key=lambda e: e.when, reverse=True)


def _rate(rewarded: list[bool]) -> float:
    return sum(rewarded) / len(rewarded) if rewarded else 0.0


def suggest(history: list[Experiment], modality: str) -> tuple[int, str]:
    """(stage, why) for the next experiment of this modality, from the mouse's experiments newest first."""
    same = [e for e in history if e.rewarded and e.modality[-1] == modality]
    if not same:
        return 1, f"No Earlier {modality} Experiment With Trials: Stage 1"
    last = same[0]
    stage = last.stage[-1]
    # Trials of the experiment's last stage only (a stage changed mid-experiment uses the last)
    rewarded = [r for r, s in zip(last.rewarded, last.stage) if s == stage]
    total = len(last.rewarded)
    # Over every trial of the experiment, as Tom's: rewarded at the last stage / all trials
    rate = sum(rewarded) / total
    where = f"{last.name}: {total} trials, {rate:.0%} rewarded at stage {stage}"
    if total < MIN_TRIALS:
        return stage, f"Under {MIN_TRIALS} Trials ({where}): Stage {stage} Again"
    if stage == 2:
        before = [r for r, s in zip(same[1].rewarded, same[1].stage) if s == 2] if len(same) > 1 else []
        if sum(rewarded) >= STAGE2_REWARDED and sum(before) >= STAGE2_REWARDED:
            return 3, f"{STAGE2_REWARDED}+ Rewarded in Two Experiments in a Row ({where}): Stage 3"
        if rate < STAGE2_REGRESS_RATE:
            return 1, f"Under {STAGE2_REGRESS_RATE:.0%} Rewarded ({where}): Back to Stage 1"
        return 2, f"Stage 2 Needs {STAGE2_REWARDED}+ Rewarded in Two Experiments in a Row ({where}, before {sum(before)}): Stage 2 Again"
    if 3 <= stage < MAX_AUTO_STAGE:
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
    parser.add_argument("--modality", required=True, choices=["A", "V", "AV"])
    parser.add_argument("--root", action="append", type=Path, help="where this workflow's experiments are (repeatable)")
    parser.add_argument("--legacy-root", action="append", type=Path, help="where the old workflow's Logs are (repeatable)")
    args = parser.parse_args()
    unread: list[str] = []
    history = experiments(args.subject, args.root or ROOTS, args.legacy_root or LEGACY_ROOTS, unread)
    if unread:
        # The mouse's history may be there alone (a rig keeps its copy 72 h): no guess (audit, 9 Nov 2026,
        # a trained mouse was offered Stage 1).
        print(json.dumps({"value": None, "why": f"Could Not Read {' and '.join(unread)}: Choose the Stage by Hand"}))
        return
    stage, why = suggest(history, args.modality)
    print(json.dumps({"value": stage, "why": why}))


if __name__ == "__main__":
    main()
