# coen-dome-navigation

Tom Childs' auditory (and visual) VR corridor task on the lab's open-rigs platform, run from rig-control. A head-fixed mouse runs on a wheel through a virtual track while a tone sweeps up in frequency (and a grating's spatial frequency changes) with its position; it licks in the reward zone for a reward. The stage is the Training Stage setting, a row of the task file's `trainingStages` from 0: Tom's shaping stages 1 to 6 are rows 0 to 5, each saying what its stage does (see `src\ucl_open_coen_dome_navigation\task.py`).

Converted on 9 Nov 2026 from `ucl-open/auditory-vr-corridor` (`main` b67a1f6 with `zapit-integration` 1782543), which stays as it is: Tom's original, read only. Rebuilt on 13 Nov 2026 (plan v11) on the lab fork's shared dome blocks, with the task in tested C# and every task wait on the Harp clock.

## Running it

From rig-control: choose the rig and the mouse (launched by its lab ID, such as `TC001`), then this workflow and `config\task-abstract-av-corridor.yml`. The form shows the task's Modality and Training Stage; the stage is suggested from the mouse's earlier experiments of that modality (`uv run suggest-stage --subject <lab ID> --modality auditory`, Tom's rules from `gen_session.py`, reading this workflow's data on the rig, in staging and in Live-Data, and the old TrialLogs). Launch puts the rig in ready mode (hardware armed, cameras on, spouts homed, nothing recorded); Go reads the task file and starts the trials once the experiment folder exists.

What changed from Tom's workflow, and why, is in `BACKLOG.md` (Decided). The Zapit optogenetics port is in `ZAPIT-V2-CHANGES.md`; the rig checks before the first optogenetics experiment are in `docs\zapit-rig-test.md`.

## Layout

| Path | What |
|---|---|
| `src\main.bonsai` | The workflow, top level in data-flow order: RigSetup, Initialization, Declarations, Ready, Task, Zapit, Commands, Grating, Audio, StartTrials, TaskLogic, Logging, LiveView |
| `src\Extensions\Corridor.cs` | The task: each trial's draw (`TrialDraw`), its interval, quiet wait and run along the virtual track (`VirtualTrack`, `CorridorState`) on the Harp clock, its outcome and row (`CreateTrialRow`), all tested in `tests\Extensions.Tests` |
| `config\task-abstract-av-corridor.yml` | The task file rig-control copies at Go with the form's edits |
| `config\rig-template.yml` | The dome rig model's template (`open_rigs.rigs.dome`): every dome workflow reads the same `C:\Coen-Lab-Local\RigConfigs\<MACHINE>\rig.yml` |
| `schemas\trial-columns.yml` | What each `BonsaiData_Trial` column means |
| `src\waveforms` | The tone pips, reward tone, error tone and background noise, each at the loudness it plays at, and `sweep.json`, the sweep they were made for; made by `uv run generate-waveforms` and committed |
| `src\ucl_open_coen_dome_navigation` | Rig and task models (`regenerate-schemas` makes `src\DataSchemas` and the C#), waveforms, stage suggestion |
| `local_packages` | The lab's OpenRigs packages from `Coen-Lab/lib-open-rigs`; never edited here |

## Shared Blocks

The dome's hardware, logging and reward come from the lab fork (lib-open-rigs; what each takes and gives: [Dome Building Blocks](https://github.com/Coen-Lab/lib-open-rigs/blob/main/docs/articles/dome-building-blocks.md)). `tests\test_workflow.py` fails when the workflow uses a block this table does not list.

| Block | Its job in this task | File |
|---|---|---|
| `ReadRigConfig`, `ResolveRigConfigDirectory` | read this machine's rig.yml | `OpenRigs.Core:ReadRigConfig.bonsai` |
| `DomeHardware` | every device, the Harp clock (`Timestamp`), the wheel's distance (`WheelDistanceCm`), the left lick detector (`LickStateLeft`), the ZapitTrigger line, the spout stage and its commands | `OpenRigs.Devices:DomeHardware.bonsai` |
| `DomeDisplay` | the dome's window, where the grating draws | `OpenRigs.Vision:DomeDisplay.bonsai` |
| `DeliverReward` | each reward on the left spout, planned from the valve's calibration; `MissingCalibration` is a launch property | `OpenRigs.Devices:BehaviorBoard.DeliverReward.bonsai` |
| `DomeHarpLogs` | the Harp boards' logs | `OpenRigs.Devices:DomeHarpLogs.bonsai` |
| `HarpDelay` | the trigger line cleared `triggerHoldSeconds` after the outcome, on the Harp clock | `OpenRigs.Core` |
| `StartupWatchdog` | Ready once the clock is set and the spouts are homed, or a stop naming what never came | `OpenRigs.Core` |
| `ThrowError` | stops the workflow on a Zapit error | `OpenRigs.Core` |
| `ZapitClient`, `CreateZapitQuery`, `CheckZapitConditionCount`, `ZapitConditionsSnapshot` | Zapit: commands and replies, its loaded conditions checked against the task's file, the file copied into the experiment | `OpenRigs.Devices` (`OpenRigs.Devices.Zapit`) |

## Checks

```
uv run pytest
dotnet test tests\Extensions.Tests
powershell -NoProfile -File scripts\check-workflow.ps1
```

`check-workflow.ps1` needs `.bonsai\Setup.ps1` run once (Bonsai and its packages).

## Data

Each experiment is `C:\Coen-Lab-Local\Acquired-Data\sub-<lab ID>\ses-<session>_<UTC>`: the Harp boards' raw logs, the cameras, `BonsaiData\BonsaiData_Trial_*.csv` (one row per trial, columns in `schemas\trial-columns.yml`), `BonsaiData\BonsaiData_Async_*.csv` (rewards, commands, Go), `BonsaiData\BonsaiData_StimulusState_*.csv` (one row per 50 ms pip while a trial runs: `Seconds` (Harp time), `trialNumber`, `positionCm`, `toneFrequencyHz`, `stimulusActive` (tone and grating on), `visualSpatialFrequencyCpd`), `BonsaiData\BonsaiData_ZapitReplies_*.csv` (each Zapit reply) when optogenetics is on, the settings files, each calibrated valve's file (`ValveCalibrationDO<n>`) and `manifest.json`.
