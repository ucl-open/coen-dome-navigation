# Backlog

Open work first, then what has been decided. The conversion from `ucl-open/auditory-vr-corridor`
is in `git log`.

## Open

### For Pip and Tom to decide

- **Wheel diameter.** Tom's workflow converted encoder counts to cm at 75 mm (the template's
  default in his inline RunningWheel); every corridor rig's rig.yml says 170 mm. The task file's
  `wheelDiameterMm: 75` keeps each track the wheel turns the mice trained on. Measure the wheel; if
  it is 170 mm, either keep 75 (the numbers stay "Tom cm") or set it null and scale the track
  lengths by 170/75.
- **Reward size.** Tom's valve opened a fixed time (`RigConfigs.cs` ValveOpenTime 80 ms on
  BLUE-OPTO-1 and GREEN-DOME-2, both marked uncalibrated, 95 ms on BLUE-OPTO-2). Rewards now go through the lab's reward line
  (`DeliverRewardCommand`, av-dome's): the task's `rewardSizeUl` (2.0) through the left valve's
  calibration, or one pulse of rig.yml's DO1 width (40 ms on the BLUE-OPTO rigs) per 2 ul when the
  valve is uncalibrated, so roughly half Tom's opening or less. Calibrate the left valves, or set
  rewardSizeUl to what Tom's mice got.
- **Lick thresholds.** Tom's workflow left both Lickety Split thresholds at 255/255 (its include's
  values); this one maps rig.yml's, 0/0 on BLUE-OPTO-1 and in the template. The threshold decides
  what counts as a lick, so the stage 3+ reward, the wrong-lick limits and WaitForNoLicks. Check
  what 0 does on the board, then set rig.yml (production config, not this repo) or keep it.
- **IntervalLog dropped.** Tom's IntervalLog (wheel, licks, tone and grating on the PC clock at
  every change) is not written: the wheel and licks are in the Harp logs and the tone, grating and
  position are in `Stimulus` every 50 ms on the Harp clock. Bring it back if Tom's analysis needs
  it.
- **Modality default** is A (gen_session's default); Tom's model said AV.
- **Data-dictionary names.** The 20 new trial columns and the `Stimulus` stream are not yet on
  `K:\config\data-dictionary.json`, so rig-control refuses to launch until they are: add them from
  rig-setup's Repos page, or with the script this conversion's session used (`add-names.ps1`, which
  calls rig-setup's `Save-NamesDictionary`).

### Before a rig runs it

- Not in rig-setup's Repos, so no rig has it; adding it deploys `C:\Github\Coen-Lab\coen-av-corridor`
  where ticked. Never on a rig while it records.
- Never run: not headless (it opens BonVision's window and the dome's sound card), not on a rig.
  The first run is a `test`-mouse experiment through rig-control on a dome, Zapit off.
- No `config\main.layout` yet: save one with the camera windows on a machine with the Spinnaker
  SDK, then commit it (`.bonsai\Setup.ps1` copies it).
- Old sessions' history: `stage.py` reads the old TrialLogs from each rig's
  `C:\Github\Coen-Lab\auditory-vr-corridor\Logs` and the project share; check those paths on a rig.
  A share that does not answer gives no stage (the form says which), so check the rig's account can
  list `\\128.40.51.157\rigs\staging` and the RDSS path, or every suggestion on it says so.
- The wheel: `RigSetup` has its own RunningWheel group in Position mode, because OpenRigs.Devices'
  include sets Displacement and then differences and accumulates, a speed sample, not a distance
  (coen-behaviour-box does the same). av-dome's `pip/main-switchover` uses the include: whether its
  `BoardWheelDistanceCm` matters there, and fixing the include in lib-open-rigs, are for Pip.

### Smaller

- A Go then Stop before trial 1's first beep leaves no `Stimulus\` folder (LogData makes it with its
  first row), and the manifest expects one, so rig-sync holds that experiment with an alarm.
- No current-trial metrics: av-dome's Status group (ReportMetric) was not kept, so rig-control's
  Current Trial line is blank until a row lands. Report the trial number and stage.

## Decided

- **Base.** av-dome `pip/main-switchover` for everything that is not the task (rig.yml, devices,
  ExperimentControl, spout commands, reward lines, logging, manifest, live view), because every
  corridor rig is one of its rigs; Tom's task groups kept node for node.
- **Task file.** Read at Go from `TaskPath` (YAML) instead of `session-schemas\...json` at start;
  `thresholdFrequencies` written out in `config\task.yml` because rig-control does not run the
  pydantic model. Subject and session come from rig-control (`LogConfig` dropped).
- **Waveforms** made once and committed; `CheckWaveforms` stops at Go if the task's sweep or
  amplitude is not the one they were made for. Tones keep Tom's loudness (written at amplitude and
  scaled by it again).
- **Mixer** is av-dome's (rig.yml's device, the 3 s start delay before the dome's sound card).
- **Zapit** host, port, hardware triggering and trigger line from rig.yml's `zapit:`; the laser
  onset pulses `zapit.triggerLine` through the lab's PulseController (`TriggerPulse`).
- **Trials start** once the experiment folder exists and the tones are loaded, 0.5 s later.
- **Trial log** is the lab's `BonsaiData_Trial`, built when TrialEnded turns true as Tom's TrialLog
  was; `outcome` is Rewarded/Unrewarded so rig-control's performance is Tom's success rate.
- **Session and engagement statistics** and the GUI are rig-control's (its view), not the
  workflow's.
- **Devices.** Both Lickety Splits and all three cameras are opened and recorded, as av-dome does,
  because the manifest expects a folder for each device in rig.yml.
- **Trial row at the outcome** (audit, 9 Nov 2026): built when WasRewarded is known (TrialOutcome),
  not 2 s later at TrialEnded as Tom's TrialLog was, so a Stop in between no longer loses a trial
  whose reward is logged. `Seconds` and `endPositionCm` are read then.

### Not changed on audit (9 Nov 2026, round 1)

- Trials start 0.5 s after the experiment folder exists, not on Tom's Start button: decided above.
- Zapit's hardware trigger is a pulse on `zapit.triggerLine`: same 5 ms width; off on BLUE-OPTO-1.
- Background noise fades in from Go, not launch: at most trial 1's first second.
- Waveform paths relative to `src`: av-dome's `src\LocalFiles` resolve the same under rig-control.
- The suggestion not kept across an agent restart, and `test` getting one: edits are kept; harmless.
- Legacy TrialLog picked by mtime, not Tom's ctime: the same file in practice.
- `cueOnsetTime` is beep 0 (the laser onset in an optogenetics session), as Tom's CueOnsetHarp:
  value kept, description corrected.
