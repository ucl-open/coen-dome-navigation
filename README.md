# coen-dome-navigation

Tom Childs' auditory (and visual) VR corridor task on the lab's open-rigs platform, run from rig-control. A head-fixed mouse runs on a wheel through a virtual track while a tone sweeps up in frequency (and a grating's spatial frequency changes) with its position; it licks in the reward zone for a reward. The stage is the Training Stage setting, a row of the task file's `trainingStages` from 0: Tom's shaping stages 1 to 6 are rows 0 to 5, each saying what its stage does (see `src\ucl_open_coen_dome_navigation\task.py`).

Converted on 9 Nov 2026 from `ucl-open/auditory-vr-corridor` (`main` b67a1f6 with `zapit-integration` 1782543), which stays as it is: Tom's original, read only. Rebuilt on 13 Nov 2026 (plan v11) on the lab fork's shared dome blocks, with the task in tested C# and every task wait on the Harp clock.

## Running it

From rig-control: choose the rig and the mouse (launched by its lab ID, such as `TC001`), then this workflow and `config\task-abstract-av-corridor.yml`. The form shows the task's Modality and Training Stage; the stage is suggested from the mouse's earlier experiments of that modality (`uv run suggest-stage --subject <lab ID> --modality auditory`, Tom's rules from `gen_session.py`, reading this workflow's data on the rig, in staging and in Live-Data, and the old TrialLogs). Launch puts the rig in ready mode (hardware armed, cameras on, spouts homed, nothing recorded); Go reads the task file and starts the trials once the experiment folder exists.

What changed from Tom's workflow, and why, is in `BACKLOG.md` (Decided); the rig checks before the first optogenetics experiment are in `docs\zapit-rig-test.md`.

## Optogenetics

Zapit (from Tom's `zapit-integration-v2`, 9 Nov 2026), on with the task file's `optogenetics.useZapit`:

- Every stimulus is continuous (no `StimDuration` or `StartDelay` is sent): Zapit ignores StopStimulus for a timed one. The laser runs from the trigger (`laserToStimulusSeconds` before the first pip, signed: after it if negative) until Bonsai stops it at the outcome, or at `laserMaxSeconds` on the Harp clock (a wall-clock backstop should the board stall).
- Every trial sends Zapit a condition drawn at random, laser-off trials at 0 mW, so galvo noise and masking light are the same on laser-on and laser-off trials; the first tone pip comes `laserToStimulusSeconds` after beep 0 on every trial of the experiment.
- Timing (Pip, 13 Nov 2026): each trial's interval runs from the last outcome (after any timeout; trial 1 from the first clock tick). Go, the trial's start (beep 0), is the interval's end less the lead, max(`laserToStimulusSeconds`, 0), or later until the stage's quiet waits hold (`lickQuietSeconds` with the spout released, a held contact holding Go; the wheel's travel, forward and back added, within `wheelQuietCm` over the last `wheelQuietSeconds`; each counted from the interval's start; `quietWaitSeconds` in the trial row says what they added). The first pip comes the lead after Go in every experiment, optogenetics on or off, so with a quiet mouse it lands exactly `itiSeconds` after the outcome. On a hardware-triggered rig Zapit is armed during the interval, once the last trial's trigger line has fallen, so it adds no time; the trigger must find it armed with the trial's condition and laser state, and a late reply, any other answer or any Zapit error reply stops the workflow. A trial that ends before its trigger has no laser and logs `laserPowerMw` 0.
- At connect Zapit's loaded condition count must equal the task's conditions file's, and each condition's `offRampDownDuration_ms` must be 100 to 500 ms.
- A rig without hardware triggering (rig.yml `zapit.hardwareTriggered`) runs optogenetics with the `test` mouse only: software start is tens of ms late, varies and has no onset in the Harp log.
- In an optogenetics experiment only, the trigger line (and the masking LEDs wired to it) rises at every trial's start and falls `triggerHoldSeconds` after the outcome; without optogenetics it stays low (Pip, 13 Nov 2026).
- `suggest-stage` keeps an optogenetics experiment's stage, and its laser-on trials never count toward a decision.

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
| `HarpDelay` | the trigger line cleared `triggerHoldSeconds` after the outcome, on the Harp clock, in an optogenetics experiment | `OpenRigs.Core` |
| `ExperimentControl`, `ExperimentSettings`, `OnCommand`, `OnParameterSet`, `ParameterDefault`, `ParameterValue`, `ReportMetric`, `ReportPhase` | parameters, commands, Go and phases for rig-control; the settings files; the metrics the view shows | `OpenRigs.Control` (`ExperimentControl.bonsai`) |
| `LogController`, `LogAsync`, `LogTrial`, `LogData`, `LogVideo`, `LogDataSchema`, `CreateAsyncEvent` | the experiment folder and its files: trial rows, async events (Go, commands, `RngSeed`), stimulus state, Zapit replies, video, settings | `OpenRigs.Logging` |
| `RecordHarpDevices` | each Harp board checked at Start and its device record and register map written into the experiment | `OpenRigs.HarpDevices` |
| `ExperimentManifest` | `manifest.json`: what ran, the config files by hash, what the experiment must leave | `OpenRigs.Manifest` |
| `StreamDevices` | the live view (the last node) | `OpenRigs.LiveView` |
| `StartupWatchdog` | Ready once the clock is set and the spouts are homed, or a stop naming what never came | `OpenRigs.Core` |
| `ThrowError` | stops the workflow on a Zapit error | `OpenRigs.Core` |
| `ZapitClient`, `CreateZapitQuery`, `CheckZapitConditionCount`, `ZapitConditionsSnapshot`, `ZapitLineMessage` | Zapit: commands and replies, the trigger line's set and clear, its loaded conditions checked against the task's file, the file copied into the experiment | `OpenRigs.Devices` (`OpenRigs.Devices.Zapit`) |

## Checks

```
uv run pytest
dotnet test tests\Extensions.Tests
powershell -NoProfile -File scripts\check-workflow.ps1
```

`check-workflow.ps1` needs `.bonsai\Setup.ps1` run once (Bonsai and its packages).

## Data

Each experiment is `C:\Coen-Lab-Local\Acquired-Data\sub-<lab ID>\ses-<session>_<UTC>`: the Harp boards' raw logs, the cameras, `BonsaiData\BonsaiData_Trial_*.csv` (one row per trial, columns in `schemas\trial-columns.yml`), `BonsaiData\BonsaiData_Async_*.csv` (rewards, commands, Go), `BonsaiData\BonsaiData_StimulusState_*.csv` (one row per 50 ms pip while a trial runs: `Seconds` (Harp time), `trialNumber`, `positionCm`, `toneFrequencyHz`, `stimulusActive` (tone and grating on), `visualSpatialFrequencyCpd`), `BonsaiData\BonsaiData_ZapitReplies_*.csv` (each Zapit reply) when optogenetics is on, the settings files, each calibrated valve's file (`ValveCalibrationDO<n>`) and `manifest.json`.
