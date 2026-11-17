# coen-dome-navigation

Tom Childs' auditory (and visual) VR corridor task on the lab's open-rigs platform, run from rig-control. A head-fixed mouse runs on a wheel through a virtual track while a tone sweeps up in frequency (and a grating's spatial frequency changes) with its position; it licks in the reward zone for a reward. The stage is the Training Stage setting, a row of the task file's `trainingStages` from 0, each saying what its stage does (see `src\ucl_open_coen_dome_navigation\task.py`).

Converted on 9 Nov 2026 from `ucl-open/auditory-vr-corridor` (`main` b67a1f6 with `zapit-integration` 1782543), which stays as it is: Tom's original, read only. Rebuilt on 13 Nov 2026 on the lab fork's shared dome blocks, with the task in tested C# and every task wait on the Harp clock.

## Running an experiment

From rig-control: choose the rig and the mouse (launched by its lab ID, such as `TC001`), then this workflow and `config\task-abstract-corridor.yml`. The form shows the Training Stage, suggested from the mouse's earlier experiments of this workflow (`uv run suggest-stage --subject <lab ID>`, Tom's rules from `gen_session.py`, reading this workflow's data on the rig, in staging and in Live-Data; the old TrialLogs are not read). What the trials show is the task file's `trialWeights: { visual, auditory, audiovisualCoherent, audiovisualConflict, neutral }` (coen-dome-decision-making's names), relative chances set per mouse on Setup and carried from its last experiment, so they survive a stage change; each trial draws the grating, the tone sweep or both, logged as `trialType`. A `trainingStages` row may set its own `trialWeights`, replacing the mouse's for that stage (none does). `audiovisualConflict` and `neutral` must be 0 (no such trial here yet), and all 0 is refused (no session-wide modality). Launch puts the rig in ready mode (hardware armed, cameras on, spouts homed, the background noise on from Ready at the launched task file's level, nothing recorded); Go reads the task file and starts the trials once the experiment folder exists.

What changed from Tom's workflow, and why, is in `BACKLOG.md` (Decided); the rig checks before the first optogenetics experiment are in `docs\zapit-rig-test.md`.

## Optogenetics

Zapit (from Tom's `zapit-integration-v2`, 9 Nov 2026), on with the task file's `optogenetics.enabled`:

- Every stimulus is continuous (no `StimDuration` or `StartDelay` is sent): Zapit ignores StopStimulus for a timed one. The laser runs from the trigger (`laserToStimulusSeconds` before the first pip, signed: after it if negative) until Bonsai stops it at the outcome, or at `laserMaxSeconds` on the Harp clock (a wall-clock backstop should the board stall).
- Every trial sends Zapit a condition drawn at random, laser-off trials at 0 mW, so galvo noise and masking light are the same on laser-on and laser-off trials; the first tone pip comes `laserToStimulusSeconds` after step 0 on every trial of the experiment.
- Timing: each trial's interval runs from the last outcome (after any timeout; trial 1 from the first clock tick). The trial's start (step 0) is the interval's end less the lead, max(`laserToStimulusSeconds`, 0), or later until the stage's quiet waits hold (`lickQuietSeconds` with the spout released, a held contact holding the start; the wheel's travel, forward and back added, within `wheelQuietCm` over the last `wheelQuietSeconds`; each counted from the interval's start; `quietWaitSeconds` in the trial row says what they added). The first pip comes the lead after the trial start in every experiment, optogenetics on or off, so with a quiet mouse it lands exactly `itiSeconds` after the outcome. On a hardware-triggered rig Zapit is armed during the interval, once the last trial's trigger line has fallen, so it adds no time; the trigger must find it armed with the trial's condition and laser state, and a late reply, any other answer or any Zapit error reply stops the workflow. A trial that ends before its trigger has no laser and logs `laserPowerMw` 0.
- At connect Zapit's loaded condition count must equal the task's conditions file's, and each condition's `offRampDownDuration_ms` must be 100 to 500 ms.
- A rig without hardware triggering (rig.yml `zapit.hardwareTriggered`) runs optogenetics with the `test` mouse only: software start is tens of ms late, varies and has no onset in the Harp log.
- In an optogenetics experiment only, the trigger line (and the masking LEDs wired to it) rises at every trial's trigger (`laserToStimulusSeconds` before the first pip) and falls `triggerHoldSeconds` after the outcome; without optogenetics it stays low.
- Each trial row's `laserTriggerTime` is the trigger's Harp time, NaN without optogenetics or on a trial answered before its trigger, so a laser-off trial (a time, `laserPowerMw` 0) and one answered first (NaN) differ.
- `suggest-stage` keeps an optogenetics experiment's stage, and its laser-on trials never count toward a decision.

## Layout

| Path | What |
|---|---|
| `src\main.bonsai` | The workflow, top level in data-flow order: ReadLabRig, Initialization (ExperimentControl, LaunchSettings, the training stage), TaskFile, Declarations, ExperimentLogs, Hardware, Display, Audio, BackgroundNoise, ConnectForTask, Logging, Status, Rewards, RewardValve-Left, RewardValve-Right, TaskLogic, ExperimentRecords, LiveView; launch properties SubjectId, SessionId, LaunchId, TaskPath, DisplayDevice and ConfigRoot |
| `src\Extensions` | The task in tested C# (`tests\Extensions.Tests`): each trial's draw, timing and run along the virtual track, the rules lib-open-rigs' `TrialLoop` runs on the Harp clock (`TrialLogic.cs`), its row (`TrialRow.cs`) and the tone sweep (`Sweep.cs`); its README lists each file's classes |
| `config\task-abstract-corridor.yml` | The task file rig-control copies at launch (read at Start for the background noise) and again at Go with the form's edits |
| `schemas\trial-columns.yml` | What each `WorkflowData_Trials` column means |
| `src\sounds` | The tone pips and reward tone (float32 `.bin`), each one channel at the loudness it plays at (the penalty sound is the lab's, from `OpenRigs.Audio`; the background noise the lab's one file, rig.yml's `speakerArray.backgroundNoise`), and `sweep.json`, the sweep they were made for and their rate (48 kHz), which must be rig.yml's `speakerArray.device.sampleRate` (the workflow stops at Start, naming both, if not); made by `uv run generate-waveforms` and committed |
| `src\ucl_open_coen_dome_navigation` | The task model (`regenerate-schemas` makes `src\DataSchemas` and its C#; the rig model is lib-open-rigs' `open_rigs.rigs.lab_rig`, its C# OpenRigs.Devices' and its template open-rigs' `rigs\rig-template.yml`), waveforms, stage suggestion |
| `local_packages` | The lab's OpenRigs packages from `Coen-Lab/lib-open-rigs`; never edited here |

## Shared Blocks

The dome's hardware, logging and reward come from the lab fork (lib-open-rigs; what each takes and gives: [Building Blocks](https://github.com/Coen-Lab/lib-open-rigs/blob/main/docs/articles/building-blocks.md)). `tests\test_workflow.py` fails when the workflow uses a block this table does not list.

| Block | Job in this task | File |
|---|---|---|
| `ReadLabRig` | this machine's rig.yml read once into the lab rig model (`RigConfigs`, `RigFile`, `ConfigRoot`), first | `OpenRigs.Devices:ReadLabRig.bonsai` |
| `DomeHardware` | every dome device as a piece: the Harp clock (`Timestamp`), the wheel's distance (`WheelDistanceCm`), the left lick detector (`LickStateLeft`), the ZapitTrigger line, the lick spout stage (`LickSpoutStage`) and its commands, the three cameras, triggered once all three acquire, each logging itself; and Ready once the clock is set, the spouts are homed, each camera has sent a frame and the mixer has started (`PieceStages`; a stop naming what never came after 180 s) | `OpenRigs.Devices:DomeHardware.bonsai` |
| `ProjectorDisplay` | the dome's window, where the grating draws | `OpenRigs.Vision:ProjectorDisplay.bonsai` |
| `Rewards` | before the valves, `TaskLogic` and `ExperimentRecords`: declares `RewardRequests`, which the task sends each reward into (`left`, the stage's `rewardSizeUl`), refuses a reward for a valve no piece fires and logs each delivery (`RewardDelivered`) | `OpenRigs.Devices:Rewards.bonsai` |
| `RewardValve` | `RewardValve-Left` and `RewardValve-Right`, on `BehaviorBoard-Main` (rig.yml's wiring `name: left` and `name: right`): each loads its valve's calibration at Start, plans the rewards asked of it and fires them as calibrated pulses; a reward over `MaxRewardUl` (20 µl) stops the workflow, and so does a valve with no calibration, for every mouse; its manual drop `RewardValve-Left.Reward` or `RewardValve-Right.Reward`, 2.5 µl before and during a run | `OpenRigs.Devices:RewardValve.bonsai` |
| `ExperimentRecords` | the experiment's records, last but the live view: `LogControlEvents`, `RecordHarpDevices` and `ExperimentManifest` below, with this task's `IngestScript` and `ExpectedFiles` | `OpenRigs.Logging:ExperimentRecords.bonsai` |
| `TrialLoop`, `PickTrialEvents`, `ZapitTrialCommand` | the trials on the Harp clock, the same state machine as coen-dome-decision-making's: each trial's interval, Zapit's arming, the quiet waits, the trial start, the trigger and its check, the stimulus, the response window (the stage's `responseWindowSeconds` from the first pip), the outcome and its timeout, the line's fall (`TriggerOff`, `triggerHoldSeconds` after the outcome) and the laser cap, run by `RunTrials` with this task's `TrialRules`; `PickTrialEvents` picks each kind from `TrialEvents`; `ZapitTrialCommand` makes each trial's StartStimulus (sent at the Arm event on a hardware-triggered rig, rig.yml's `zapit.hardwareTriggered`, and at the trigger otherwise) | `OpenRigs.Trials` |
| `OnNextTick` | the first trial started from the Harp clock's thread, with no lock held | `OpenRigs.Core` |
| `ExperimentControl`, `ReportMetric` | the launch's `LaunchId` (in the manifest), parameters, commands and Go for rig-control (`SpoutStepSize`, the spout commands, each valve's manual drop are declared by their pieces, DomeHardware's `LickSpoutStage` and each `RewardValve`, never here); the status metrics `trialNumber`, `trialType` (as coen-dome-decision-making reports it), `rewardOfferedUl` and `laserCondition` | `OpenRigs.Control` (`ExperimentControl.bonsai`) |
| `LaunchSettings` | the launch's `SubjectId` and task file (`TaskPath`) from Start, and `ExperimentIds` at Go (ExperimentControl refuses a Go with a blank subject or session) | `OpenRigs.Control:LaunchSettings.bonsai` |
| `ExperimentLogs`, `LogControlEvents` | the experiment folder at Go, `WorkflowData_Events` with every controller event (`LogControlEvents`, in `ExperimentRecords`), `Settings_Rig`, `Settings_Experiment` and the Recording phase | `OpenRigs.Logging` |
| `LogWorkflowData` | the task's own file, `WorkflowData_StimulusState`, one for the experiment | `OpenRigs.Logging:LogWorkflowData.bonsai` |
| `TrainingStage` | the parameter `TrainingStage` at Go as `TrainingStage`, reported as the metric `trainingStage` | `OpenRigs.Control:TrainingStage.bonsai` |
| `PlayPenaltySound` | the lab's penalty sound at `penalty.sound.levelDbfs`, made at Go, played on each penalty | `OpenRigs.Audio:PlayPenaltySound.bonsai` |
| `SetZapitTriggerLine` | the ZapitTrigger line raised at the laser trigger and cleared at the laser's end | `OpenRigs.Devices:SetZapitTriggerLine.bonsai` |
| `LogRngSeed` | the `RngSeed` row of `WorkflowData_Events`, once, at trial 1's start | `OpenRigs.Logging:LogRngSeed.bonsai` |
| `TaskLogs` | `Settings_Task` (the task file as JSON), `WorkflowData_Trials` (`LogTrials`) and Zapit's logs (`ZapitLogs`: each reply a `ZapitReply` row of `WorkflowData_Events`, and `Settings_ZapitConditions`) | `OpenRigs.Logging:TaskLogs.bonsai` |
| `ReadTextFile` | the task file read at Start (`LaunchTaskConfig`, the background noise's) and at Go (`TaskConfig`) | `OpenRigs.Core` |
| `BackgroundNoise` | the lab's background noise, rig.yml's `speakerArray.backgroundNoise` (`..\shared\sounds\background_noise.wav`: the server's `config\shared` copy, kept on the rig by app-rig-control), read at Start at the mixer's rate (`ReadBackgroundNoise`), unequalised on every speaker at the launched task file's `backgroundNoise.levelDbfs` from Ready; Go stops if the task file changed `backgroundNoise` | `OpenRigs.Audio:BackgroundNoise.bonsai` |
| `RecordHarpDevices` | in `ExperimentRecords`: each Harp board checked at Start and its device record and register map written into the experiment | `OpenRigs.HarpDevices` |
| `ExperimentManifest` | in `ExperimentRecords`: `manifest.json`: what ran, the config files by hash, what the experiment must leave | `OpenRigs.Manifest` |
| `StreamDevices` | the live view (the last node) | `OpenRigs.LiveView` |
| `ConnectForTask`, `ZapitLogs`, `StopZapit` | Zapit's commands and replies (`ZapitCommands`, `ZapitReplies`, which its ConnectZapit declares) once the task uses it, each trial's laser stopped at its outcome or cap (`StopZapit`), its replies and conditions file logged (`ZapitLogs`), and the stops at start: an error reply, no conditions file loaded, a conditions count other than the task's file, a real mouse on a rig without hardware triggering | `OpenRigs.Devices:ConnectForTask.bonsai` |
| `ZapitConditionsSnapshot`, `ZapitTrial.ReplyMarginSeconds`, `ZapitTrial.CheckArmed` | the conditions file copied into the experiment, the 0.2 s Zapit reply margin the task's checks use, the check of Zapit's armed reply (`TrialLoop`'s) | `OpenRigs.Devices` (`OpenRigs.Devices.Zapit`) |
| `ZapitTrial.Draw`, `ZapitTrial.Command`, `ZapitTrial.LeadProblem`, `ZapitTrial.NotArmedByTrigger`, `ZapitConditions.Check` | in `TrialLogic.cs`, `TrialLoop` and TaskLogic: each trial's condition and laser draw, its StartStimulus, the lead's check against the shortest interval, the stop when Zapit is not armed by the trigger, the conditions file's count and ramp-downs | `OpenRigs.Devices` (`OpenRigs.Devices.Zapit`) |
| `QuietWait`, `RngSeed` | in `TrialLoop` and `TrialLogic.cs`: the stage's quiet waits before the trial start (both spouts', a lick on either holds the start; each stage checked at Go by `QuietWait.SettingsProblems`), and the experiment's seed (the task file's `rngSeed`, or one drawn when it is null) | `OpenRigs.Core` |
| `SpeakerMixer` | rig.yml's speakers and their filters, loaded once at Start (`Speakers`), stopping the workflow naming any speaker without a usable filter; the mixer, started no sooner than 3 s after Start; the pips, reward tone, penalty sound and background noise played over each other, the background noise from Ready, faded in over 2 s | `OpenRigs.Audio:SpeakerMixer.bonsai` |
| `SpeakerBuffer` | each sound laid out on rig.yml's `channelCount` channels at the task file's level in dBFS RMS (`auditoryStimulus`, `rewardTone`, `backgroundNoise`, `penalty.sound` `levelDbfs`), checked to peak under -1 dBFS after equalisation (the background noise at Start, at the launched task file's level; the others at Go): the pips and reward tone equalised on every rig.yml speaker, the background noise as it is on every speaker, the penalty sound the lab's, on `penaltySoundChannel` alone | `OpenRigs.Audio` |

## Setup

`scripts\deploy.ps1` makes the Python environment (`uv sync --locked`); Bonsai and its packages come from `.bonsai\Setup.ps1`, which app-rig-setup runs when packages are missing.

## Checks

```
uv run pytest
dotnet test tests\Extensions.Tests
powershell -NoProfile -File scripts\check-workflow.ps1
```

`check-workflow.ps1` needs `.bonsai\Setup.ps1` run once (Bonsai and its packages).

## Data

Each experiment is `C:\Coen-Lab-Local\Acquired-Data\sub-<lab ID>\ses-<session>_<UTC>`: the Harp boards' raw logs, the cameras (each `Camera-Face`, `Camera-Pupil` and `Camera-Body` folder holds an hourly `.mkv` (H.264) and a `.csv` of one row per frame: `harpTime`, `frameId`, `cameraClockNs`, `exposureUs`, `gainDb`; the encoder is `videoEncoder` in rig.yml), `WorkflowData\WorkflowData_Trials_*.csv` (one row per trial, columns in `schemas\trial-columns.yml`; `trialType` what the trial showed, `visual`, `auditory` or `audiovisualCoherent`), `WorkflowData\WorkflowData_Events_*.csv` (rewards, commands, Go, and each Zapit reply as a `ZapitReply` row when optogenetics is on), `WorkflowData\WorkflowData_StimulusState_*.csv` (one row per 50 ms pip while a trial runs: `harpTime`, `trialNumber`, `positionCm`, `auditoryFrequencyHz`, `stimulusActive` (True while the tone and grating are on, False past a truncated trial's fraction), `visualSpatialFrequencyCpd`), each one file for the experiment, the settings files, each valve's calibration under `RewardValve-Left\` and `RewardValve-Right\` and `manifest.json`.

`uv run summarise-experiment <experiment folder>` reads one experiment from these files alone (lib-open-rigs' `open_rigs.rigs.trials`, tested there, the same for every dome task): how the run ended (the manifest), trials, performance, each response time and outcome-to-next-stimulus interval, and the water the task asked for against the water delivered. It also checks the rows against each other and names each problem, exiting 1: trial numbers with a gap, an outcome not `Correct`, `Incorrect` or `NoResponse`, a trial start or a stimulus more than 2 ms from where the task file puts it (`itiStartTime + itiSeconds + quietWaitSeconds`), a laser trigger more than 2 ms from `laserToStimulusSeconds` before the stimulus, an interval not starting at the last outcome plus its `penaltyTimeoutSeconds`, a `penalised` trial without the task's timeout and, when the task has it on, the penalty sound (or an unpenalised one with either), a `rewarded` trial with no `RewardDelivered` at its outcome requesting its `rewardOfferedUl`, or a `RewardDelivered` at the outcome of a trial not rewarded. A trial cut short by Stop has no trial row; it is said as unfinished, its outcome-to-next-stimulus interval taken from `WorkflowData_StimulusState`, and never counted as a problem.
