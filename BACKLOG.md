# Backlog

Open work first, then what has been decided. The conversion from `ucl-open/auditory-vr-corridor` is in `git log`. The repo was `coen-av-corridor` until 9 Nov 2026 (renamed on Pip's word); the task file is `config\task-abstract-av-corridor.yml` (Pip).

## 2026-11-16 Pieces declare their own commands and parameters (lib-open-rigs def14f6; Control .13, Devices .27)

- ExperimentControl declares only `TrainingStage`: `SpoutStepSize` and the 14 spout commands are DomeHardware's `LickSpoutStage`'s, `RewardLeft` and `RewardRight` `ManualRewards'`, each declared by its piece (`DeclareControls`), and ControlServer stops at Start on a name declared twice. `tests\test_workflow.py` holds the task to `undeclared_commands` and `declared_twice`.

## 2026-11-16 Device-pieces test on BLUE-OPTO-2 (runs 001E3, 001E4; lib eccf28a, this repo 1ba43e4)

- Passed, 40 s test runs, no mouse: launch accepted, Ready 20-21 s, expected files, camera k 0 and 0 gaps, CSV closed, RewardLeft row, DO1-DO3 low at Start, Bonsai exit 1.6 s after Stop, no ffmpeg left. Camera USB serials are rig.yml's serialNumber in hex.
- Fixed on the way in lib: the projector window deadlocked the start, so an error could not end headless Bonsai (5c44e45); the Harp clock timeout counts from the first heartbeat (48c8297).
- Open: background noise not yet heard at Ready; `config\main.layout` not yet saved (both need Pip at the rig). One StepperDriver-A homing timeout in about 104 launches (001DB), likely a switch or cable.

## 2026-11-16 One background noise for every dome task (lib-open-rigs a5120a4; Audio .12, Devices .24; Pip: "It should be literally the same file")

- The background noise is rig.yml's `speakerArray.backgroundNoise`, `../shared/sounds/background_noise.wav` from the rig's folder: the server's `K:\config\shared\sounds\background_noise.wav`, which app-rig-control's upkeep copies down to `RigConfigs\shared\`. That file is this repo's `src\waveforms\background_noise.wav` byte for byte (SHA-256 762999848f9152e400e2ac7c452a729a2cf980056a59c34d44e148681b177704), so the mice hear the same noise at the same level (`backgroundNoise.levelDbfs` -64.1). The manifest hashes it with rig.yml's other files (`{RigFiles}`).
- The include takes no `FileName`; `BackgroundNoise` stops at Start, naming the file, when rig.yml names none, it is missing or not at the device's rate. This repo's copy, `waveforms.background_noise`, `write_wave`, `read_wave` and their test are deleted; `generate-waveforms` writes the pips, reward tone and `sweep.json` only.
- Open: each dome's rig.yml on the share needs the `backgroundNoise` line and the share the file before a rig runs this version (a rig on older packages refuses the key; the file goes first, the key with the new packages).

## 2026-11-16 Task-side includes (lib-open-rigs 5ea5729; Audio .11, Control .12, Core .19, Devices .22, Logging .15, Manifest .23, Video .17)

- `LaunchSettings` (OpenRigs.Control) publishes `SubjectId`, `TaskFilePath` and `RunSettings`; Initialization keeps ExperimentControl and the training stage. One inner `ExternalizedMapping` maps `SubjectId` onto ExperimentControl and LaunchSettings (ExperimentControl was subscribed once `SubjectId` came; it now takes the launch property directly).
- `SpoutStepSize`: DomeHardware's `LickSpoutStage` takes the parameter live and shows its default (150); the task's subject, `OnParameterSet`, `ParameterDefault` and the mapping onto DomeHardware are gone. The parameter's declaration stays on ExperimentControl.
- `BackgroundNoise` (OpenRigs.Audio) after Audio plays the noise and stops Go on a changed `backgroundNoise`. It reads `src\waveforms\background_noise.wav`, a 32-bit float WAV with the samples of the old `background_noise.bin` bit for bit (48 kHz, checked by `tests\test_task_file.py`), which `generate-waveforms` now writes; the `.bin` is deleted.
- `ExperimentRecords` (OpenRigs.Logging), last before LiveView, holds `DeliverReward`, `LogControlEvents`, `RecordHarpDevices` and `ExperimentManifest`, with the same `ConfigRoot`, `IngestScript` and `ExpectedFiles`.
- Nothing written changes: trial columns, `ExpectedFiles`, async events, `Settings_*` and device log names are as before (checked against the old workflow). `tests\test_workflow.py` calls `launch_settings_problems`, `spout_step_size_problems`, `background_noise_problems` and `experiment_records_problems`.

## Audit of the device-pieces cut (16 Nov 2026): not changed

- Rig lists are registered in the lint: deliberate, documented and tested; an unregistered list fails loudly.
- `PulseOutputs` and `ZapitTriggerLine` are not in `ONE_EACH`: no rig has two boards, and a collision fails the build.
- The 7-line `check-workflow` wrappers are identical, and each repo's lint test calls are thin, by design.
- The Ready-announcer rule is not run over expanded rig lists: lib's own scan covers every rig list in lib.
- No package dependency from `OpenRigs.Devices` on `OpenRigs.Audio` or `OpenRigs.Vision` for `DomeHardware`: a missing package fails the build check, the existing convention.
- `ReadyWaitsFor` is not exposed on `DomeHardware`: Ready waits for exactly what the pieces announce.
- Package version reuse drift: the push script refuses a reused version with different contents.
- `WindowState` is not exposed on `DomeHardware`; it was not before the cut either.

## 2026-11-15 Rig hardware as device pieces (lib-open-rigs, Pip: "NO BACKWARDS COMPATIBILITY")

- The hardware is lib-open-rigs' `DomeHardware` rig list (one include per device, the Ready gate, the timestamp generator's clock, the camera triggers), after this workflow's own `ReadLabRig`; `RigHardware`, `HarpLogs` and `LogCameras` are gone from lib. Ready waits for the Harp clock, the spouts homed, a first frame from each camera and the mixer (`ReadyWaitsFor`). The groups follow coen-dome-decision-making's: `ReadLabRig` first, `Initialization`, then `LogLabExperiment` on its own and `Hardware` (a piece's log waits for `PathPrefix`; Bonsai builds a subject's whole top-level node when a subscriber comes first, so `LogLabExperiment` inside `Logging`, beside `DeliverReward`'s `Timestamp`, built as "defined in terms of itself").
- Zapit is `Zapit.ConnectForTask`, the manual rewards `BehaviorBoard.ManualRewards` (`ManualRewardDropUl` gone), the shared logs `LogLabExperiment` and `LogControlEvents`; the manifest's `ConfigFiles` is lib's default `{RigFiles}` (rig.yml, every file it names, the mouse's spout positions). The background noise waits for `Ready` itself, not a copy of its stages. The quiet-wait checks are lib's (`QuietWait.SettingsProblems` at Go, open_rigs.core.task's caps in task.py).
- **Data from this change on:** every video and its CSV include the first frame (the old include skipped it), and a dropped frame is a jump in `frameId` with the triggers around it kept (frames were paired to triggers in order). The cameras' trigger starts once all three cameras acquire (it was 2 s after Start).
- **Phase 1 camera finding (recorded, past video left as is; Pip):** 7 av-dome sessions (12-13 Nov 2026, 4 rigs) had no frameId gaps, no non-Event register-92 messages, and camera clock and `harpTime` steps agreeing within 10 ms on every row, so no mid-run shift. Cameras in one session started 0 to 2 triggers apart; whether a late starter missed its first trigger or is paired one trigger late (at most 20 ms, constant) cannot be told from logs. No run of this workflow was on RDSS or K:\staging, so the first test experiment after this change is its first measurement (k = 0 expected).
- 16 Nov 2026: `{RigFiles}` also records `valve-DO1.json` and `valve-DO2.json` now, which the old hand-typed `ConfigFiles` lists did not.

## 15 Nov 2026: Pip's decisions after the audit cycles

- swc-aeon is pinned to the commit uv.lock held (`rev = "7cd561d..."`), not aeon_api's `main` branch.
- The repo is BSD 3-Clause, as lib-open-rigs (`LICENSE`, "Coen Lab, University College London", as coen-behaviour-box's; lib's names upstream's holder).
- The quiet waits have a maximum, in task.py and at Go (`CorridorSetup.Create`): `lickQuietSeconds` and `wheelQuietSeconds` 10 s (a longer wait and a licking mouse would hold Go with no message), `wheelQuietCm` 20 cm (under one turn of the 75 mm wheel, 23.6 cm: a whole turn within the wait is running, not stillness). Every stage of the task file is under them (lickQuietSeconds 0 or 1, the wheel's 0), so nothing changes for the mice.
- `sweep.json` is read at Start (`LoadSweep`, into `Sweep`), not at Go: a missing file (a wrong working folder, GREEN-DOME-2 test-0946) stops the run at Start, naming its full path.
- `sweep.json` records the rate every waveform was made at (`sampleRate`, 48000, waveforms.py's `SAMPLE_RATE`; "Other frequencies of sound may be used in future", Pip). `LoadSweep` stops the run at Start, naming both rates, when it is not rig.yml's `speakerArray.device.sampleRate` (worded as OpenRigs.Audio's `CheckWaveRate`), and the pips' `MatrixReader` takes its `SampleRate` and `BufferLength` (one 50 ms pip, `Sweep.PipSamples`) from `Sweep` instead of a typed 48000 and 2400. The waveform files are unchanged and the mapped values are the typed ones (48000, 2400), so nothing changes for the mice.
- "Go" is only the experimenter's (app-rig-control's button, `ExperimentGo`); each trial's start after its interval and quiet waits is "the trial start" in comments, descriptions, tests and READMEs, as in coen-dome-decision-making (`WaitForTrialStart`, `TrialStart`). Nothing on disk changed: trial columns, task files and schema as before. The quiet waits' schema descriptions and `QuietWait.GoTime` are lib-open-rigs'.
- `THIRD-PARTY-NOTICES.md` is coen-behaviour-box's: one section, every `local_packages\OpenRigs.*` package (the 11 in `.bonsai\Bonsai.config`) carries lib-open-rigs' BSD 3-Clause `LICENSE`, which it quotes (it named gone 0.1.0-bbox.1 and -local.3 packages and an MIT licence).

## Audit cycles 1-3 (15 Nov 2026)

- Done, details in `git log`: names end in `Seconds`; Go refuses a NaN or infinite time (`CorridorSetup.Create`); `suggest-stage` names an unreadable experiment; RigSetup reads rig.yml with lib-open-rigs' `ReadLabRig` (a test holds its `ConfigRoot`, as check-workflow does not look inside an include); the manifest hashes the three speakers' filters.

## Audit cycle 4 (15 Nov 2026)

- Deleted what nothing used: the `pydantic-settings` dependency (`pyyaml` is a dev one, for the tests), `MAX_STAGE`, `STAGE_COUNT`, the unused `gl` xmlns, the CI's `--with` packages the dev group already has, and stale BACKLOG lines. `TrialStructure` and `Truncation` are lib-open-rigs `TaskBlock`s, so the task file refuses inf and NaN in them as in every lab block (the schema is unchanged; test), and `laserMaxSeconds`' own finite check went, as `Optogenetics` already refuses it. A test runs lib-open-rigs' `fixed_width_pulses_on_reward_valves` against the lab's rig template.

## Audit cycle 5 (15 Nov 2026)

- Tests now fail when a task field is not regenerated into the C# (`Generated.cs` names every schema field) and when a `trainingStages` row is added without raising TrainingStage's `Maximum`. check-workflow sees ReadLabRig's `ConfigRoot` again (lib-open-rigs bb1b388, 4 nodes), so the test of it holds only the root's value to the lab's.

## 2026-11-15 The background noise from Ready (Pip)

- **Changed for the mice:** the background noise starts at Ready, not Go, faded in over 2 s. Ready waits for the mixer (RigHardware's `ReadyWaitsFor`: `HarpClockSet, SpoutsHomed, MixerStarted`), so the noise never reaches the mixer before it starts. Its level is the task file's as rig-control wrote it at launch, read at Start (`LaunchTaskConfig`) and checked then to peak under -1 dBFS; the stimuli and penalty sound still take theirs at Go.
- Go stops the workflow, saying so, if the form changed `backgroundNoise` (`enabled` or `levelDbfs`) after launch: the noise already plays at the launch's level. To play it at another level, launch again with a task file that has it.
- `TaskFilePath` is published at Start (it was at Go); `TaskConfig` is still read at Go.

## Audit rounds 1-3 of 3 (15 Nov 2026, stage C): items not changed

- `stage.py` reads the mouse's session summaries (jacuzzi-ingest's `sub-<lab ID>/<ses folder>.json`) from `--summaries`, with `--expected` listing the run ids that must each have a file: app-rig-control runs it with its own Python, `-I`, outside this project (`[task.suggest]`, standard library only), so it keeps no search of experiment folders and imports no `open_rigs`.
- `regenerate.py` is the same in coen-dome-navigation and coen-dome-decision-making but for the package name: it is tiny, and each repo owns its schemas.
- `.bonsai\Setup.ps1` is the same 15 lines in five task repos (coen-dome-navigation, coen-dome-decision-making, coen-behaviour-box, coen-qc-calibrate-valve, coen-qc-preflight-rig): each repo needs its own bootstrap.
- A task file that leaves out a sound's `levelDbfs` plays at lib's default (stimuli -30, penalty -30, reward tone -36, background -50), not refused: Pip chose defaults in lib (15 Nov 2026), the shipped files state every level, and older files are refused by the closed schema.
- The background-noise and penalty chains are the same XML here and in coen-dome-decision-making: the noise sources differ (.bin here, .wav there) and lib's lint forces the level mapping.
- No headless test that rig.yml's `bootDelay` above 3 s is honoured: the 3 s floor holds either way and the value arrives synchronously today.

## 2026-11-15 One speaker mixer and sound levels (lib-open-rigs OpenRigs.Audio)

- The mixer, its 3 s boot delay and the speakers (`Speakers`) are lib-open-rigs' `SpeakerMixer`, shared with coen-dome-decision-making; Ready, its 180 s `StartupWatchdog` and the cameras' start 2 s after Start are `RigHardware`'s. This repo keeps none of them.
- **Tom: every sound's level is the task file's `levelDbfs`**, in dBFS RMS of the sound as stored, before equalisation (`tonePips`, `rewardTone`, `backgroundNoise`, `penalty.sound`); `backgroundNoiseLevel` and `penalty.sound.gain` are gone. The task file states the levels played until now: pips -58.1, reward tone -55.1, background -64.1, penalty -44.6 (the lab's defaults are -30, -36, -50 and -30; the pips are quiet because the original scaled them by 0.05 twice: Tom decides on raising them). A level whose buffer would peak at -1 dBFS or more after equalisation stops the workflow at Go, naming it.
- The pips, reward tone and background noise are laid out at Go (they need the task file's levels), not at Start; trials still wait for the pips (`TrialsReady`).
- Unit-tested and build-checked only: the mixer's start, and every sound at the same loudness as before, need a rig run (Pip's yes).

## 2026-11-14 Sounds from OpenRigs.Audio (for Tom)

- **Tom: the penalty sound is the lab's**, the same in every dome task (OpenRigs.Audio `PenaltySoundBuffer`). It is quieter by about 5 dB than this repo's own sound was and plays from rig.yml's `penaltySoundChannel` (the centre speaker) alone, equalised for that speaker, where the old one played on every channel. Its level is the task file's `penalty.sound.levelDbfs`; the task file names no sound file.
- **Tom: the pips and the reward tone now play only on rig.yml's speakers, each equalised with its filter** (OpenRigs.Audio `SpeakerBuffer`), where before they went to all 32 mixer channels as they were. The background noise plays on every rig.yml speaker as it is (not equalised). Loudness at the mouse will differ from before by each speaker's filter; check it on the rig.
- The speakers are loaded once at Start from rig.yml (`LoadSpeakers`, into `Speakers`), which stops the workflow naming any speaker without a filter, or with one missing, not symmetric, for another rate or measured on another machine (it replaces `RequireSpeakerFilters`). Unit-tested and build-checked only: needs a rig run.
- The trial row gains `laserTriggerTime` after `laserPowerMw` (the dome tasks' shared block, now 16 columns): the trigger's Harp time, NaN without optogenetics or on a trial answered before its trigger. `BonsaiData_StimulusState`'s `stimulusActive` is True/False, not 1/0. Zapit's armed reply is checked by lib-open-rigs' `ZapitTrial.CheckArmed`, as in coen-dome-decision-making.

## Dome-task alignment audit (14 Nov 2026): not changed

- `outcomeTime` duplicates `harpTime` by choice, so every moment of a trial is named.
- This task's `audiovisual` and coen-dome-decision-making's `audiovisualCoherent` are different tasks' trial types, so their names stay.
- The rig C# generated in both dome tasks is identical; generating it once in a lab package is a larger change than it saves.
- `rngSeed` is both in the task file and an `RngSeed` event: the file's is what was asked (null draws one), the event's what ran.
- A trial cut by Stop gets no row; its Go is in `BonsaiData_Async`.
- Trial 1 starts about 2.3 s after Go here and 1.2 s in coen-dome-decision-making: each task's own start-up, neither a bug.
- `history` stays in trial-columns.yml until the old data's refactor.

## 2026-11-14 Camera video on GREEN-DOME-2 (test mouse, open)

The log is `K:\config\machines\GREEN-DOME-2\switch\iterate-log.md` (round 3).

- **The last video file of a run loses the frames still queued at Stop.** LogVideo (lib-open-rigs OpenRigs.Logging) uses Bonsai.Vision's `VideoWriter` with `Buffered` true: frames wait on Rx's EventLoopScheduler, whose thread is a background thread, and headless Bonsai exits within 1 s of the workflow ending, so the queue and the file's closing are cut off. The last AVI's final RIFF AVIX and LIST movi declare size 0 and the OpenDML total is 0; readers that trust the index see only up to the last 1 GB boundary. Measured: 7 to 1,231 Pupil frames lost per run (1,231 with 1,067 queued at Stop, reproduced on demand under CPU load); files closed by the hourly roll-over are complete; the camera CSVs list the lost frames, so they do not show the loss. Being replaced by the ffmpeg recording (another session, 14 Nov 2026); whatever writes video must finish its queue and close its file before Bonsai exits, with a bounded wait that fails loudly (a test writer whose thread kept Bonsai alive hung at Stop until the agent killed it at 30 s, leaving every file unfinished).
- **Add a check that each video holds every frame its CSV lists** (count the frames in the file, not the index), after every run.
- **The Pupil camera's noise makes its video expensive.** rig.yml `cameras.pupil.gain: 20` (Body and Face 0): frame-to-frame noise 3.7 grey levels, 139 KB a frame (Body 11, Face 6), about 6.4 GB per 15 min, and its writer thread near a full core; when it falls behind, queued 1440x1080 frames hold 1 to 4 GB of memory until written. Proven by a gain-0 copy: 2 KB a frame, noise 0.01, but the image 6.5 times darker. Pip, 14 Nov 2026: camera settings stay as they are until a better lens arrives; re-measure frame size and writer load then.
- Fixed 15 Nov 2026 (Pip's decisions, above): `CreateCorridorSetup` read `SweepFile` (`waveforms\sweep.json`, relative) at Go: once in 11 launches on GREEN-DOME-2 (test-0946, the first after a package update) Bonsai's working folder was `.bonsai` and the run ended at Go (DirectoryNotFoundException). Not reproduced since. `LoadSweep` now reads it at Start, naming the full path when it is missing. Why the working folder was `.bonsai` that once is still unknown.

## 2026-11-14 First runs on hardware (BLUE-OPTO-2, test mouse, unattended)

The log is `K:\config\machines\BLUE-OPTO-2\switch\iterate-log.md`; GREEN-DOME-2's is beside it.

- Fixed then (tests hold both): Ready never came (a Zip now, StartupWatchdog on its own branch), and ZapitCommands is a BehaviorSubject, so a per-trial multicast's completion cannot close it.
- Measured on the rig with both fixes, through the agent as the Setup page launches: stages 0-2 Ready in 17-29 s and idle 2 s after Stop (stage 0's drift ends 5 trials with the wheel still; stages 1 and 2 hold trial 1, having no time limit); stages 3, 4 and 5 at 99 trials each, first pip 0.49-0.51 ms after its scheduled time (max 1.02), drift under 0.25 ms an hour, every trial ending by Timeout 12.000 s after the first pip, the 2.000 s penalty timeout after each, stage 5 truncating 16 of 99 at 0.4 and 0.6, `BonsaiData_StimulusState` every 50 ms, no dictionary problems, no dropped camera frame, Stop to idle 1-2 s.

### Still untried

- Zapit (no Zapit run on this rig: the real one would emit light at 0 mW, see Open; the simulated one makes the rig's presence ask for an Override, which an unattended session does not give).
- Licks, the wheel, choices and rewards need a mouse; the valves need a calibration.

## 2026-11-14 Pip's Zapit line decision

- **Pip, 14 Nov 2026: the ZapitTrigger line (DO3) is a held level, never a board-timed pulse.** It goes high at the trigger and stays high until the workflow clears it `triggerHoldSeconds` after the outcome, so the masking light on it lasts as long as the laser. lib-open-rigs `LabRig` (954353d) refuses a rig.yml whose `outputPulseEnable` lists the Zapit line (the board would end it after its `PulseDO3` width: 5 ms, or 3000 ms on BLUE-OPTO-2); `config\rig-template.yml` has `"DO1, DO2"`. RigHardware's `WithoutPulseLine` already dropped the line at run time. Every dome's rig.yml needs `outputPulseEnable: "DO1, DO2"`.
- **Open: check on the first test run** (BLUE-OPTO-2, `test` mouse, docs\zapit-rig-test.md step 3): on the scope, or in Zapit's log and the Behavior board log, DO3 rises `laserToStimulusSeconds` before the first pip and falls `triggerHoldSeconds` after the outcome, with the masking light lit for all of it and Zapit started on the rising edge.

## 2026-11-13 Pip's timing decisions

- **Pip, 13 Nov 2026: Zapit adds no time; laserToStimulusSeconds is signed (before/after the stimulus).** A hardware-triggered Zapit is armed during the interval (once the last trigger line has fallen, `triggerHoldSeconds` after the outcome, now at most `itiMinSeconds - laserToStimulusSeconds - 0.2 s`); the trigger rises `laserToStimulusSeconds` before the first pip (after it if negative, at most the stage's `timeLimitSeconds` or 2 s on a stage without one), and a Zapit reply not in by then stops the task. A trial that ends before its trigger has no laser.
- **Pip, 13 Nov 2026: the lead applies in every experiment.** The first pip comes max(`laserToStimulusSeconds`, 0) after the trial's start, optogenetics on or off (beep 2 at the default 0.1 s; it was beep 1 without optogenetics), and `itiSeconds` runs from the outcome to the first pip. Trial 1 now waits its interval too. **Tell Tom: the first pip is now at the lead after trial start; the quiet waits now count from the start of the interval, so a quiet mouse on stages 3-5 starts sooner.**
- **Pip, 13 Nov 2026: quiet waits per stage** (`lickQuietSeconds`, Tom's `quietSeconds` renamed with its values, 1 s on stages 3-5; `wheelQuietSeconds` and `wheelQuietCm`, 0, the same names as coen-dome-decision-making's). `quietWaitSeconds` (new column) says how much they held the start.
- **Pip, 13 Nov 2026 (audit round 2): the wheel's quiet wait counts the travel in the last `wheelQuietSeconds`** (was all travel since the last restart, so slow fidgeting delayed Go); **the lick wait needs the spout released** for `lickQuietSeconds` (a held contact holds Go); **no laser after an answer** in both domes, and such a trial logs `laserPowerMw` 0. **Tell Tom.**
- **Untried**: unit-tested (`CorridorTests.cs`) and build-checked only.

## 2026-11-13 Audit rounds 3-5: changed

- Done, details in `git log`: Zapit's connection and checks are lib-open-rigs' `Zapit.ConnectZapit`; one reward cap, `MAX_REWARD_UL`; `triggerHoldSeconds`' minimum is `ZAPIT_MAX_RAMP_DOWN_SECONDS`; no `ObserveOn` after `CorridorTask`; `config\main.layout` tracked.

## Audit round 5 (13 Nov 2026): not changed

- Removing the `ObserveOn` after `CorridorTask` stays: one lock, and nothing waits inside it.
- `armSent` is set at a software trigger, which only the `test` mouse runs.
- `wheelQuietCm` 0 is refused unless the wheel wait is off: any travel would hold Go on encoder jitter.
- The 20 ul cap is written in three places in lib-open-rigs, tied by tests.
- A calibration `pulseGapMs` of 0 is allowed; the domes fire at the calibrated gap.
- Lab IDs are not checked unique over time; the lab never reuses them.

## Audit round 4 (13 Nov 2026): not changed

- coen-qc-calibrate-valve and the domes pace pulses on different Harp-synced devices, about 1 ms apart.
- The right valve needs a calibration too (`DeliverReward` loads both lines), though the corridor rewards on the left.
- No Go time column but `trialStartTime`; the trigger is `laserTriggerTime` from 14 Nov 2026 (NaN without optogenetics or when the mouse answered first).
- A skipped trial leaves Zapit armed; the next trial disarms it.
- The 0.5 s wall-clock `rx:Delay` before `TrialsReady` stays: it holds the setup once, before the first trial, and no trial wait depends on it.

## Audit round 3 (13 Nov 2026): not changed

- coen-dome-decision-making: discrimination's minimum interval equals its `drinkSeconds`; both are Gerion's measured values.
- jsonschema accepts K:'s old rig.yml keys, but the generated deserializer and app-rig-control's key walk refuse them.
- `usedBy` is filled by app-rig-control's `python -m app_rig_control.names add <clone> --apply` at switch step 8.
- A skipped trial leaves Zapit armed; app-zapit disarms it.
- coen-dome-navigation arms the next trial during the last one's ramp-down; the trigger hold is at least the longest ramp-down.
- coen-dome-decision-making has no `goTime` column; it is `itiStartTime + itiSeconds - lead + quietWaitSeconds`.

## Open

- Old log readers deleted 15 Nov 2026 (Pip); migrating every mouse's past sessions into the current format is planned in its own session.

### To fix later (Tom, 10 Nov 2026)

- **The masking LEDs stay on after a Stop mid-trial: fixed in the workflow, not yet checked on a rig** (11 Nov 2026, Decided below). Check on BLUE-OPTO-2 with a `test` mouse: Stop mid-trial, by rig-control's Stop and by the editor's, and the LEDs go off within a second; the board is in Standby by then, so if they stay on, the board ignores writes in Standby.
- **rig-control's text panel pops outside its window.** In the panel where you enter text, clicking Show (or a similar button) makes the panel spill past the window's frame, so it looks broken. Note the exact panel and button next time, then fix the layout.

### For Pip (found 9 Nov 2026 on BLUE-OPTO-2, with Claude)

- **Calibration folder switch-over** (rig-setup's): BLUE-OPTO-2 done by hand (MeshMap.csv, gammalut.bmp and LickSpoutPositions copied from `C:\RigConfigs` into `calibration\`, rig.yml's `projectionCalibration` pointed there). BLUE-OPTO-1 and GREEN-DOME-2 still need it; whether rig-setup should do the copy is yours.

### Zapit on BLUE-OPTO-2: before any optogenetics with a mouse (found 9 Nov 2026; affects av-dome too; for Pip, Tom and Ainiah)

- **The masking light lasts 3 s; the laser up to 12.5 s.** Changed in the workflow, not yet tried on the rig (Tom and Pip, 10 Nov 2026; software timing of the end accepted): the board never pulses the trigger line (RigHardware), so TaskLogic sets it high at the trigger (`laserToStimulusSeconds` before the first pip) and clears it `triggerHoldSeconds` (1 s) after the outcome, on every trial of an optogenetics experiment only (Pip, 13 Nov 2026). The LEDs on it now last from the trigger until after the laser has ramped down (ramp-downs capped at 500 ms in `ZapitConditions`), and the board log has the falling edge. Zapit must start on the rising edge alone: check on BLUE-OPTO-2 with a `test` mouse (docs\zapit-rig-test.md, step 3) before a real one. Moving the LEDs to Zapit's masking output (NI AO3) remains the hardware-timed alternative.
- **The laser glows on laser-off trials.** Zapit's "off" is 0 V (`sendSamples.m`), but this rig's Zapit settings put 0 mW at -0.04 V, so 0 V gives about 0.4 mW: a faint spot on every 0 mW trial (test-2055). Confirm with a power meter, then fix the laser's zero or Zapit's off voltage (upstream).
- **Laser offset is not logged.** Onset is the DO3 edge (Behavior board log). The stop is sent at the outcome, but only Zapit's reply, about 1 s later, is logged. Wiring Zapit's laser control output into the Behavior board's spare ADC0 logs it at 1 kHz, with no workflow change.
- **The arming rule leaves out Zapit's StopStimulus reply** (audit round 5). `triggerHoldSeconds` may be up to `itiMinSeconds - laserToStimulusSeconds - 0.2 s` (`Corridor.cs` check, task.py `_the_next_trial_is_armed_in_time`), but `ZapitClient` sends one command at a time, so the next arm waits for the StopStimulus reply, about 1 s on 9 Nov (Laser offset, above). Time the stop round trip on BLUE-OPTO-2 in plan v11 Switch step 9's `test` run, then add it to the rule in both the C# and the pydantic model.
- **Zapit itself:** simulated mode is broken in 1.0.4 (`zapit.simulated.DAQ` lacks `stopAndDeleteDOTask`), so testing needs the real hardware and a scanner calibration. Starting Zapit switches the rig camera it opens to Mono12, which Bonsai cannot record until SpinView sets it back to Mono8; `camera.connectionIndex: 2` (Mono8) in Zapit's settings should stop it.

### For Pip and Tom to decide

- **Lick thresholds: test 0 first** (Pip, 10 Nov 2026). Tom's workflow left both Lickety Split thresholds at 255/255 (its include's values); this one maps rig.yml's, 0/0 on BLUE-OPTO-1, BLUE-OPTO-2 and in the template. The threshold decides what counts as a lick, so the stage 3+ reward, the wrong-lick limits and WaitForNoLicks. Next: a `test`-mouse run on BLUE-OPTO-2 with nobody at the spouts (any lick logged at 0 is false), then set rig.yml from it (production config, on Pip's word).
- **Zapit trigger cable.** BLUE-OPTO-2 is hardware-triggered (DO3; BLUE-OPTO-1 is not). If DO3 is not cabled to Zapit's trigger input, no laser fires although the trial row gives a `laserPowerMw`: check with a photodiode (`docs\zapit-rig-test.md`). The pulse width is the masking question above. A rig without hardware triggering refuses optogenetics with any mouse but `test`.
- **Modality is set per mouse on the form** (the task file's default is auditory): TC001 to TC004 auditory, TC005 and TC006 visual. The stage suggestion is per modality.

### Before a rig runs it

- **GREEN-DOME-2**, one of Tom's rigs, had no rig.yml on `K:\config\machines`; it has one now (`GREEN-DOME-2_rig.yml`, 9 Nov 2026). Check it is on the lab rig model (`behaviorBoard.main.wiring`: DO1 left and DO2 right RewardValve, DO3 ZapitTrigger, each valve calibrated) with this workflow's devices before it runs this.
- In rig-setup's Repos since 9 Nov 2026: BLUE-OPTO-1, BLUE-OPTO-2, GREEN-DOME-1 and GREEN-DOME-2 each pull `main` into `C:\Github\Coen-Lab\coen-dome-navigation` within 30 minutes of a push, never while recording, and check its names. A push here deploys to all four.
- **Tested on BLUE-OPTO-2** (9 Nov 2026, `test` mouse), with optogenetics off and then on: Bonsai and Zapit agree on every trial. The logging bugs found are fixed (Decided); the Zapit rig issues are above. Not yet run: one of Tom's mice, a dome.
- **Tom's mice move between BLUE-OPTO-1, BLUE-OPTO-2, GREEN-DOME-1 and GREEN-DOME-2**, so each needs a rig.yml, this clone and the calibration folder (above). A mouse without a spout file gets rig.yml's `lickSpoutDefaultPosition` (all 0 on BLUE-OPTO-2), not Tom's In positions (3550, 5500, 4700, 2150, 950): set and Save each mouse's positions in its first experiment (Pip, 10 Nov 2026; the rig.yml default stays).
- `config\main.layout` is empty (audit round 4): save one with the camera windows on a machine with the Spinnaker SDK, then commit it (`.bonsai\Setup.ps1` copies it).
- A Zapit ticked with no `zapitConfigPath` is refused on the form (the task schema). A path that names no file on the rig stops the experiment at Go, before trial 1: the Zapit condition-count check reads the file when Zapit connects (zapit-v2).

### Smaller

- For analysis: logs are written one file per clock hour (`..._T20-00-00`, `..._T21-00-00`), so an experiment across an hour has two of each; join them.

## Decided

- **Rebuilt on the shared dome blocks** (plan v11, 13 Nov 2026): RigHardware, HarpLogs, DeliverReward, ProjectorDisplay and the fork's Zapit replace the local copies; the task is `src\Extensions\Corridor.cs`, tested in `tests\Extensions.Tests`, every task wait on the Harp clock. Each stage's `timeLimitSeconds` and `quietSeconds` (null for none) replace the booleans (since 13 Nov 2026: `quietSeconds` is `lickQuietSeconds`, 0 for none); `itiSeconds` runs from the outcome, after any timeout, and includes the 2 s to drink (2 to 3 s, Tom's 2 s then 0 to 1 s); the row's `Seconds` is when the outcome was known (`outcomeTime` gone; since 14 Nov 2026: `outcomeTime` is a column again, equal to `harpTime`); a stage past the table stops the workflow at Go. Mice are launched and filed by lab ID.

- **Wheel: 75 mm** (measured, 9 Nov 2026), as Tom's workflow converted it (75 mm, 4096 counts a turn): rig.yml's `wheelDiameterMm`, 75 on every dome since 9 Nov 2026; the task file's override went with the rebuild (plan v11), so RigHardware's wheel distance is the track's.
- **Reward 3 ul** (Pip, 15 Nov 2026): `rewardSizeUl` 3.0 on every stage. The 2 ul of 10 Nov 2026 sat below BLUE-OPTO-2's left valve curve (fitted 25 to 125 ms; 2 ul needs 24.4 ms), so every task reward there was clamped to 25 ms, 2.04 ul (`clamped=True` in RewardDelivered; rig test, 15 Nov 2026); 3 ul is about 39 ms there, inside the curve. Was: **Reward stays 2 ul** (Pip, 10 Nov 2026), not Tom's 3: `rewardSizeUl` 2.0, planned from the left valve's calibration (`calibration\valve-DO1.json`; plan v11: calibrated reward in every case, an uncalibrated valve stops the workflow at Start, the `test` mouse runs on one with no water). Tom's mice got 3 ul as one fixed opening (42 ms on BLUE-OPTO-2, 45 ms on BLUE-OPTO-1, 65 ms on the GREEN-DOMEs), measured as about 3 ul.
- **IntervalLog stays dropped** (Pip, 10 Nov 2026): Tom's IntervalLog (wheel, licks, tone and grating on the PC clock at every change) is replaced by the Harp logs (wheel, licks) and `BonsaiData_StimulusState` (tone, grating, position every 50 ms on the Harp clock).
- **Data-dictionary names**: this workflow's trial columns and streams are on `K:\config\data-dictionary.json`; app-rig-control's `python -m app_rig_control.names add <clone> --apply` adds new ones.
- **Base.** av-dome `pip/main-switchover` for everything that is not the task (rig.yml, devices, ExperimentControl, spout commands, reward lines, logging, manifest, live view), because every corridor rig is one of its rigs; Tom's task groups were kept node for node until the rebuild of 13 Nov 2026 moved the task into `src\Extensions\Corridor.cs` (above).
- **Task file.** Read at Go from `TaskPath` (YAML) instead of `session-schemas\...json` at start; laid out as av-dome's since 9 Nov 2026 (Pip): `trainingStages` rows from 0 (Tom's stages 1 to 6 are rows 0 to 5) say what each stage does, where the workflow tested the stage number (reward zone and size, lick or zone entry, wrong-lick limit, penaliseing a wrong-zone ending, the quiescent wait, time limit, end of track, track scale, frequency drift, truncation); the stage is the TrainingStage setting, which `suggest-stage` fills; `trialStructure` holds the ITI (now drawn evenly between itiMinSeconds and itiMaxSeconds, where Tom's drew 0 or 1 s), the 12 s time limit and the track lengths, `penalty` the timeout and penalty sound, and `rngSeed` seeds every draw. centerFreq, rewardWindowSize and engagement are gone (the zones are written in the rows). Subject and session come from rig-control (`LogConfig` dropped).
- **Waveforms** made once and committed, each at the loudness it plays at (plan v11): pips and reward tone at Tom's amplitude squared, the noise at a pip's RMS; each plays at the task file's `levelDbfs` (15 Nov 2026). The sweep is `src\waveforms\sweep.json`'s, read by the workflow and task.py, no longer task fields.
- **Mixer** is lib-open-rigs' `SpeakerMixer` (rig.yml's device, the 3 s start delay before the dome's sound card, as av-dome's).
- **Zapit** host, port, hardware triggering and trigger line from rig.yml's `zapit:`.
- **The laser** (zapit-v2, Tom and Pip, 9 Nov 2026; ZAPIT-V2-CHANGES.md has the why): the `optogenetics` block is `useZapit`, `zapitConfigPath`, `laserPowerMw`, `laserOnProbability`, `laserMaxSeconds`, `laserToStimulusSeconds` (0.05-0.2 s) and `trialsBeforeLaser` (Pip's names, 9 Nov 2026; av-dome's `stimDelay`, `excludeRepeats` and `pseudorandomSelection` dropped: only 0 delay works, there are no repeat trials, and each trial's condition is drawn at random, no queue, since 10 Nov 2026). Zapit only ever gets continuous stimuli (it ignores StopStimulus for a timed one): ArmZapit arms it once the ITI has ended (hardware-triggered rigs), the trigger line rises at beep 0 on every trial of an optogenetics experiment (held until 1 s after the outcome since 10 Nov 2026; av-dome's StartLaser pulses it), and LaserOnUntilOutcome stops it at the outcome or after `laserMaxSeconds`. Every trial of an optogenetics experiment is sent a condition, laser-on or laser-off, so galvos and masking light are the same on every trial; p 0.4 after 20 trials without laser. The first tone pip comes `laserToStimulusSeconds` (0.1 s) after beep 0 in an optogenetics experiment, laser-on and laser-off alike.
- **The cap and slow trials.** `laserMaxSeconds` (12.5 s) must be at least the longest stage `timeLimitSeconds` (12 s) + `laserToStimulusSeconds` + 0.25 s, so it never ends a timed-out trial's laser early (task.py's model and Corridor.cs at Go check it). Rows 0 to 2 have no time limit, so a trial slower than `laserMaxSeconds` loses its laser before the outcome. The cap is timed on the Harp clock, with a wall-clock backstop should the board stall.
- **Trials start** once the experiment folder exists and the tones are loaded, 0.5 s later.
- **Trial log** is the lab's `BonsaiData_Trial`, one row per trial as Tom's TrialLog (built at the outcome, below); `outcome` is Correct/Incorrect/NoResponse; rig-control's Performance is Correct over Correct and Incorrect (as av-dome's), with the response rate beside it, where Tom's success rate was Correct over all trials. Names follow av-dome where they can (Pip, 9 Nov 2026): `trainingStage`, `outcome`'s values, `trialType` in words as av-dome's (auditory/visual/audiovisual: the task's modality; av-dome's trialType became text the same day), the `tone`/`visual` prefixes (`toneEndFrequencyHz`, `visualEndSpatialFrequencyCpd`), `truncationFraction`, the `BonsaiData_StimulusState` stream; `timedOut` and `hitEndOfTrack` dropped (endReason says both). Later that day only what changes from trial to trial stayed (Pip): `trainingStage`, `rewardFloorHz`, `rewardCeilingHz` and `audioAmplitude` dropped (fixed for the experiment, in Settings_Task; `suggest-stage` reads the stage there), `optoStimulated` dropped (it was `laserCondition` > 0; zapit-v2's laser-off trials have a condition too, so laser on is now `laserPowerMw` > 0: the power sent, the task's `optogenetics.laserPowerMw`, 0 on laser-off trials; Pip, 9 Nov 2026), and `penalised` split into `penaltySound` and `penaltyTimeoutSeconds`, so other kinds of penalty can follow.
- **Tom's SessionStats and EngagementStats** and the GUI are rig-control's (its view), not the workflow's. Tom's model's `engagement` block, unused, was dropped on 9 Nov 2026.
- **Devices.** Both Lickety Splits and all three cameras are opened and recorded, as av-dome does, because the manifest expects a folder for each device in rig.yml.
- **One calibration folder per rig, as av-dome's** (9 Nov 2026): `C:\Coen-Lab-Local\RigConfigs\<MACHINE>\calibration\` beside rig.yml (MeshMap.csv, gammalut.bmp, LickSpoutPositions), read by every workflow on the rig. Production's `C:\RigConfigs` stays for Tom's auditory-vr-corridor and av-dome's ActiveExperiments; at switch-over its files are copied, never over an existing one. The copies are separate from then on, so adjust a mouse's spout positions in one workflow only.
- **StimulusState is in BonsaiData** (Pip, 9 Nov 2026): `BonsaiData\BonsaiData_StimulusState_*.csv`, beside Trial and Async, so everything Bonsai computes is in one folder. Its own stream, not Async (20 rows a second of 6 typed columns would bury Async's events and squeeze into its text Detail) and not the trial row (about 240 rows a trial; per-trial values such as `endPositionCm` are trial columns). Columns `trialNumber`, `positionCm`, `toneFrequencyHz`, `stimulusActive`, `visualSpatialFrequencyCpd` (were `frequencyHz`, `cueActive`, `spatialFrequency`); Tom's `beep`, `frequencyBin` and `gratingOpacity` dropped, as they follow from `Seconds` and `trialStartTime`, from the frequency and the sweep settings, and from `stimulusActive` and the modality. Zapit's reply log is `BonsaiData\BonsaiData_ZapitReplies` too (was `Zapit\`). A Go then Stop before the first beep no longer leaves an expected folder missing.
- **StimulusState is written row by row** (AutoFlush), so the manifest records its full size; it may still show `closed: false`, as the file closes a few ms after the manifest's last write.
- **Never multicast a shared PublishSubject or ReplaySubject from a stream that completes**: the completion passes on and the subject drops every later value. This stopped the trial log after trial 1 (`TrialOutcome`) and the Async log a second after Go (`AsyncEvents`). Take a one-shot value after the multicast, as `TrialEnded` does; tests hold both. av-dome's `pip/main-switchover` had the same `AsyncEvents` bug, fixed there in 9a43c0e (9 Nov 2026) with this repo's test.
- **DO3 cleared however the workflow stops** (11 Nov 2026, for Tom's 10 Nov report; untried on a rig, above): since the rebuild (13 Nov 2026) RigHardware clears the ZapitTrigger line when the workflow stops; before it, `ClearOutputsOnStop` sat on the Behavior board include's output in BehaviorBoards, so a Stop, an error or ExperimentControl's stop disposes the board first (Bonsai.Harp writes Standby and closes the port), then it opens the board's port and writes OutputClear DO3. A final message into `BehaviorBoard-Main.Commands` (Finally, or Concat on completion) cannot do it: every stop disposes rather than completes the graph, and a value sent during disposal is dropped. A Bonsai killed past rig-control's stop deadline still leaves DO3 high until the next start (PulseController clears DO1 to DO3 then).
- **Trial row at the outcome** (audit, 9 Nov 2026): built when WasRewarded is known (TrialOutcome), not 2 s later at TrialEnded as Tom's TrialLog was, so a Stop in between no longer loses a trial whose reward is logged. `Seconds`, `endPositionCm` and `lickCount` are read then (so `lickCount` leaves out the licks while the mouse drinks, round 3).

### Not changed on audit (9 Nov 2026, round 1)

- Trials start 0.5 s after the experiment folder exists, not on Tom's Start button: decided above.
- Zapit's hardware trigger is the rising edge of `zapit.triggerLine`, raised at beep 0 on every trial of an optogenetics experiment, on every rig; since 10 Nov 2026 held until 1 s after the outcome rather than pulsed for rig.yml's width (5 ms in Tom's; 3000 ms on BLUE-OPTO-2, still av-dome's), see above.
- Waveform paths relative to `src`: av-dome's `src\LocalFiles` resolve the same under rig-control.
- The suggestion not kept across an agent restart, and `test` getting one: edits are kept; harmless.
- Tom's CueOnsetHarp value kept. (Round 3 found it is the first tone pip and grating frame, after Skip's silent beeps, not beep 0 or the laser onset; the column is now the lab's `stimulusOnsetTime`.)

### Not changed on audit (9 Nov 2026, round 2)

- The suggestion lost on an agent restart (raised again): restarts are rare and a person's edits are kept.
- The manifest's `ConfigFiles` naming `calibration\MeshMap.csv` and `gammalut.bmp` while rig.yml points the projector at `C:\RigConfigs`, and the spout file read before zeroing may finish: av-dome's own, fixed there or in lib-open-rigs if at all. (BLUE-OPTO-2 has since been switched over, so there both point at `calibration\`.)

### Not changed on audit (9 Nov 2026, round 3)

- The live wheel chart in counts/s, not cm/s: the wheel diameter was undecided then, so a cm/s scale would have been a guess. It is now 75 mm (Decided), so cm/s is possible.
- `BonsaiData_ZapitReplies` not checked by the manifest on its own: it is written only with optogenetics on, and the manifest expects the `BonsaiData` folder, which Trial and Async always fill.

### Not changed on audit (10 Nov 2026, the 9 Nov naming pass)

- `toneEndFrequencyHz` and `toneFrequencyHz` beside `visual...`: Pip chose "tone" for the sweep (9 Nov 2026); av-dome's `audio...` is a speaker position, a different thing.
- Laser timing names differ from av-dome's (`laserToStimulusSeconds`, `laserMaxSeconds` against `laserDelaySeconds`, `laserDurationSeconds`): measured from different points (first pip against trial start) and a cap against a timed stimulus, so one name would mislabel one task.
- `trialStructure` shaped unlike av-dome's (one `quiescentSeconds`, `timeLimitSeconds`): the tasks differ (a lick-free wait against a drawn quiescent period; a whole-trial limit against a response window).
- StimulusState's column meanings in the README only, not the manifest: the lab has no schema for stream columns yet; adding one is a platform change.

### Not changed on audit (10 Nov 2026, rounds 1-5 of 5)

- Pip, 13 Nov 2026: the trigger line and masking LEDs only in optogenetics experiments.
- rig.yml's `PulseDO3` width still written to the board: unused while DO3 is not pulse-enabled; the mapping keeps the board's settings as av-dome's.
- `triggerLine: 0` allowed by the rig model (ge=0, as av-dome's, whose model this one copies field for field) while `DigitalOutputLine` refuses it at start: every rig.yml on the share says 3, and the refusal is loud.
- `Settings_ZapitConditions` and rig-control's kept copy of the Zapit file are two copies: the settings copy is this workflow's record beside its other settings, the kept copy rig-control's rule for every `...ConfigPath` field.
- `rewardSizeUl` on every trial row though fixed for the experiment: a lab-wide column shared with av-dome, where large-reward trials make it vary, so one reader serves both tasks.
- "Required with optogenetics on" and `ConnectZapit`'s "when the task YAML enables optogenetics", though the switch is `useZapit`: plain English for the block's one switch; the schema and the validator messages name `useZapit`.
- rig-control's `taskfile.py` docstring names `taskParameters.trialStructure.itiMin` as its example of a dotted field: a generic example matching rig-control's own synthetic tests, not a workflow's field.

### Audit round 1 (13 Nov 2026): not changed

- TC001's stage suggestion ignores Tom's two 12 Nov runs under `sub-PCO1-01456` (Pip: they stay as they are).
- An uncalibrated right valve refuses Start although the task rewards only the left: the manual RewardRight needs it, and every line gets calibrated.

