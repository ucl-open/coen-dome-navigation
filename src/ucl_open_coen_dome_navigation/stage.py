"""The training stage to suggest for a mouse's next experiment, from its earlier ones of the same
modality: Tom Childs' determine_shaping_stage (auditory-vr-corridor gen-sessions/helpers.py), rules
unchanged, reading both this workflow's BonsaiData_Trial and the old workflow's TrialLog files.

Stages are the task file's trainingStages rows, from 0 (Pip, 9 Nov 2026: as av-dome's): Tom's stages 1
to 6 are 0 to 5, and his old logs are read one lower. rig-control asks for it when a mouse and modality
are chosen and fills the TrainingStage setting with it; the experimenter can change it before Go. As a
command:

    uv run suggest-stage --subject TC003 --modality auditory [--root DIR ...] [--legacy-root DIR ...]

prints one JSON line, {"value": 3, "why": "..."}: the contract of rig-control's [task.suggest]. If
a share among the roots does not answer, an experiment there cannot be read, or one has only its manifest
left (its data ingested elsewhere), value is null and why names it: no stage is guessed, since the mouse's history
may be only there (rig-sync deletes a rig's copy after 72 h).

The rules: the latest experiment of the modality with trials decides. Under 50 trials keeps its stage.
Stage 1 advances only after two experiments in a row each with at least 50 rewarded trials, and falls
back to 0 under 50% rewarded. Stages 2 and 3 also advance on 70% over the first 100 or 150 trials
(mice stop licking late on). Otherwise 70% over the experiment advances, below keeps. Stage 4 never
advances on its own; 5 is only chosen by hand and then kept. An optogenetics experiment (zapit-v2) keeps
its stage, and its laser-on trials never count toward a decision.
"""

import argparse
import csv
import json
import re
from dataclasses import dataclass, field
from pathlib import Path

MAX_STAGE = 5
MAX_AUTO_STAGE = 4
MIN_TRIALS = 50
ADVANCE_RATE = 0.7
TWO_IN_A_ROW_STAGE = 1  # Tom's stage 2
TWO_IN_A_ROW_REWARDED = 50
REGRESS_RATE = 0.5
EARLY_ADVANCE_FROM = 2  # Tom's stage 3: stages 2 and 3 also advance on their first 100 or 150 trials

# This workflow's experiments: on the rig until rig-sync copies them, then in the server's staging, then where
# jacuzzi-ingest files them (Live-Data, <MACHINE>\sub-<lab ID>\ses-..., the project share's root). Folders are named
# by the mouse's lab ID (plan v11), the SubjectId rig-control launches with, as the old TrialLogs' were.
LIVE_DATA = Path(r"\\rdp.arc.ucl.ac.uk\ritd-ag-project-rd01n9-pcoen00")
ROOTS = [Path(r"C:\Coen-Lab-Local\Acquired-Data"), Path(r"\\128.40.51.157\rigs\staging"), LIVE_DATA]
# The old workflow's TrialLogs: each rig's clone wrote Logs\, which was pushed to the project share.
LEGACY_ROOTS = [
    Path(r"C:\Github\Coen-Lab\auditory-vr-corridor\Logs"),
    LIVE_DATA / "AV_VR_Corridor",
]


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


def _bool(text: str) -> bool:
    return text.strip().lower() in ("true", "1")


def _when(folder: str) -> str:
    """ses-005D1_2026-11-04T10-00-00 and the old ses-A6_date-2026-08-05T10-00-00 both sort by their date."""
    m = re.search(r"(\d{4}-\d{2}-\d{2}T\d{2}-\d{2}-\d{2})", folder)
    return m.group(1) if m else ""


def _read(csv_file: Path) -> list[dict]:
    with open(csv_file, newline="", encoding="utf-8-sig") as f:
        return list(csv.DictReader(f))


# Tom Childs' TrialLog wrote the modality as a code; the trial log and the task file now say it in words.
LEGACY_MODALITY = {"A": "auditory", "V": "visual", "AV": "audiovisual"}


