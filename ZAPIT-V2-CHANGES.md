# zapit-v2: changes and why

The Zapit optogenetics from `ucl-open/auditory-vr-corridor` branch `zapit-integration-v2`, ported onto Pip's `main` (5f8a3d6). Pip's structure and conventions are kept: rig.yml's `zapit:` block, the trigger pulsed at beep 0 on every trial, the `optoStimulated`/`optoCondition` trial columns, the `Zapit\` log.

## Laser behaviour

| Change | Why |
|---|---|
| Zapit is never sent a StimDuration or StartDelay: every stimulus is continuous. | Zapit ignores StopStimulus for a timed stimulus. With `stimDuration` sent (12 s), the laser ran 12 s on every laser trial, through the outcome, ITI and into the next trial, and the next laser trial's start was refused. |
| LaserOnUntilOutcome (BeepTimer/OptoOnset) sends one StopStimulus per trial: at the outcome (TrialActive back to 0) or after `stimDuration`, whichever comes first. Pip's stop in ProcessTrialEnd removed. | The cap must be Bonsai's, since Zapit can't cap a continuous stimulus; a second stop during Zapit's ramp-down could error. |
| Sham trials: every trial of an optogenetics experiment sends Zapit a condition, laser off on non-laser trials. | Galvo noise and masking light are then identical on laser and sham trials, so the mouse can't tell them apart. |
| ArmZapit runs once the ITI has ended, not during it, and checks Zapit armed the requested condition and laser state. | Zapit stays armed only for the round trip; a stop during the ITI never leaves it armed. |
| `leadTime` (0.1 s): the first tone pip, lick detection and the timeout move `leadTime / 50 ms` beeps later in an optogenetics experiment (CueStartBeep). | The laser starts at beep 0; this gives ~100 ms of inhibition before the first evidence, the same on laser and sham trials. |
| StimulusLeftOn?: at ITI start, if the last command sent to Zapit was a StartStimulus, send StopStimulus (and print it). | Insurance: if a trial ever ends without reaching its stop, the continuous laser can't run on uncapped. |
| BeepTimer: TrialNumber++ is its own component again; InitialiseOptoTrial reads TrialNumber itself. | Restores main's subscription order (a branch from TrialNumber++ into the opto draw had moved TrialNumber++ last). |

## Checks (the experiment stops with a message)

| Check | Why |
|---|---|
| Any Zapit error reply (start, stop or query). Replaces StartRefused?, which only watched during arming. | Otherwise a refused start logs a laser trial with no laser, or a failed stop goes unnoticed. |
| At connect: Zapit's condition count equals the task file's config, and every condition's `offRampDownDuration_ms` is ≥ 100 ms and longer than one stimulus cycle. | A different config loaded in Zapit (shared rigs) would stimulate the wrong sites silently. Short ramps cause rebound excitation; a ramp of one cycle or less leaves Zapit's DAQ running, so the next armed stimulus starts before its trigger. |
| A rig without hardware triggering (rig.yml) refuses an optogenetics experiment unless the mouse ID starts with `test`. | Software start is tens of ms late, varies, and has no onset in the Harp log. |
| InitialiseOptoTrial (trial 1): `stimDelay` 0, `leadTime` a multiple of 50 ms (0.05-1 s), `stimDuration` ≥ 12 s + `leadTime` + 0.25 s. | rig-control checks only the schema; these are cross-field rules. |

## Task file and model

| Change | Why |
|---|---|
| `stimProbability` 0.4, `warmupTrials` 20 (sham), `stimDuration` 12.5, `leadTime` 0.1 (new), `stimDelay` 0 (schema max 0). | Tom's design: 40% laser over 5 conditions incl. an off-brain control. `stimDelay` kept for av-dome's field names; Zapit ignores it for continuous stimuli. |
| `rewardSizeUl` 3.0. | Tom's reward volume. Uncalibrated valves give two 40 ms pulses; calibrate for a true 3 ul. |

## Files

- `src/main.bonsai`: the above (BeepTimer, OptoOnset, ArmZapit, ProcessTrialEnd, ConnectZapit, ExperimentLogs: `Settings_ZapitStimConfig.yml`, a copy of the stimulus config, in optogenetics experiments).
- `src/Extensions/`: `InitialiseOptoTrial.cs` (sham queues, checks), `ZapitStimConfig.cs` and `CheckZapitConditionCount.cs` (new), `ZapitClient.cs` (sends StopStimulus when the workflow stops).
- `stage.py`: an optogenetics experiment keeps its stage; laser-on trials never count toward a decision.
- `schemas/trial-columns.yml`: `optoCondition` includes sham trials' conditions.
- Tests: `tests/test_optogenetics.py` (new); `test_task_file.py`, `test_stage.py`, `test_workflow.py` updated (Pip's av-dome-laser test replaced).

## Verified on a Mac (no rig)

- pytest: 64 passed. C#: the Zapit suite, 187 passed (draws, config parsing, count check, client against a mock Zapit); the ramp rule, 9 cases.
- The whole workflow builds in Bonsai 2.9 with all its packages (cameras left out: their SDK is Windows-only).
- Not run on hardware: see `docs/zapit-rig-test.md`.
