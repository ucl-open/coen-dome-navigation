# coen-dome-navigation

Tom Childs' auditory (and visual) VR corridor task on the lab's open-rigs platform, run from rig-control. A head-fixed mouse runs on a wheel through a virtual track while a tone sweeps up in frequency (and a grating's spatial frequency changes) with its position; it licks in the reward zone for a reward. The stage is the Training Stage setting, a row of the task file's `trainingStages` from 0: Tom's shaping stages 1 to 6 are rows 0 to 5, each saying what its stage does (see `src\ucl_open_coen_dome_navigation\task.py`).

Converted on 9 Nov 2026 from `ucl-open/auditory-vr-corridor` (`main` b67a1f6 with `zapit-integration` 1782543), which stays as it is: Tom's original, read only. The rig, control, commands and logging are av-dome's `pip/main-switchover` (6c5a885), which runs the same domes and BLUE-OPTO rigs; the task is Tom's, its groups kept as they were.

## Running it

From rig-control: choose the rig and the mouse, then this workflow and `config\task-abstract-av-corridor.yml`. The form shows the task's Modality and Shaping Stage; the stage is suggested from the mouse's earlier experiments of that modality (`uv run suggest-stage --subject <ID> --modality A`, Tom's rules from `gen_session.py`, reading both this workflow's data and the old TrialLogs). Launch puts the rig in ready mode (hardware armed, cameras on, nothing recorded); Go reads the task file and starts the trials once the experiment folder exists.

What changed from Tom's workflow, and why, is in `BACKLOG.md` (Decided). The Zapit optogenetics port (branch `zapit-v2`) is in `ZAPIT-V2-CHANGES.md`; the rig checks before the first optogenetics experiment are in `docs\zapit-rig-test.md`.

## Layout

| Path | What |
|---|---|
| `src\main.bonsai` | The workflow. Top level in data-flow order: RigSetup, Declarations, Initialization, Task, Commands, Wheel (with ComputeRewardZone), LickCounter (with TaskState), HarpClock, TrialEventTimes, Audio (with TaskLogic), StartTrials, VirtualPosition, Grating, TrialRow, Logging, LiveView |
| `config\task-abstract-av-corridor.yml` | The task file rig-control copies at Go with the form's edits |
| `config\rig-template.yml` | av-dome's rig template: both workflows read the same `C:\Coen-Lab-Local\RigConfigs\<MACHINE>\rig.yml` |
| `schemas\trial-columns.yml` | What each `BonsaiData_Trial` column means |
| `src\waveforms` | The tone pips, error tone and background noise, made by `uv run generate-waveforms` and committed |
| `src\ucl_open_coen_dome_navigation` | Rig and task models (`regenerate-schemas` makes `src\DataSchemas` and the C#), waveforms, stage suggestion |
| `local_packages` | The lab's OpenRigs packages from `Coen-Lab/lib-open-rigs`; never edited here |

## Checks

```
uv run pytest
powershell -NoProfile -File C:\Github\Coen-Lab\lib-open-rigs\scripts\check-workflow-builds.ps1 -Repo .
```

The second needs `.bonsai\Setup.ps1` run once (Bonsai and its packages).

## Data

Each experiment is `C:\Coen-Lab-Local\Acquired-Data\sub-<mouse>\ses-<session>_<UTC>`: the Harp boards' raw logs, the cameras, `BonsaiData\BonsaiData_Trial_*.csv` (one row per trial, columns in `schemas\trial-columns.yml`), `BonsaiData\BonsaiData_Async_*.csv` (rewards, commands, Go), `BonsaiData\BonsaiData_StimulusState_*.csv` (one row per 50 ms beep while a trial is active: `Seconds` (Harp time), `trialNumber`, `positionCm`, `audioFrequencyHz`, `stimulusActive` (tone and grating on), `visualSpatialFrequencyCpd`), `BonsaiData\BonsaiData_ZapitReplies_*.csv` (each Zapit reply) when optogenetics is on, the settings files and `manifest.json`.
