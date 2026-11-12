# Backlog

Open work first, then what has been decided. The conversion from `ucl-open/auditory-vr-corridor` is in `git log`. The repo was `coen-av-corridor` until 9 Nov 2026 (renamed on Pip's word); the task file is `config\task-abstract-av-corridor.yml` (Pip).

## Open

### To fix later (Tom, 10 Nov 2026)

- **The masking LEDs stay on after a Stop mid-trial: fixed in the workflow, not yet checked on a rig** (11 Nov 2026, Decided below). Check on BLUE-OPTO-2 with a `test` mouse: Stop mid-trial, by rig-control's Stop and by the editor's, and the LEDs go off within a second; the board is in Standby by then, so if they stay on, the board ignores writes in Standby.
- **rig-control's text panel pops outside its window.** In the panel where you enter text, clicking Show (or a similar button) makes the panel spill past the window's frame, so it looks broken. Note the exact panel and button next time, then fix the layout.

### For Pip (found 9 Nov 2026 on BLUE-OPTO-2, with Claude)

- **Tom's mice are launched by eartag, but their data, files and history use the lab ID** (12 Nov 2026). rig-control lists TC001-LL but passes `PCO1-01456` as SubjectId, so:
  - **Sessions save under the eartag:** `Acquired-Data\sub-PCO1-01456\`, not `sub-TC001\`, which splits TC001's data from its earlier sessions. Tom wants new sessions saved as `sub-TC001` (the lab ID without the `-LL` suffix, as on the RDSS share).
  - **The spout file** (`LickSpoutPositions\<SubjectId>.csv`) wasn't found, so the spouts went to rig.yml's all-0 default. Worked around: BLUE-OPTO-2's spout files are copied under eartags (TC001 `PCO1-01456`, TC002 `PCO1-01454`, TC005 `PCO1-01554`; TC003 `PCO1-01474`, TC004 `PCO1-01553` and TC006 `PCO1-01556` have no file here). Other rigs need the same, and the copies go stale whenever Tom's old workflow re-saves `TC00X.csv`.
  - **`stage.py`'s history** (the share's `sub-TC001`) wasn't found, so it suggested Stage 0. Until it's fixed, set the stage by hand.

  All three need the eartag-to-lab-ID mapping, which is in `pyrat\data\training_schedule.json`. Pip, your call where to apply it: in rig-control (pass or add the lab ID) or in the workflow and `stage.py`.
- **Calibration folder switch-over** (rig-setup's): BLUE-OPTO-2 done by hand (MeshMap.csv, gammalut.bmp and LickSpoutPositions copied from `C:\RigConfigs` into `calibration\`, rig.yml's `projectionCalibration` pointed there). BLUE-OPTO-1 and GREEN-DOME-2 still need it; whether rig-setup should do the copy is yours.

### Zapit on BLUE-OPTO-2: before any optogenetics with a mouse (found 9 Nov 2026; affects av-dome too; for Pip, Tom and Ainiah)

- **The masking light lasts 3 s; the laser up to 12.5 s.** Changed in the workflow, not yet tried on the rig (Tom and Pip, 10 Nov 2026; software timing of the end accepted): the board no longer pulses the trigger line (`WithoutPulseLine` drops it from rig.yml's `outputPulseEnable`, in this workflow only; av-dome still pulses it 3 s), so `TriggerPulse` sets it high at beep 0 and `OptoOnset` clears it (`ZapitTriggerClear`) 1 s after the outcome, on every trial. The LEDs on it now last from the trial's start until after the laser has ramped down (ramp-downs capped at 500 ms in `ZapitConditions`), and the board log has the falling edge. Zapit must start on the rising edge alone: check on BLUE-OPTO-2 with a `test` mouse (docs\zapit-rig-test.md, step 3) before a real one. Moving the LEDs to Zapit's masking output (NI AO3) remains the hardware-timed alternative.
- **The laser glows on laser-off trials.** Zapit's "off" is 0 V (`sendSamples.m`), but this rig's Zapit settings put 0 mW at -0.04 V, so 0 V gives about 0.4 mW: a faint spot on every 0 mW trial (test-2055). Confirm with a power meter, then fix the laser's zero or Zapit's off voltage (upstream).
- **Laser offset is not logged.** Onset is the DO3 edge (Behavior board log). The stop is sent at the outcome, but only Zapit's reply, about 1 s later, is logged. Wiring Zapit's laser control output into the Behavior board's spare ADC0 logs it at 1 kHz, with no workflow change.
- **Zapit itself:** simulated mode is broken in 1.0.4 (`zapit.simulated.DAQ` lacks `stopAndDeleteDOTask`), so testing needs the real hardware and a scanner calibration. Starting Zapit switches the rig camera it opens to Mono12, which Bonsai cannot record until SpinView sets it back to Mono8; `camera.connectionIndex: 2` (Mono8) in Zapit's settings should stop it.

### For Pip and Tom to decide

- **Lick thresholds: test 0 first** (Pip, 10 Nov 2026). Tom's workflow left both Lickety Split thresholds at 255/255 (its include's values); this one maps rig.yml's, 0/0 on BLUE-OPTO-1, BLUE-OPTO-2 and in the template. The threshold decides what counts as a lick, so the stage 3+ reward, the wrong-lick limits and WaitForNoLicks. Next: a `test`-mouse run on BLUE-OPTO-2 with nobody at the spouts (any lick logged at 0 is false), then set rig.yml from it (production config, on Pip's word).
- **Zapit trigger cable.** BLUE-OPTO-2 is hardware-triggered (DO3; BLUE-OPTO-1 is not). If DO3 is not cabled to Zapit's trigger input, no laser fires although the trial row gives a `laserPowerMw`: check with a photodiode (`docs\zapit-rig-test.md`). The pulse width is the masking question above. A rig without hardware triggering refuses optogenetics with any mouse but `test`.
- **Modality is set per mouse on the form** (the task file's default is auditory): TC001 to TC004 auditory, TC005 and TC006 visual. The stage suggestion is per modality.

### Before a rig runs it

- **GREEN-DOME-2**, one of Tom's rigs, had no rig.yml on `K:\config\machines`; it has one now (`GREEN-DOME-2_rig.yml`, 9 Nov 2026). Check it has `rewardValves` and this workflow's devices before it runs this.
- In rig-setup's Repos since 9 Nov 2026: BLUE-OPTO-1, BLUE-OPTO-2, GREEN-DOME-1 and GREEN-DOME-2 each pull `main` into `C:\Github\Coen-Lab\coen-dome-navigation` within 30 minutes of a push, never while recording, and check its names. A push here deploys to all four.
- **Tested on BLUE-OPTO-2** (9 Nov 2026, `test` mouse), with optogenetics off and then on: Bonsai and Zapit agree on every trial. The logging bugs found are fixed (Decided); the Zapit rig issues are above. Not yet run: one of Tom's mice, a dome.
- **Tom's mice move between BLUE-OPTO-1, BLUE-OPTO-2, GREEN-DOME-1 and GREEN-DOME-2**, so each needs a rig.yml, this clone and the calibration folder (above). A mouse without a spout file gets rig.yml's `lickSpoutDefaultPosition` (all 0 on BLUE-OPTO-2), not Tom's In positions (3550, 5500, 4700, 2150, 950): set and Save each mouse's positions in its first experiment (Pip, 10 Nov 2026; the rig.yml default stays).
- No `config\main.layout` yet: save one with the camera windows on a machine with the Spinnaker SDK, then commit it (`.bonsai\Setup.ps1` copies it).
- The old workflow's history: `stage.py` reads the old TrialLogs from each rig's `C:\Github\Coen-Lab\auditory-vr-corridor\Logs` and the RDSS share. On BLUE-OPTO-2 the local path does not exist, but the share answers with every TC mouse's history (9 Nov 2026); check the share from each other rig's account, or every suggestion there says it has no history.
- When jacuzzi-ingest clears staging (keeping each manifest), `stage.py` gives no stage for a mouse whose experiments are only there ("Only Its Manifest Is Left"): add ingest's destination to its `ROOTS` then, in any order (a folder's data found in any root counts; another workflow's manifests are passed over, by `source.repo`).
- A Zapit ticked with no `zapitConfigPath` is refused on the form (the task schema). A path that names no file on the rig stops the experiment at Go, before trial 1: the Zapit condition-count check reads the file when Zapit connects (zapit-v2).
- The wheel: `RigSetup` has its own RunningWheel group in Position mode, because OpenRigs.Devices' include sets Displacement and then differences and accumulates, a speed sample, not a distance (coen-behaviour-box does the same). av-dome's `pip/main-switchover` uses the include: whether its `BoardWheelDistanceCm` matters there, and fixing the include in lib-open-rigs, are for Pip.

### Smaller

- No current-trial metrics: av-dome's Status group (ReportMetric) was not kept, so rig-control's Current Trial line is blank until a row lands. Report the trial number and stage.
- For analysis: logs are written one file per clock hour (`..._T20-00-00`, `..._T21-00-00`), so an experiment across an hour has two of each; join them. `endPositionCm` is read when the row is written (after the reward tone, or the error tone and timeout), not at the outcome; for the position at the outcome use StimulusState's last row or the wheel encoder.

## Decided

- **Wheel: 75 mm** (measured, 9 Nov 2026), as Tom's workflow converted it (75 mm, 4096 counts a turn); the task file's `wheelDiameterMm: 75` overrides rig.yml's, so the tracks are as long as the mice trained on. Every dome's rig.yml has said 75 since 9 Nov 2026 (desk edits on the share, taken at 19:00).
- **Reward stays 2 ul** (Pip, 10 Nov 2026), not Tom's 3: `rewardSizeUl` 2.0, one opening of DO1's width on an uncalibrated valve (42 ms on BLUE-OPTO-2), the calibrated volume once a valve is calibrated. Tom's mice got 3 ul as one fixed opening (42 ms on BLUE-OPTO-2, 45 ms on BLUE-OPTO-1, 65 ms on the GREEN-DOMEs), so an uncalibrated rig's DO1 width sets what 2 ul really is.
- **IntervalLog stays dropped** (Pip, 10 Nov 2026): Tom's IntervalLog (wheel, licks, tone and grating on the PC clock at every change) is replaced by the Harp logs (wheel, licks) and `BonsaiData_StimulusState` (tone, grating, position every 50 ms on the Harp clock).
- **Data-dictionary names**: this workflow's trial columns and streams are on `K:\config\data-dictionary.json`; `scripts\add-names.ps1` adds new ones.
- **Base.** av-dome `pip/main-switchover` for everything that is not the task (rig.yml, devices, ExperimentControl, spout commands, reward lines, logging, manifest, live view), because every corridor rig is one of its rigs; Tom's task groups kept node for node.
- **Task file.** Read at Go from `TaskPath` (YAML) instead of `session-schemas\...json` at start; laid out as av-dome's since 9 Nov 2026 (Pip): `trainingStages` rows from 0 (Tom's stages 1 to 6 are rows 0 to 5) say what each stage does, where the workflow tested the stage number (reward zone and size, lick or zone entry, wrong-lick limit, punishing a wrong-zone ending, the quiescent wait, time limit, end of track, track scale, frequency drift, truncation); the stage is the TrainingStage setting, which `suggest-stage` fills; `trialStructure` holds the ITI (now drawn evenly between itiMinSeconds and itiMaxSeconds, where Tom's drew 0 or 1 s), the 12 s time limit and the track lengths, `punishment` the timeout and error tone, and `rngSeed` seeds every draw. centerFreq, rewardWindowSize and engagement are gone (the zones are written in the rows). Subject and session come from rig-control (`LogConfig` dropped).
- **Waveforms** made once and committed; `CheckWaveforms` stops at Go if the task's sweep or amplitude is not the one they were made for. Tones keep Tom's loudness (written at amplitude and scaled by it again).
- **Mixer** is av-dome's (rig.yml's device, the 3 s start delay before the dome's sound card).
- **Zapit** host, port, hardware triggering and trigger line from rig.yml's `zapit:`.
- **The laser** (zapit-v2, Tom and Pip, 9 Nov 2026; ZAPIT-V2-CHANGES.md has the why): the `optogenetics` block is `useZapit`, `zapitConfigPath`, `laserPowerMw`, `laserOnProbability`, `laserMaxSeconds`, `laserToStimulusSeconds` (0.05-0.2 s) and `trialsBeforeLaser` (Pip's names, 9 Nov 2026; av-dome's `stimDelay`, `excludeRepeats` and `pseudorandomSelection` dropped: only 0 delay works, there are no repeat trials, and each trial's condition is drawn at random, no queue, since 10 Nov 2026). Zapit only ever gets continuous stimuli (it ignores StopStimulus for a timed one): ArmZapit arms it once the ITI has ended (hardware-triggered rigs), the trigger line rises at beep 0 on every trial (held until 1 s after the outcome since 10 Nov 2026; av-dome's StartLaser pulses it), and LaserOnUntilOutcome stops it at the outcome or after `laserMaxSeconds`. Every trial of an optogenetics experiment is sent a condition, laser-on or laser-off, so galvos and masking light are the same on every trial; p 0.4 after 20 trials without laser. The first tone pip comes `laserToStimulusSeconds` (0.1 s) after beep 0 in an optogenetics experiment, laser-on and laser-off alike.
- **The cap and slow trials.** `laserMaxSeconds` (12.5 s) must be at least `trialStructure.timeLimitSeconds` (12 s) + `laserToStimulusSeconds` + 0.25 s, so it never ends a timed-out trial's laser early (task.py's model and InitialiseOptoTrial at trial 1 check it; rig-control's schema only checks that it is above 0). Rows 0 to 2 have no time limit (timeLimit), so a trial slower than `laserMaxSeconds` loses its laser before the outcome.
- **Trials start** once the experiment folder exists and the tones are loaded, 0.5 s later.
- **Trial log** is the lab's `BonsaiData_Trial`, one row per trial as Tom's TrialLog (built at the outcome, below); `outcome` is Correct/Incorrect/NoResponse so rig-control's performance is Tom's success rate. Names follow av-dome where they can (Pip, 9 Nov 2026): `trainingStage`, `outcome`'s values, `trialType` in words as av-dome's (auditory/visual/audiovisual: the task's modality; av-dome's trialType became text the same day), the `tone`/`visual` prefixes (`toneEndFrequencyHz`, `visualEndSpatialFrequencyCpd`), `truncationFraction`, the `BonsaiData_StimulusState` stream; `timedOut` and `hitEndOfTrack` dropped (endReason says both). Later that day only what changes from trial to trial stayed (Pip): `trainingStage`, `rewardFloorHz`, `rewardCeilingHz` and `audioAmplitude` dropped (fixed for the experiment, in Settings_Task; `suggest-stage` reads the stage there), `optoStimulated` dropped (it was `laserCondition` > 0; zapit-v2's laser-off trials have a condition too, so laser on is now `laserPowerMw` > 0: the power sent, the task's `optogenetics.laserPowerMw`, 0 on laser-off trials; Pip, 9 Nov 2026), and `punished` split into `punishSound` and `punishTimeoutSeconds`, so other kinds of punishment can follow.
- **Tom's SessionStats and EngagementStats** and the GUI are rig-control's (its view), not the workflow's. Tom's model's `engagement` block, unused, was dropped on 9 Nov 2026.
- **Devices.** Both Lickety Splits and all three cameras are opened and recorded, as av-dome does, because the manifest expects a folder for each device in rig.yml.
- **One calibration folder per rig, as av-dome's** (9 Nov 2026): `C:\Coen-Lab-Local\RigConfigs\<MACHINE>\calibration\` beside rig.yml (MeshMap.csv, gammalut.bmp, LickSpoutPositions), read by every workflow on the rig. Production's `C:\RigConfigs` stays for Tom's auditory-vr-corridor and av-dome's ActiveExperiments; at switch-over its files are copied, never over an existing one. The copies are separate from then on, so adjust a mouse's spout positions in one workflow only.
- **StimulusState is in BonsaiData** (Pip, 9 Nov 2026): `BonsaiData\BonsaiData_StimulusState_*.csv`, beside Trial and Async, so everything Bonsai computes is in one folder. Its own stream, not Async (20 rows a second of 6 typed columns would bury Async's events and squeeze into its text Detail) and not the trial row (about 240 rows a trial; per-trial values such as `endPositionCm` are trial columns). Columns `trialNumber`, `positionCm`, `toneFrequencyHz`, `stimulusActive`, `visualSpatialFrequencyCpd` (were `frequencyHz`, `cueActive`, `spatialFrequency`); Tom's `beep`, `frequencyBin` and `gratingOpacity` dropped, as they follow from `Seconds` and `trialStartTime`, from the frequency and the sweep settings, and from `stimulusActive` and the modality. Zapit's reply log is `BonsaiData\BonsaiData_ZapitReplies` too (was `Zapit\`). A Go then Stop before the first beep no longer leaves an expected folder missing.
- **StimulusState is written row by row** (AutoFlush), so the manifest records its full size; it may still show `closed: false`, as the file closes a few ms after the manifest's last write.
- **Never multicast a shared PublishSubject or ReplaySubject from a stream that completes**: the completion passes on and the subject drops every later value. This stopped the trial log after trial 1 (`TrialOutcome`) and the Async log a second after Go (`AsyncEvents`). Take a one-shot value after the multicast, as `TrialEnded` does; tests hold both. av-dome's `pip/main-switchover` had the same `AsyncEvents` bug, fixed there in 9a43c0e (9 Nov 2026) with this repo's test.
- **DO3 cleared however the workflow stops** (11 Nov 2026, for Tom's 10 Nov report; untried on a rig, above): `ClearOutputsOnStop` (src\Extensions) sits on the Behavior board include's output in BehaviorBoards, so a Stop, an error or ExperimentControl's stop disposes the board first (Bonsai.Harp writes Standby and closes the port), then it opens the board's port and writes OutputClear DO3. A final message into `BehaviorBoard-Main.Commands` (Finally, or Concat on completion) cannot do it: every stop disposes rather than completes the graph, and a value sent during disposal is dropped. A Bonsai killed past rig-control's stop deadline still leaves DO3 high until the next start (PulseController clears DO1 to DO3 then).
- **Trial row at the outcome** (audit, 9 Nov 2026): built when WasRewarded is known (TrialOutcome), not 2 s later at TrialEnded as Tom's TrialLog was, so a Stop in between no longer loses a trial whose reward is logged. `Seconds`, `endPositionCm` and `lickCount` are read then (so `lickCount` leaves out the licks while the mouse drinks, round 3).

### Not changed on audit (9 Nov 2026, round 1)

- Trials start 0.5 s after the experiment folder exists, not on Tom's Start button: decided above.
- Zapit's hardware trigger is the rising edge of `zapit.triggerLine`, raised at beep 0 on every trial and every rig; since 10 Nov 2026 held until 1 s after the outcome rather than pulsed for rig.yml's width (5 ms in Tom's; 3000 ms on BLUE-OPTO-2, still av-dome's), see above.
- Background noise fades in from Go, not launch: at most trial 1's first second.
- Waveform paths relative to `src`: av-dome's `src\LocalFiles` resolve the same under rig-control.
- The suggestion not kept across an agent restart, and `test` getting one: edits are kept; harmless.
- Legacy TrialLog picked by mtime, not Tom's ctime: the same file in practice.
- Tom's CueOnsetHarp value kept. (Round 3 found it is the first tone pip and grating frame, after Skip's silent beeps, not beep 0 or the laser onset; the column is now the lab's `stimulusOnsetTime`.)

### Not changed on audit (9 Nov 2026, round 2)

- The suggestion lost on an agent restart (raised again): restarts are rare and a person's edits are kept.
- TaskLogic's unsent `EncoderReset` message (only a gate for BeepTimer) and its disabled "GERION INSERTED FOR TESTING" node: Tom's, node for node.
- The manifest's `ConfigFiles` naming `calibration\MeshMap.csv` and `gammalut.bmp` while rig.yml points the projector at `C:\RigConfigs`, and the spout file read before zeroing may finish: av-dome's own, fixed there or in lib-open-rigs if at all. (BLUE-OPTO-2 has since been switched over, so there both point at `calibration\`.)

### Not changed on audit (9 Nov 2026, round 3)

- The live wheel chart in counts/s, not cm/s: the wheel diameter was undecided then, so a cm/s scale would have been a guess. It is now 75 mm (Decided), so cm/s is possible.
- `BonsaiData_ZapitReplies` not checked by the manifest on its own: it is written only with optogenetics on, and the manifest expects the `BonsaiData` folder, which Trial and Async always fill.

### Not changed on audit (10 Nov 2026, the 9 Nov naming pass)

Fixed the same day: `zapitConfigPath` again (rig-control keeps only fields ending ConfigPath), `truncatedTrialFraction` in trainingStages, the lab-wide `laserCondition`/`laserPowerMw` descriptions in add-names, the old-names test, stale prose; rig-control's names check reads only included workflows. Not changed, each on its merits:

- `toneEndFrequencyHz` and `toneFrequencyHz` beside `visual...`: Pip chose "tone" for the sweep (9 Nov 2026); av-dome's `audio...` is a speaker position, a different thing.
- Laser timing names differ from av-dome's (`laserToStimulusSeconds`, `laserMaxSeconds` against `laserDelaySeconds`, `laserDurationSeconds`): measured from different points (first pip against trial start) and a cap against a timed stimulus, so one name would mislabel one task.
- `trialStructure` shaped unlike av-dome's (one `quiescentSeconds`, `timeLimitSeconds`): the tasks differ (a lick-free wait against a drawn quiescent period; a whole-trial limit against a response window).
- StimulusState's column meanings in the README only, not the manifest: the lab has no schema for stream columns yet; adding one is a platform change.
- Zapit's own `StimDuration` in `ZapitCommand.cs` and `CreateStartStimulus.cs`: Zapit's wire names.

### Not changed on audit (10 Nov 2026, round 1 of 5)

Fixed the same night: `stimulusOnsetTime`'s description said beep 1 always; with optogenetics on the first pip is `laserToStimulusSeconds` after beep 0 (trial-columns.yml, LogStimulus, rig-control's view). Stale "pulse" and "balanced" prose (task.py, the task file, rig.py's `triggerLine`, rig-template, Decided above). Not changed, each on its merits:

- The disabled `HardwareTriggeredOptoTrial?` in OptoOnset: Bonsai passes values through a disabled node (probed: 1, 2, 3 in, 10, 20, 30 out), so the line rises on every trial as described. Tom's node, greyed in the editor, held by a test; av-dome's StartLaser has the same.
- DO3 left high by a Stop mid-trial: ZapitClient stops the laser at exit and the PulseController include clears DO1 to DO3 at every start, so only the masking LEDs stay lit until the next launch. A clear at Stop would need board commands after the workflow has begun stopping. (Fixed 11 Nov 2026: Decided, DO3 cleared however the workflow stops.)
- DO3 held on every trial with `useZapit` off: as before, when it was pulsed 3 s on every trial, so the LEDs mask alike in every experiment.
- rig.yml's `PulseDO3` width still written to the board: unused while DO3 is not pulse-enabled; the mapping keeps the board's settings as av-dome's.
- `InitialiseOptoTrial` has only text tests: probed this round over 2000 trials (the seed reproduces them; no laser-on in the first 20; every trial has a condition; 0.424 laser-on after them at p 0.4; conditions 671/650/679; the same condition as the trial before 0.325, independent being 1/3; a missing file throws). A C# test harness here is more than its rules need now.
- Two `add-names.ps1` copies, and the Zapit C# (`ZapitCommand`, `ZapitClient`, `CreateStartStimulus`) in both repos: one shared script or a lab package is a platform decision for Pip, worth it once a third workflow uses Zapit.

### Not changed on audit (10 Nov 2026, round 2 of 5)

Fixed the same night: endReason's meaning (copied into every manifest) said FreqReached at "stages 1-2", and two descriptions said "stage 6": stages are trainingStages rows from 0 now (rows 0 and 1, Tom's 1 and 2; row 5, Tom's 6); LogStimulus's description named the beep log StimulusLog, not BonsaiData_StimulusState (test added). av-dome now checks Zapit's loaded condition count against the task's file at startup, as this workflow does. Not changed, each on its merits:

- `triggerLine: 0` allowed by the rig model (ge=0, as av-dome's, whose model this one copies field for field) while `DigitalOutputLine` refuses it at start: every rig.yml on the share says 3, and the refusal is loud.
- `Settings_ZapitConditions` and rig-control's kept copy of the Zapit file are two copies: the settings copy is this workflow's record beside its other settings, the kept copy rig-control's rule for every `...ConfigPath` field.
- `CreateStartStimulus`'s description still offers `StartDelay`: a generic Zapit operator, the same in both repos; no task maps it.

### Not changed on audit (10 Nov 2026, round 3 of 5)

Fixed the same night: this file said no rig has this workflow, but every dome has pulled it since 9 Nov 2026 (above); `laserMaxSeconds`'s description and the task file's comment quoted the time limit as 12 s, now `timeLimitSeconds`; the cap's margin and validators were named "stim" (`STIM_DURATION_MARGIN_SEC`, `StimDurationMarginSec`), now "laser max"; `CheckZapitConditionCount`'s comment called the Zapit file the "stimulus config" (tests added). Not changed, each on its merits:

- `rewardSizeUl` on every trial row though fixed for the experiment: a lab-wide column shared with av-dome, where large-reward trials make it vary, so one reader serves both tasks.

### Not changed on audit (10 Nov 2026, round 4 of 5)

Fixed the same night: `ZapitConditions`' ramp-down comment described MATLAB Zapit 1.0.4's `stopOptoStim` shortcut as Zapit's; it now says so, and that the lab's Python Zapit (Coen-Lab/zapit `pointer.stop_opto_stim`) stops the DAQ task 0.2 s after any ramp-down. The check stays, as the rig test runs MATLAB Zapit. Not changed, each on its merits:

- "Required with optogenetics on" and `ConnectZapit`'s "when the task YAML enables optogenetics", though the switch is `useZapit`: plain English for the block's one switch; the schema and the validator messages name `useZapit`.

### Not changed on audit (10 Nov 2026, round 5 of 5)

Fixed the same night: `stage.py` read another workflow's experiment of the same mouse as this task's whenever its data was there (av-dome's trial log has `trialType` visual or auditory and `outcome` too; reproduced: one av-dome experiment, 80 visual Correct at stage 3, suggested Stage 4). Only an ingested one was passed over; now any experiment whose manifest names another repo is (test). `docs\zapit-rig-test.md` step 1 looked for a `BonsaiData_ZapitReplies` folder; it is `BonsaiData\BonsaiData_ZapitReplies_*.csv`. Not changed, each on its merits:

- rig-control's `taskfile.py` docstring names `taskParameters.trialStructure.itiMin` as its example of a dotted field: a generic example matching rig-control's own synthetic tests, not a workflow's field.
