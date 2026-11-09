# Backlog

Open work first, then what has been decided. The conversion from `ucl-open/auditory-vr-corridor` is in `git log`. The repo was `coen-av-corridor` until 9 Nov 2026 (renamed on Pip's word); the task file is `config\task-abstract-av-corridor.yml` (Pip).

## Open

### For Pip and Tom to decide

- **Wheel diameter.** Tom's workflow converted encoder counts to cm at 75 mm (the template's default in his inline RunningWheel); every corridor rig's rig.yml says 170 mm. The task file's `wheelDiameterMm: 75` keeps each track the wheel turns the mice trained on. Measure the wheel; if it is 170 mm, either keep 75 (the numbers stay "Tom cm") or set it null and scale the track lengths by 170/75.
- **Reward size.** `rewardSizeUl` is 3.0 (Tom, zapit-v2). Rewards go through the lab's reward line (`DeliverRewardCommand`, av-dome's): through the left valve's calibration, or, uncalibrated, one pulse of rig.yml's DO1 width (40 ms on the BLUE-OPTO rigs) per 2 ul, so two 40 ms pulses for 3 ul - about Tom's 80 ms opening, never measured in ul. Calibrate the left valves so 3 ul is 3 ul.
- **Lick thresholds.** Tom's workflow left both Lickety Split thresholds at 255/255 (its include's values); this one maps rig.yml's, 0/0 on BLUE-OPTO-1 and in the template. The threshold decides what counts as a lick, so the stage 3+ reward, the wrong-lick limits and WaitForNoLicks. Check what 0 does on the board, then set rig.yml (production config, not this repo) or keep it.
- **Zapit trigger cable.** rig.yml's `zapit:` block sets hardware triggering: BLUE-OPTO-2 `hardwareTriggered: true` (DO3, a 3000 ms pulse; Zapit starts on the rising edge, so the width does not matter), BLUE-OPTO-1 off. With hardware triggering, if DO3 is not cabled to Zapit's trigger input no laser fires while the trial row says `optoStimulated`: check with a photodiode (docs/zapit-rig-test.md). A rig without hardware triggering refuses an optogenetics experiment with any mouse but `test` (zapit-v2).
- **IntervalLog dropped.** Tom's IntervalLog (wheel, licks, tone and grating on the PC clock at every change) is not written: the wheel and licks are in the Harp logs and the tone, grating and position are in `StimulusState` every 50 ms on the Harp clock. Bring it back if Tom's analysis needs it.
- **Modality default** is A (gen_session's default); Tom's model said AV.

### Before a rig runs it

- **Data-dictionary names.** 17 trial columns and the `StimulusState` stream are not yet on `K:\config\data-dictionary.json`, so rig-control refuses to launch until they are (checked 9 Nov 2026). `scripts\add-names.ps1` says what it would add; with `-Apply` it saves them through rig-setup's `Save-NamesDictionary`. Production config: on Pip's word.

- **Tom's mice's spout positions.** Tom's workflow read and saved each mouse's spouts at `C:\RigConfigs\LickSpoutPositions\<ID>.csv`; this one (av-dome's convention) uses `C:\Coen-Lab-Local\RigConfigs\<MACHINE>\calibration\LickSpoutPositions\<ID>.csv`. Without a file it falls back to rig.yml's `lickSpoutDefaultPosition` (all 0 on BLUE-OPTO-2), not Tom's In positions (3550, 5500, 4700, 2150, 950), and moves the spouts there after zeroing. The format is the same (five integers, Motor1 to Motor5, no header), so on each corridor rig copy `C:\RigConfigs\LickSpoutPositions\*.csv` into the new folder, never over a file av-dome's mice already have there.
- **GREEN-DOME-2**, one of Tom's rigs, has no rig.yml on `K:\config\machines`, so it cannot run this (or av-dome) until it has one.
- Not in rig-setup's Repos, so no rig has it; adding it deploys `C:\Github\Coen-Lab\coen-dome-navigation` where ticked. Never on a rig while it records.
- Never run: not headless (it opens BonVision's window and the dome's sound card), not on a rig. The first run is a `test`-mouse experiment through rig-control on a dome, Zapit off.
- No `config\main.layout` yet: save one with the camera windows on a machine with the Spinnaker SDK, then commit it (`.bonsai\Setup.ps1` copies it).
- The old workflow's history: `stage.py` reads the old TrialLogs from each rig's `C:\Github\Coen-Lab\auditory-vr-corridor\Logs` and the project share; check those paths on a rig. A share that does not answer gives no stage (the form says which), so check the rig's account can list `\\128.40.51.157\rigs\staging` and the RDSS path, or every suggestion on it says so.
- When jacuzzi-ingest clears staging (keeping each manifest), `stage.py` gives no stage for a mouse whose experiments are only there ("Only Its Manifest Is Left"): add ingest's destination to its `ROOTS` then, in any order (a folder's data found in any root counts; another workflow's manifests are passed over, by `source.repo`).
- A Zapit ticked with no `zapitConfigPath` is refused on the form (the task schema). A path that names no file on the rig stops the experiment at Go, before trial 1: the Zapit condition-count check reads the file when Zapit connects (zapit-v2).
- The wheel: `RigSetup` has its own RunningWheel group in Position mode, because OpenRigs.Devices' include sets Displacement and then differences and accumulates, a speed sample, not a distance (coen-behaviour-box does the same). av-dome's `pip/main-switchover` uses the include: whether its `BoardWheelDistanceCm` matters there, and fixing the include in lib-open-rigs, are for Pip.

### Smaller

- A Go then Stop before trial 1's first beep leaves no `StimulusState\` folder (LogData makes it with its first row), and the manifest expects one, so rig-sync holds that experiment with an alarm.
- No current-trial metrics: av-dome's Status group (ReportMetric) was not kept, so rig-control's Current Trial line is blank until a row lands. Report the trial number and stage.

## Decided

- **Base.** av-dome `pip/main-switchover` for everything that is not the task (rig.yml, devices, ExperimentControl, spout commands, reward lines, logging, manifest, live view), because every corridor rig is one of its rigs; Tom's task groups kept node for node.
- **Task file.** Read at Go from `TaskPath` (YAML) instead of `session-schemas\...json` at start; `thresholdFrequencies` written out in `config\task-abstract-av-corridor.yml` because rig-control does not run the pydantic model. Subject and session come from rig-control (`LogConfig` dropped).
- **Waveforms** made once and committed; `CheckWaveforms` stops at Go if the task's sweep or amplitude is not the one they were made for. Tones keep Tom's loudness (written at amplitude and scaled by it again).
- **Mixer** is av-dome's (rig.yml's device, the 3 s start delay before the dome's sound card).
- **Zapit** host, port, hardware triggering and trigger line from rig.yml's `zapit:`.
- **The laser** (zapit-v2, Tom and Pip, 9 Nov 2026; ZAPIT-V2-CHANGES.md has the why): av-dome's `optogenetics` fields plus `leadTime`; `stimDelay` must be 0 and `excludeRepeats` does nothing here. Zapit only ever gets continuous stimuli (it ignores StopStimulus for a timed one): ArmZapit arms it once the ITI has ended (hardware-triggered rigs), the trigger line is pulsed at beep 0 on every trial as av-dome's StartLaser, and LaserOnUntilOutcome stops it at the outcome or after `stimDuration`. Every trial of an optogenetics experiment is sent a condition, laser-on or sham (laser off), so galvos and masking light are the same on every trial; p 0.4 after 20 sham warmup trials. The first tone pip comes `leadTime` (0.1 s) after beep 0 in an optogenetics experiment, laser-on and sham alike.
- **The cap and slow trials.** `stimDuration` (12.5 s) must be at least 12 s + `leadTime` + 0.25 s, so it never ends a timeout trial's laser early (task.py's model and InitialiseOptoTrial at trial 1 check it; rig-control's schema only checks that it is above 0). Stages 1 to 3 have no time limit, so a trial slower than `stimDuration` loses its laser before the outcome.
- **Trials start** once the experiment folder exists and the tones are loaded, 0.5 s later.
- **Trial log** is the lab's `BonsaiData_Trial`, one row per trial as Tom's TrialLog (built at the outcome, below); `outcome` is Correct/Incorrect/NoResponse so rig-control's performance is Tom's success rate. Names follow av-dome where they can (Pip, 9 Nov 2026): `trainingStage`, `outcome`'s values, `trialType` in words as av-dome's (auditory/visual/audiovisual: the task's modality; av-dome's trialType became text the same day), the `audio`/`visual` prefixes (`audioEndFrequencyHz`, `visualEndSpatialFrequencyCpd`), `truncationFraction`, the `StimulusState` stream; `timedOut` and `hitEndOfTrack` dropped (endReason says both).
- **Tom's SessionStats and EngagementStats** and the GUI are rig-control's (its view), not the workflow's. The task's `engagement` block is Tom's model's, unused.
- **Devices.** Both Lickety Splits and all three cameras are opened and recorded, as av-dome does, because the manifest expects a folder for each device in rig.yml.
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