def read_new(folder: Path) -> Experiment | None:
    """An experiment of this workflow: BonsaiData\\BonsaiData_Trial_*.csv (trialType the modality, outcome Correct
    is rewarded), its stage the TrainingStage setting in Settings\\Settings_Experiment_*.json (fixed for the
    experiment, so not a trial column)."""
    logs = sorted(folder.glob("BonsaiData/BonsaiData_Trial*.csv"))
    if not logs:
        return None
    rows = [r for log in logs for r in _read(log)]
    if not rows or "trialType" not in rows[0]:
        return None
    settings = sorted(folder.glob("Settings/Settings_Experiment*.json"))
    if not settings:
        raise FileNotFoundError(f"{folder} has trials but no Settings\\Settings_Experiment_*.json")
    stage = int(float(json.loads(settings[-1].read_text(encoding="utf-8-sig"))["parameters"]["TrainingStage"]))
    return Experiment(folder.name, _when(folder.name), [r["trialType"] for r in rows], [stage] * len(rows),
                      [r["outcome"] == "Correct" for r in rows],
                      [float(r.get("laserPowerMw") or 0) > 0 for r in rows],
                      any(int(float(r.get("laserCondition") or 0)) > 0 for r in rows))


def read_legacy(folder: Path) -> Experiment | None:
    """An experiment of the old workflow: its latest TrialLog_*.csv (columns Modality, ShapingStage, WasRewarded),
    the modality in words and the stage one lower (Tom's 1 to 6 are 0 to 5)."""
    logs = list(folder.glob("**/TrialLog_*.csv"))
    if not logs:
        return None
    rows = _read(max(logs, key=lambda p: p.stat().st_mtime))
    if not rows or "Modality" not in rows[0]:
        return None
    return Experiment(
        folder.name,
        _when(folder.name),
        [LEGACY_MODALITY[r["Modality"]] for r in rows],
        [int(r["ShapingStage"]) - 1 for r in rows],
        [_bool(r["WasRewarded"]) for r in rows],
        [_bool(r.get("OptogeneticEnabled") or "") for r in rows],  # auditory-vr-corridor's zapit-integration-v2 TrialLog
        any(int(float(r.get("OptogeneticCondition") or 0)) > 0 for r in rows),
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


def ours(manifest: Path) -> bool:
    """Whether an experiment's manifest.json is this workflow's (source.repo); one that cannot be
    read counts as ours, so no history is passed over unseen."""
    try:
        repo = json.loads(manifest.read_text(encoding="utf-8-sig")).get("source", {}).get("repo") or ""
    except (OSError, ValueError, AttributeError):
        return True
    return not repo or "coen-dome-navigation" in repo


def _subject_folders(base: Path, subject: str, unread: list[str] | None, eartag: str = "") -> list[Path]:
    """The mouse's experiment folders under one root; none, and the root added to `unread`, if it does not answer."""
    try:
        if unanswered(base):
            raise OSError(base)
        names = [n for n in dict.fromkeys((subject, eartag)) if n]  # runs before 13 Nov 2026 were filed under the eartag
        return [f for n in names for f in (*base.glob(f"sub-{n}/ses-*"), *base.glob(f"*/sub-{n}/ses-*"))]
    except OSError:
        if unread is not None:
            unread.append(f"{base} (No Answer)")
        return []


def experiments(subject: str, roots: list[Path], legacy_roots: list[Path], unread: list[str] | None = None, eartag: str = "") -> list[Experiment]:
    """Every experiment of the mouse in the roots, newest first; a folder seen twice (on the rig and in
    staging) counts once. A share that does not answer is added to `unread`, as is an experiment of
    this workflow with only its manifest left and its data in none of the roots."""
    found: dict[str, Experiment] = {}
    manifest_only: list[str] = []
    for reader, bases in ((read_new, roots), (read_legacy, legacy_roots)):
        for base in bases:
            for folder in _subject_folders(base, subject, unread, eartag):
                if folder.name in found or not folder.is_dir():
                    continue
                if reader is read_new and (folder / "manifest.json").is_file():
                    if not ours(folder / "manifest.json"):
                        # Another workflow's experiment never decides: av-dome's trial log has trialType
                        # (visual, auditory) and outcome too, so it read as this task's (audit, 10 Nov 2026, round 5).
                        continue
                    if not (folder / "BonsaiData").is_dir():
                        # Ingested: staging keeps only the manifest, so an older experiment would decide
                        # (audit round 2, 9 Nov 2026). Every experiment writes BonsaiData (Go is logged).
                        # Said only once every root is read, if its data is in none (an ingest
                        # destination among the roots, in any order) (audit round 3).
                        manifest_only.append(folder.name)
                        continue
                try:
                    e = reader(folder)
                except (OSError, ValueError, KeyError) as error:
                    # Never passed over unseen: it may be the experiment that decides (plan v11, fail loudly)
                    if unread is not None:
                        unread.append(f"{folder.name} (Unreadable: {type(error).__name__})")
                    continue
                if e is not None:
                    found[folder.name] = e
    if unread is not None:
        unread.extend(f"{name} (Only Its Manifest Is Left)" for name in dict.fromkeys(manifest_only) if name not in found)
    return sorted(found.values(), key=lambda e: e.when, reverse=True)


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
        return stage, (f"Optogenetics Experiment ({last.name}: {_rate(on):.0%} of {len(on)} laser-on and {_rate(off):.0%} of "
                       f"{len(off)} laser-off trials rewarded): Stage {stage} Again")
    # Trials of the experiment's last stage only (a stage changed mid-experiment uses the last)
    rewarded = [r for r, s in zip(last.rewarded, last.stage) if s == stage]
    total = len(last.rewarded)
    # Over every trial of the experiment, as Tom's: rewarded at the last stage / all trials
    rate = sum(rewarded) / total
    where = f"{last.name}: {total} trials, {rate:.0%} rewarded at stage {stage}"
    if total < MIN_TRIALS:
        return stage, f"Under {MIN_TRIALS} Trials ({where}): Stage {stage} Again"
    if stage == TWO_IN_A_ROW_STAGE:
        s2, n = TWO_IN_A_ROW_STAGE, TWO_IN_A_ROW_REWARDED
        before = [r for r, s, l in zip(same[1].rewarded, same[1].stage, _laser(same[1])) if s == s2 and not l] if len(same) > 1 else []
        if sum(rewarded) >= n and sum(before) >= n:
            return s2 + 1, f"{n}+ Rewarded in Two Experiments in a Row ({where}): Stage {s2 + 1}"
        if rate < REGRESS_RATE:
            return s2 - 1, f"Under {REGRESS_RATE:.0%} Rewarded ({where}): Back to Stage {s2 - 1}"
        return s2, f"Stage {s2} Needs {n}+ Rewarded in Two Experiments in a Row ({where}, before {sum(before)}): Stage {s2} Again"
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
    parser.add_argument("--eartag", default="", help="also search sub-<eartag>: runs before 13 Nov 2026 were filed under it")
    parser.add_argument("--modality", required=True, choices=["auditory", "visual", "audiovisual"])
    parser.add_argument("--root", action="append", type=Path, help="where this workflow's experiments are (repeatable)")
    parser.add_argument("--legacy-root", action="append", type=Path, help="where the old workflow's Logs are (repeatable)")
    args = parser.parse_args()
    unread: list[str] = []
    history = experiments(args.subject, args.root or ROOTS, args.legacy_root or LEGACY_ROOTS, unread, args.eartag)
    if unread:
        # The mouse's history may be there alone (a rig keeps its copy 72 h): no guess (audit, 9 Nov 2026,
        # a trained mouse was offered Stage 1).
        print(json.dumps({"value": None, "why": f"Could Not Read {' and '.join(unread)}: Choose the Stage by Hand"}))
        return
    stage, why = suggest(history, args.modality)
    print(json.dumps({"value": stage, "why": why}))


if __name__ == "__main__":
    main()
