# Backlog

Open work first, then what has been decided. The conversion from `ucl-open/auditory-vr-corridor` is in `git log`. The repo was `coen-av-corridor` until 9 Nov 2026 (renamed on Pip's word); the task file is `config\task-abstract-av-corridor.yml` (Pip).

> [!IMPORTANT]
> **Pip: two names were added to the lab's names list without your OK** (9 Nov 2026, `scripts\add-names.ps1 -Apply`, so zapit-v2 could be tested): `optoStimulated` (trial column) and `Settings_ZapitStimConfig` (stream).
> You dropped `optoStimulated` as always `optoCondition > 0`. On zapit-v2 it is not: sham trials (galvos and masking light, laser off) also get a Zapit condition, so `optoCondition` is the location presented and `optoStimulated` whether the laser was on. That changes `optoCondition`'s meaning from av-dome's ("the condition stimulated, 0 when none"). Either (a) keep both and define `optoCondition` lab-wide as the condition presented (no change for av-dome, which has no shams), or (b) drop `optoStimulated` and log shams as `optoCondition` 0 (losing the sham location).

## Open

### For Pip (found 9 Nov 2026 on BLUE-OPTO-2, with Claude)

- **av-dome's Async log stops a second after Go** (fixed here in 9d04d77). In `pip/main-switchover`, the RngSeed sender in `ExperimentLogs` has its `Take(1)` before `MulticastSubject AsyncEvents`; a MulticastSubject passes on its source's completion, so the shared `AsyncEvents` subject completes after the seed and nothing later is logged (no RewardDelivered rows; rig-control counts 0 water). Seen here on BLUE-OPTO-2; av-dome's sender is the same (not run there). Fix: move the `Take(1)` after the multicast (edges `21->22, 22->23` become `21->23, 25->22`); `tests\test_workflow.py::test_no_async_event_sender_completes_async_events` checks it.
- **Calibration folder switch-over** (rig-setup's): BLUE-OPTO-2 done by hand (MeshMap.csv, gammalut.bmp and LickSpoutPositions copied from `C:\RigConfigs` into `calibration\`, rig.yml's `projectionCalibration` pointed there). BLUE-OPTO-1 and GREEN-DOME-2 still need it; whether rig-setup should do the copy is yours.
- **rig.yml's wheel diameter** is 170 mm on BLUE-OPTO-1, GREEN-DOME-1 and GREEN-DOME-2; the wheels are 75 mm (BLUE-OPTO-2 corrected).

### For Pip and Tom to decide

- **Calibrate the left valves, then set `rewardSizeUl` back to 3.0.** Tom's mice got 3 ul as one fixed opening (42 ms on BLUE-OPTO-2 in his RigLogs; 45 ms on BLUE-OPTO-1, 65 ms on the GREEN-DOMEs). An uncalibrated valve here is taken as 2 ul per pulse of DO1's width, so 3 ul would be two openings. Until calibrated, `rewardSizeUl` is 2.0: one opening of DO1's width (set to 42 ms on BLUE-OPTO-2), logged as 2 ul. Set each other rig's DO1 width to Tom's before it runs this uncalibrated.
- **Lick thresholds.** Tom's workflow left both Lickety Split thresholds at 255/255 (its include's values); this one maps rig.yml's, 0/0 on BLUE-OPTO-1, BLUE-OPTO-2 and in the template. The threshold decides what counts as a lick, so the stage 3+ reward, the wrong-lick limits and WaitForNoLicks. Check what 0 does on the board (a run with nobody at the spouts: any lick logged is false), then set rig.yml (production config, not this repo) or keep it.
- **Zapit trigger cable.** rig.yml's `zapit:` block sets hardware triggering: BLUE-OPTO-2 `hardwareTriggered: true` (DO3, a 3000 ms pulse; Zapit starts on the rising edge, and a 3 s pulse can swallow the next one when trials start less than 3 s apart, as fast early-stage trials can, so a short pulse such as the template's 5 ms is safer for Zapit), BLUE-OPTO-1 off. With hardware triggering, if DO3 is not cabled to Zapit's trigger input no laser fires while the trial row says `optoStimulated`: check with a photodiode (docs/zapit-rig-test.md). A rig without hardware triggering refuses an optogenetics experiment with any mouse but `test` (zapit-v2).
- **IntervalLog dropped.** Tom's IntervalLog (wheel, licks, tone and grating on the PC clock at every change) is not written: the wheel and licks are in the Harp logs and the tone, grating and position are in `StimulusState` every 50 ms on the Harp clock. Bring it back if Tom's analysis needs it.
- **Modality is set per mouse on the form** (the task file's default is auditory): TC001 to TC004 auditory, TC005 and TC006 visual. The stage suggestion is per modality.

### Before a rig runs it

- **GREEN-DOME-2**, one of Tom's rigs, had no rig.yml on `K:\config\machines`; it has one now (`GREEN-DOME-2_rig.yml`, 9 Nov 2026). Check it has `rewardValves` and this workflow's devices before it runs this.
- Not in rig-setup's Repos, so no rig has it; adding it deploys `C:\Github\Coen-Lab\coen-dome-navigation` where ticked. Never on a rig while it records.
- **First runs** (BLUE-OPTO-2, 9 Nov 2026, `test` mouse, optogenetics off) found three logging bugs, all fixed (Decided). Not yet run: one of Tom's mice, optogenetics, a dome.
- **Tom's mice move between BLUE-OPTO-1, BLUE-OPTO-2, GREEN-DOME-1 and GREEN-DOME-2**, so each needs a rig.yml, this clone and the calibration folder (above). A mouse without a spout file gets rig.yml's `lickSpoutDefaultPosition` (all 0 on BLUE-OPTO-2), not Tom's In positions (3550, 5500, 4700, 2150, 950): set those as the default on the corridor rigs, or save each mouse's positions in its first experiment.
- No `config\main.layout` yet: save one with the camera windows on a machine with the Spinnaker SDK, then commit it (`.bonsai\Setup.ps1` copies it).
- The old workflow's history: `stage.py` reads the old TrialLogs from each rig's `C:\Github\Coen-Lab\auditory-vr-corridor\Logs` and the RDSS share. On BLUE-OPTO-2 the local path does not exist, but the share answers with every TC mouse's history (9 Nov 2026); check the share from each other rig's account, or every suggestion there says it has no history.
- When jacuzzi-ingest clears staging (keeping each manifest), `stage.py` gives no stage for a mouse whose experiments are only there ("Only Its Manifest Is Left"): add ingest's destination to its `ROOTS` then, in any order (a folder's data found in any root counts; another workflow's manifests are passed over, by `source.repo`).
- A Zapit ticked with no `zapitConfigPath` is refused on the form (the task schema). A path that names no file on the rig stops the experiment at Go, before trial 1: the Zapit condition-count check reads the file when Zapit connects (zapit-v2).
- The wheel: `RigSetup` has its own RunningWheel group in Position mode, because OpenRigs.Devices' include sets Displacement and then differences and accumulates, a speed sample, not a distance (coen-behaviour-box does the same). av-dome's `pip/main-switchover` uses the include: whether its `BoardWheelDistanceCm` matters there, and fixing the include in lib-open-rigs, are for Pip.

### Smaller

- A Go then Stop before trial 1's first beep leaves no `StimulusState\` folder (LogData makes it with its first row), and the manifest expects one, so rig-sync holds that experiment with an alarm.
- No current-trial metrics: av-dome's Status group (ReportMetric) was not kept, so rig-control's Current Trial line is blank until a row lands. Report the trial number and stage.

## Decided

- **Wheel: 75 mm** (measured, 9 Nov 2026), as Tom's workflow converted it (75 mm, 4096 counts a turn); the task file's `wheelDiameterMm: 75` overrides rig.yml's, so the tracks are as long as the mice trained on.
- **Data-dictionary names**: this workflow's trial columns and streams are on `K:\config\data-dictionary.json`; `scripts\add-names.ps1` adds new ones.
- **Base.** av-dome `pip/main-switchover` for everything that is not the task (rig.yml, devices, ExperimentControl, spout commands, reward lines, logging, manifest, live view), because every corridor rig is one of its rigs; Tom's task groups kept node for node.
- **Task file.** Read at Go from `TaskPath` (YAML) instead of `session-schemas\...json` at start; laid out as av-dome's since 9 Nov 2026 (Pip): `trainingStages` rows from 0 (Tom's stages 1 to 6 are rows 0 to 5) say what each stage does, where the workflow tested the stage number (reward zone and size, lick or zone entry, wrong-lick limit, punishing a wrong-zone ending, the quiescent wait, time limit, end of track, track scale, frequency drift, truncation); the stage is the TrainingStage setting, which `suggest-stage` fills; `trialStructure` holds the ITI (now drawn evenly between itiMin and itiMax, where Tom's drew 0 or 1 s), the 12 s time limit and the track lengths, `punishment` the timeout and error tone, and `rngSeed` seeds every draw. centerFreq, rewardWindowSize and engagement are gone (the zones are written in the rows). Subject and session come from rig-control (`LogConfig` dropped).
- **Waveforms** made once and committed; `CheckWaveforms` stops at Go if the task's sweep or amplitude is not the one they were made for. Tones keep Tom's loudness (written at amplitude and scaled by it again).
- **Mixer** is av-dome's (rig.yml's device, the 3 s start delay before the dome's sound card).
- **Zapit** host, port, hardware triggering and trigger line from rig.yml's `zapit:`.
- **The laser** (zapit-v2, Tom and Pip, 9 Nov 2026; ZAPIT-V2-CHANGES.md has the why): av-dome's `optogenetics` fields plus `leadTime` (0.05-0.2 s); `stimDelay` must be 0 and `excludeRepeats` does nothing here. Zapit only ever gets continuous stimuli (it ignores StopStimulus for a timed one): ArmZapit arms it once the ITI has ended (hardware-triggered rigs), the trigger line is pulsed at beep 0 on every trial as av-dome's StartLaser, and LaserOnUntilOutcome stops it at the outcome or after `stimDuration`. Every trial of an optogenetics experiment is sent a condition, laser-on or sham (laser off), so galvos and masking light are the same on every trial; p 0.4 after 20 sham warmup trials. The first tone pip comes `leadTime` (0.1 s) after beep 0 in an optogenetics experiment, laser-on and sham alike.
- **The cap and slow trials.** `stimDuration` (12.5 s) must be at least `trialStructure.closedLoopDuration` (12 s) + `leadTime` + 0.25 s, so it never ends a timed-out trial's laser early (task.py's model and InitialiseOptoTrial at trial 1 check it; rig-control's schema only checks that it is above 0). Rows 0 to 2 have no time limit (timeLimit), so a trial slower than `stimDuration` loses its laser before the outcome.
- **Trials start** once the experiment folder exists and the tones are loaded, 0.5 s later.
- **Trial log** is the lab's `BonsaiData_Trial`, one row per trial as Tom's TrialLog (built at the outcome, below); `outcome` is Correct/Incorrect/NoResponse so rig-control's performance is Tom's success rate. Names follow av-dome where they can (Pip, 9 Nov 2026): `trainingStage`, `outcome`'s values, `trialType` in words as av-dome's (auditory/visual/audiovisual: the task's modality; av-dome's trialType became text the same day), the `audio`/`visual` prefixes (`audioEndFrequencyHz`, `visualEndSpatialFrequencyCpd`), `truncationFraction`, the `StimulusState` stream; `timedOut` and `hitEndOfTrack` dropped (endReason says both). Later that day only what changes from trial to trial stayed (Pip): `trainingStage`, `rewardFloorHz`, `rewardCeilingHz` and `audioAmplitude` dropped (fixed for the experiment, in Settings_Task; `suggest-stage` reads the stage there), `optoStimulated` dropped (it was `optoCondition` > 0; back in zapit-v2, whose sham trials have a condition too), and `punished` split into `punishSound` and `punishTimeoutSeconds`, so other kinds of punishment can follow.
- **Tom's SessionStats and EngagementStats** and the GUI are rig-control's (its view), not the workflow's. Tom's model's `engagement` block, unused, was dropped on 9 Nov 2026.
- **Devices.** Both Lickety Splits and all three cameras are opened and recorded, as av-dome does, because the manifest expects a folder for each device in rig.yml.
- **One calibration folder per rig, as av-dome's** (9 Nov 2026): `C:\Coen-Lab-Local\RigConfigs\<MACHINE>\calibration\` beside rig.yml (MeshMap.csv, gammalut.bmp, LickSpoutPositions), read by every workflow on the rig. Production's `C:\RigConfigs` stays for Tom's auditory-vr-corridor and av-dome's ActiveExperiments; at switch-over its files are copied, never over an existing one. The copies are separate from then on, so adjust a mouse's spout positions in one workflow only.
- **StimulusState is written row by row** (AutoFlush), so the manifest records its full size; it may still show `closed: false`, as the file closes a few ms after the manifest's last write.
- **Never multicast a shared PublishSubject or ReplaySubject from a stream that completes**: the completion passes on and the subject drops every later value. This stopped the trial log after trial 1 (`TrialOutcome`) and the Async log a second after Go (`AsyncEvents`). Take a one-shot value after the multicast, as `TrialEnded` does; tests hold both.
- **Trial row at the outcome** (audit, 9 Nov 2026): built when WasRewarded is known (TrialOutcome), not 2 s later at TrialEnded as Tom's TrialLog was, so a Stop in between no longer loses a trial whose reward is logged. `Seconds`, `endPositionCm` and `lickCount` are read then (so `lickCount` leaves out the licks while the mouse drinks, round 3).

### Not changed on audit (9 Nov 2026, round 1)

- Trials start 0.5 s after the experiment folder exists, not on Tom's Start button: decided above.
- Zapit's hardware trigger is a pulse on `zapit.triggerLine`, its width rig.yml's (5 ms in Tom's; 3000 ms on BLUE-OPTO-2, see above). (Since the laser follows av-dome, the line is pulsed on every trial and every rig.)
- Background noise fades in from Go, not launch: at most trial 1's first second.
- Waveform paths relative to `src`: av-dome's `src\LocalFiles` resolve the same under rig-control.
- The suggestion not kept across an agent restart, and `test` getting one: edits are kept; harmless.
- Legacy TrialLog picked by mtime, not Tom's ctime: the same file in practice.
- Tom's CueOnsetHarp value kept. (Round 3 found it is the first tone pip and grating frame, after Skip's silent beeps, not beep 0 or the laser onset; the column is now the lab's `stimulusOnsetTime`.)

### Not changed on audit (9 Nov 2026, round 2)

- The suggestion lost on an agent restart (raised again): restarts are rare and a person's edits are kept.
- TaskLogic's unsent `EncoderReset` message (only a gate for BeepTimer) and its disabled "GERION INSERTED FOR TESTING" node: Tom's, node for node.
- The manifest's `ConfigFiles` naming `calibration\MeshMap.csv` and `gammalut.bmp` while rig.yml points the projector at `C:\RigConfigs`, and the spout file read before zeroing may finish: av-dome's own, fixed there or in lib-open-rigs if at all.

### Not changed on audit (9 Nov 2026, round 3)

- The live wheel chart in counts/s, not cm/s: the wheel diameter is undecided (above), so a cm/s scale would be a guess.
- `Zapit\` not in the manifest's `ExpectedFiles`: it is written only with optogenetics on, and an expected folder that is missing holds the experiment in rig-sync.
