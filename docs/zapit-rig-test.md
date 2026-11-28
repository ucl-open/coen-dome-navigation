# Zapit: rig checks before the first optogenetics experiment

Use a `test` mouse throughout; keep the laser shutter closed (or 0 mW) until step 5.

## Before you start

- Zapit (MATLAB): real DAQ, not `start_zapit('simulated', true)` (its simulated DAQ hides stop problems); `tcpServer.enable: true`; calibrated; the conditions file named in the task file loaded. Every condition's `offRampDownDuration_ms` 100 to 500 ms (250 is usual).
- rig.yml `zapit:` block: host, port, `hardwareTriggered`; the trigger line is the Behavior board line `behaviorBoard.main.wiring` gives `ZapitTrigger` (DO3). With `hardwareTriggered: true`, it must be cabled to the Zapit DAQ trigger input. Also check rig.yml's lick thresholds and the left valve's calibration (BACKLOG).
- If app-rig-control refuses to launch over names not in the lab's Data Dictionary, add them on its Lab Names page, with Pip's OK (the dictionary is production config).

## 1. Optogenetics off

app-rig-control, `test` mouse, Training Stage 4, optogenetics unticked; ~20 trials.
**Pass:** task as before; nothing in Zapit's window; no `ZapitReply` rows in `WorkflowData_Events` and no `Settings_ZapitConditions` file; trial rows `laserPowerMw` 0, `laserCondition` 0, `laserPointsMm` empty; the trigger line (and the masking LEDs) low throughout: no DO3 edges in the Behavior board log.

## 2. Optogenetics on

Same, optogenetics ticked with `zapitConditionsPath` and `laserPowerMw`; at least 30 trials (the first 20, `trialsBeforeLaser`, are all 0 mW: galvos move, laser off). With nobody at the wheel every trial ends as NoResponse 12 s after its first pip, when the stage's response window (`responseWindowSeconds`) closes.
**Pass:**
- `ZapitReply` rows in `WorkflowData_Events`: StartStimulus then StopStimulus on every trial, alternating (the command in `Detail`), and no error in any `Detail`.
- Trial rows: `laserCondition` on every trial, with its points from the conditions file in `laserPointsMm`; `laserPowerMw` the task's `laserPowerMw` on about 40% of trials after the first 20, 0 on the rest.
- `Settings\Settings_ZapitConditions_<time>.json` in the experiment folder, holding the config's path and its text.
- No "Not ready to stimulate" in MATLAB.

## 3. Hardware-triggered rig

Step 2 on the rig with `hardwareTriggered: true`.
**Pass:** each trial's StartStimulus reply arrives during the interval, before the trial starts (the trial's start is never later for Zapit); the Behavior board log has the trigger line going high `laserToStimulusSeconds` before every first pip (at the trial's start with the default 0.1 s) and low the Zapit file's max ramp-down (1 s) after each outcome (it is held, not pulsed); the masking LEDs stay lit for all of that, on laser-off trials too; Zapit starts every trial (one rising edge each), including the first after a long trial; with the galvos visible or a photodiode, the stimulus starts at the trigger, not at the command.

## 4. Failures (each should stop the experiment with a message)

- A different config (different number of conditions) loaded in Zapit, at Go.
- A config with a ramp-down under 100 ms or over 500 ms, named in `zapitConditionsPath` (and loaded in Zapit), at Go.
- A non-`test` mouse on a rig with `hardwareTriggered: false`, at Go.
- Zapit closed mid-experiment.

## 5. Laser on (power meter or photodiode)

Laser trials reach the set power; laser-off trials stay dark; at every offset the light fades over the ramp-down (not a cut: the fade is what prevents rebound); a Training Stage 1-2 (a response window that never closes) `test` trial with the wheel still goes dark 12.5 s after onset (the cap).

## If Bonsai dies mid-experiment

Nothing tells Zapit, and a continuous stimulus keeps playing: stop it in Zapit straight away (Stop in the GUI, or `hZP.stopOptoStim`) or close the shutter. Stopping the workflow, or an error, is fine: Bonsai sends StopStimulus itself. Stopped or killed mid-trial, the trigger line (and the masking LEDs) goes low on its own once its pulse width in rig.yml has passed (10 s on the domes): the board runs it in pulse mode (lib-open-rigs #16).
