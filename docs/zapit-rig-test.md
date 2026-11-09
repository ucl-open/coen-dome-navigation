# Zapit: rig checks before the first optogenetics experiment

Use a `test` mouse throughout; keep the laser shutter closed (or 0 mW) until step 5.

## Before you start

- Zapit (MATLAB): real DAQ, not `start_zapit('simulated', true)` (its simulated DAQ hides stop problems); `tcpServer.enable: true`;
  calibrated; the stimulus config named in the task file loaded. Every condition's `offRampDownDuration_ms` at least 100 ms (250 is usual).
- rig.yml `zapit:` block: host, port, `hardwareTriggered`, `triggerLine`. With `hardwareTriggered: true`, the trigger line must be
  cabled to the Zapit DAQ trigger input. Also check rig.yml's lick thresholds and the left valve's calibration (BACKLOG).
- If rig-control refuses to launch over names not on the lab's names list, add them on rig-setup's Repos page (Save Names),
  with Pip's OK (the list is production config).

## 1. Optogenetics off

rig-control, `test` mouse, Training Stage 4 (Tom's 5), optogenetics unticked; ~20 trials.
**Pass:** task as before; nothing in Zapit's window; no `Zapit\` folder and no `Settings_ZapitStimConfig` file; trial rows
`laserPowerMw` 0, `optoCondition` 0.

## 2. Optogenetics on

Same, optogenetics ticked with `zapitStimConfigPath` and `laserPowerMw`; at least 30 trials (the first 20, `trialsBeforeLaser`, are all 0 mW: galvos move, laser off).
With nobody at the wheel every trial times out after 12 s (`trialStructure.closedLoopDuration`).
**Pass:**
- `Zapit\` log: StartStimulus (command 1) then StopStimulus (command 0) on every trial, alternating, `isError` False.
- Trial rows: `optoCondition` on every trial; `laserPowerMw` the task's `laserPowerMw` on about 40% of trials after the first 20, 0 on the rest.
- `Settings\Settings_ZapitStimConfig_<time>.json` in the experiment folder, holding the config's path and its text.
- No console line "Zapit: the previous trial ended without a StopStimulus"; no "Not ready to stimulate" in MATLAB.

## 3. Hardware-triggered rig

Step 2 on the rig with `hardwareTriggered: true`.
**Pass:** each trial's StartStimulus reply arrives just after the ITI ends; the Behavior board log has the trigger write at every
trial start; with the galvos visible or a photodiode, the stimulus starts at the trigger, not at the command.

## 4. Failures (each should stop the experiment with a message)

- A different config (different number of conditions) loaded in Zapit, at Go.
- A config with a ramp-down under 100 ms, named in `zapitStimConfigPath` (and loaded in Zapit), at Go.
- A non-`test` mouse on a rig with `hardwareTriggered: false`, at Go.
- Zapit closed mid-experiment.

## 5. Laser on (power meter or photodiode)

Laser trials reach the set power; sham trials stay dark; at every offset the light fades over the ramp-down (not a cut: the fade
is what prevents rebound); a Training Stage 1-2 (no time limit) `test` trial with the wheel still goes dark 12.5 s after onset (the cap).

## If Bonsai dies mid-experiment

Nothing tells Zapit, and a continuous stimulus keeps playing: stop it in Zapit straight away (Stop in the GUI, or `hZP.stopOptoStim`)
or close the shutter. Stopping the workflow, or an error, is fine: Bonsai sends StopStimulus itself.
