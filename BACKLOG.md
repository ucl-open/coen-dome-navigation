# Backlog

Open work first, then what has been decided. The conversion from `ucl-open/auditory-vr-corridor` is in `git log`. The repo was `coen-av-corridor` until 9 Nov 2026 (renamed on Pip's word); the task file is `config\task-abstract-av-corridor.yml` (Pip).

## Open

### For Pip and Tom to decide

- **Wheel diameter.** Tom's workflow converted encoder counts to cm at 75 mm (the template's default in his inline RunningWheel); every corridor rig's rig.yml says 170 mm. The task file's `wheelDiameterMm: 75` keeps each track the wheel turns the mice trained on. Measure the wheel; if it is 170 mm, either keep 75 (the numbers stay "Tom cm") or set it null and scale the track lengths by 170/75.
- **Reward size.** Tom's valve opened a fixed time (`RigConfigs.cs` ValveOpenTime 80 ms on BLUE-OPTO-1 and GREEN-DOME-2, both marked uncalibrated, 95 ms on BLUE-OPTO-2). Rewards now go through the lab's reward line (`DeliverRewardCommand`, av-dome's): the task's `rewardSizeUl` (2.0) through the left valve's calibration, or one pulse of rig.yml's DO1 width (40 ms on the BLUE-OPTO rigs) per 2 ul when the valve is uncalibrated, so roughly half Tom's opening or less. Calibrate the left valves, or set rewardSizeUl to what Tom's mice got.
- **Lick thresholds.** Tom's workflow left both Lickety Split thresholds at 255/255 (its include's values); this one maps rig.yml's, 0/0 on BLUE-OPTO-1 and in the template. The threshold decides what counts as a lick, so the stage 3+ reward, the wrong-lick limits and WaitForNoLicks. Check what 0 does on the board, then set rig.yml (production config, not this repo) or keep it.
- **Zapit trigger on BLUE-OPTO-2.** Tom's `RigConfigs.cs` had `ZapitHardwareTriggered = false` on every rig ("true once DO3 is cabled to the Zapit DAQ trigger input"), so his laser started on the StartStimulus command at beep 0. This workflow takes rig.yml's `zapit:` block, and BLUE-OPTO-2's says `hardwareTriggered: true` with DO3 a 3000 ms pulse (BLUE-OPTO-1: off). So on BLUE-OPTO-2 Zapit is armed in the inter-trial interval and the DO3 pulse starts the laser: if DO3 is not cabled to Zapit's trigger input there, no laser fires while the trial row gives an `optoCondition`. Check the cable, and whether a 3 s pulse suits Zapit (an edge or a gate), before an optogenetics experiment on BLUE-OPTO-2.
- **IntervalLog dropped.** Tom's IntervalLog (wheel, licks, tone and grating on the PC clock at every change) is not written: the wheel and licks are in the Harp logs and the tone, grating and position are in `StimulusState` every 50 ms on the Harp clock. Bring it back if Tom's analysis needs it.
- **Modality default** is A (gen_session's default); Tom's model said AV.

### Before a rig runs it

- **Data-dictionary names.** 17 trial columns and the `StimulusState` stream are not yet on `K:\config\data-dictionary.json`, so rig-control refuses to launch until they are (checked 9 Nov 2026). `scripts\add-names.ps1` says what it would add; with `-Apply` it saves them through rig-setup's `Save-NamesDictionary`. Production config: on Pip's word.

- **GREEN-DOME-2**, one of Tom's rigs, has no rig.yml on `K:\config\machines`, so it cannot run this (or av-dome) until it has one.
- Not in rig-setup's Repos, so no rig has it; adding it deploys `C:\Github\Coen-Lab\coen-dome-navigation` where ticked. Never on a rig while it records.
- Never run: not headless (it opens BonVision's window and the dome's sound card), not on a rig. The first run is a `test`-mouse experiment through rig-control on a dome, Zapit off.
- No `config\main.layout` yet: save one with the camera windows on a machine with the Spinnaker SDK, then commit it (`.bonsai\Setup.ps1` copies it).
- The old workflow's history: `stage.py` reads the old TrialLogs from each rig's `C:\Github\Coen-Lab\auditory-vr-corridor\Logs` and the project share; check those paths on a rig. A share that does not answer gives no stage (the form says which), so check the rig's account can list `\\128.40.51.157\rigs\staging` and the RDSS path, or every suggestion on it says so.
- When jacuzzi-ingest clears staging (keeping each manifest), `stage.py` gives no stage for a mouse whose experiments are only there ("Only Its Manifest Is Left"): add ingest's destination to its `ROOTS` then, in any order (a folder's data found in any root counts; another workflow's manifests are passed over, by `source.repo`).
- A Zapit ticked with no `zapitConfigPath` is refused on the form (the task schema). A path that names no file on the rig is only found at trial 1, after Go, when the workflow stops: rig-control's `keep_files` passes a missing file over (av-dome relies on that with optogenetics off).
- The wheel: `RigSetup` has its own RunningWheel group in Position mode, because OpenRigs.Devices' include sets Displacement and then differences and accumulates, a speed sample, not a distance (coen-behaviour-box does the same). av-dome's `pip/main-switchover` uses the include: whether its `BoardWheelDistanceCm` matters there, and fixing the include in lib-open-rigs, are for Pip.

### Smaller

- A Go then Stop before trial 1's first beep leaves no `StimulusState\` folder (LogData makes it with its first row), and the manifest expects one, so rig-sync holds that experiment with an alarm.
- No current-trial metrics: av-dome's Status group (ReportMetric) was not kept, so rig-control's Current Trial line is blank until a row lands. Report the trial number and stage.

## Decided

- **Base.** av-dome `pip/main-switchover` for everything that is not the task (rig.yml, devices, ExperimentControl, spout commands, reward lines, logging, manifest, live view), because every corridor rig is one of its rigs; Tom's task groups kept node for node.
- **Task file.** Read at Go from `TaskPath` (YAML) instead of `session-schemas\...json` at start; laid out as av-dome's since 9 Nov 2026 (Pip): `trainingStages` rows from 0 (Tom's stages 1 to 6 are rows 0 to 5) say what each stage does, where the workflow tested the stage number (reward zone and size, lick or zone entry, wrong-lick limit, punishing a wrong-zone ending, the quiescent wait, time limit, end of track, track scale, frequency drift, truncation); the stage is the TrainingStage setting, which `suggest-stage` fills; `trialStructure` holds the ITI (now drawn evenly between itiMin and itiMax, where Tom's drew 0 or 1 s), the 12 s time limit and the track lengths, `punishment` the timeout and error tone, and `rngSeed` seeds every draw. centerFreq, rewardWindowSize and engagement are gone (the zones are written in the rows). Subject and session come from rig-control (`LogConfig` dropped).
- **Waveforms** made once and committed; `CheckWaveforms` stops at Go if the task's sweep or amplitude is not the one they were made for. Tones keep Tom's loudness (written at amplitude and scaled by it again).
- **Mixer** is av-dome's (rig.yml's device, the 3 s start delay before the dome's sound card).
- **Zapit** host, port, hardware triggering and trigger line from rig.yml's `zapit:`.
- **The laser is started as av-dome starts it and lasts the trial** (Pip, 9 Nov 2026): the task's `optogenetics` block is av-dome's field for field (`stimDelay` and `excludeRepeats` added, `leadTime` dropped; `excludeRepeats` does nothing, as this task has no repeat trials). Each stimulated trial's StartStimulus carries `stimDelay` (StartDelay) and `stimDuration`; `zapit.triggerLine` is pulsed at every trial's start (beep 0), opto or not and on every rig, as av-dome's StartLaser does; Zapit starts the laser `stimDelay` later. Unlike av-dome, StopStimulus ends it at the trial's outcome, so it covers the whole trial, as Tom's did; `stimDuration` (12 s, Tom's) only caps it. Before, the laser also came on `leadTime` (0.1 s) before the cue, which was delayed by it on every trial of an optogenetics experiment; the cue now starts at beep 1 in every experiment. av-dome arms and pulses 0.1 s apart and runs the trial 0.5 s later; here arming is in the ITI and waits for Zapit's answer, and the pulse is at beep 0.
- **The 12 s cap and slow trials.** Rows 0 to 2 have no time limit (timeLimit; row 3 on, trialStructure.closedLoopDuration 12 s), so a trial slower than `stimDuration` loses its laser before the outcome. Raise `stimDuration` if trials at rows 0-2 run longer and Zapit takes a longer stimulus.
- **Trials start** once the experiment folder exists and the tones are loaded, 0.5 s later.
- **Trial log** is the lab's `BonsaiData_Trial`, one row per trial as Tom's TrialLog (built at the outcome, below); `outcome` is Correct/Incorrect/NoResponse so rig-control's performance is Tom's success rate. Names follow av-dome where they can (Pip, 9 Nov 2026): `trainingStage`, `outcome`'s values, `trialType` in words as av-dome's (auditory/visual/audiovisual: the task's modality; av-dome's trialType became text the same day), the `audio`/`visual` prefixes (`audioEndFrequencyHz`, `visualEndSpatialFrequencyCpd`), `truncationFraction`, the `StimulusState` stream; `timedOut` and `hitEndOfTrack` dropped (endReason says both). Later that day only what changes from trial to trial stayed (Pip): `trainingStage`, `rewardFloorHz`, `rewardCeilingHz` and `audioAmplitude` dropped (fixed for the experiment, in Settings_Task; `suggest-stage` reads the stage there), `optoStimulated` dropped (it was `optoCondition` > 0), and `punished` split into `punishSound` and `punishTimeoutSeconds`, so other kinds of punishment can follow.
- **Tom's SessionStats and EngagementStats** and the GUI are rig-control's (its view), not the workflow's. Tom's model's `engagement` block, unused, was dropped on 9 Nov 2026.
- **Devices.** Both Lickety Splits and all three cameras are opened and recorded, as av-dome does, because the manifest expects a folder for each device in rig.yml.
- **Tom's mice's spout positions are read where Tom's workflow keeps them** (9 Nov 2026, chosen over copying them): Tom's workflow reads and saves each mouse's spouts at `C:\RigConfigs\LickSpoutPositions\<ID>.csv`; this one (av-dome's convention) saves them at `C:\Coen-Lab-Local\RigConfigs\<MACHINE>\calibration\LickSpoutPositions\<ID>.csv`, a folder BLUE-OPTO-2 never had. `SpoutPositionsToRead` reads that file when the mouse has one, else Tom's (same format: five integers, Motor1 to Motor5, no header); saves go to `calibration\` only, so Tom's files are never written, and the two can differ once a mouse is saved here. A mouse with neither still gets rig.yml's `lickSpoutDefaultPosition` (all 0 on BLUE-OPTO-2), not Tom's In positions (3550, 5500, 4700, 2150, 950), after zeroing. The manifest hashes both files, and MeshMap.csv and gammalut.bmp where rig.yml names them (`C:\RigConfigs`); change those two with rig.yml when a rig switches over.
- **A shared PublishSubject or ReplaySubject is never multicast from a stream that completes** (9 Nov 2026): a MulticastSubject passes on its source's completion, and a completed subject drops every later value. `TrialOutcome` (completed at the end of trial 1: one trial row per experiment) and `AsyncEvents` (completed by RngSeed's Take(1) a second after Go: no reward or water logged) were. A one-shot sender takes its value after the multicast, as `TrialEnded` always did; BehaviorSubjects are not completed this way (checked on Bonsai 2.9). Tests hold both.
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
