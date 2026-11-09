"""What the audit of 9 Nov 2026 found wrong in src\\main.bonsai stays fixed."""

import xml.etree.ElementTree as ET
from pathlib import Path

W = "{https://bonsai-rx.org/2018/workflow}"
XSI = "{http://www.w3.org/2001/XMLSchema-instance}type"
MAIN = ET.parse(Path(__file__).parents[1] / "src" / "main.bonsai").getroot()


def group(name: str) -> ET.Element:
    return next(e for e in MAIN.iter(W + "Expression") if e.findtext(W + "Name") == name and e.find(W + "Workflow") is not None)


def test_the_behavior_board_encoder_counts_position_not_displacement():
    # The OpenRigs.Devices RunningWheel include sets Displacement, which its difference-and-accumulate
    # turns into a speed: the mouse never moved along the track.
    text = ET.tostring(MAIN, encoding="unicode")
    assert "BehaviorBoard.RunningWheel.bonsai" not in text
    wheel = ET.tostring(group("RunningWheel"), encoding="unicode")
    assert ">Position<" in wheel and ">Displacement<" not in wheel
    assert "BoardWheelDistanceCm" in wheel


def test_the_laser_is_run_as_av_dome_runs_it():
    # Pip, 9 Nov 2026: av-dome's parameters and behaviour. Zapit gets StartDelay and StimDuration and
    # stops the laser itself (no StopStimulus at the outcome); the trigger is pulsed on every trial
    # (the opto-only condition disabled, as av-dome's StartLaser); the cue no longer waits a lead time.
    text = ET.tostring(MAIN, encoding="unicode")
    assert "CreateStopStimulus" not in text and "LeadTime" not in text and "CueStartBeep" not in text
    assert text.count('Selector="OptogeneticDelay"') == 2  # both StartStimulus: armed and software-timed
    onset = group("OptoOnset").find(W + "Workflow").find(W + "Nodes")
    pulse_gate = next(e for e in onset if e.get(XSI) == "Disable")
    assert pulse_gate.find(W + "Builder").findtext(W + "Name") == "HardwareTriggeredOptoTrial?"


def test_the_trial_row_is_built_at_the_outcome_not_two_seconds_later():
    # A Stop in the 2 s before TrialEnded lost the trial's row, its reward already logged.
    first = group("TrialRow").find(W + "Workflow").find(W + "Nodes")[0]
    assert first.findtext(W + "Name") == "TrialOutcome"
    assert any(e.findtext(W + "Name") == "TrialOutcome" and e.get(XSI) == "MulticastSubject"
               for e in group("ProcessTrialEnd").find(W + "Workflow").find(W + "Nodes"))
