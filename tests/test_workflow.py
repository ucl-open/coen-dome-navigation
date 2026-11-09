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


def test_the_laser_is_continuous_and_stopped_by_bonsai():
    # zapit-v2: Zapit only gets continuous stimuli (it ignores StopStimulus for a timed one, so a StimDuration or StartDelay
    # sent to it would leave the laser on past the outcome); LaserOnUntilOutcome stops each trial's once, at the outcome
    # (TrialActive back to 0) or after OptogeneticMaxDuration. The trigger is still pulsed on every trial, as av-dome's.
    text = ET.tostring(MAIN, encoding="unicode")
    for gone in ('Selector="OptogeneticDelay"', 'Selector="OptogeneticDuration"', 'Property Name="StimDuration"', 'Property Name="StartDelay"'):
        assert gone not in text
    assert "CreateStopStimulus" not in ET.tostring(group("ProcessTrialEnd"), encoding="unicode")
    stop = ET.tostring(group("LaserOnUntilOutcome"), encoding="unicode")
    assert stop.count("CreateStopStimulus") == 1 and ">TrialActive<" in stop and "OptogeneticMaxDuration" in stop
    onset = group("OptoOnset").find(W + "Workflow").find(W + "Nodes")
    pulse_gate = next(e for e in onset if e.get(XSI) == "Disable")
    assert pulse_gate.find(W + "Builder").findtext(W + "Name") == "HardwareTriggeredOptoTrial?"


def test_zapit_is_armed_once_the_iti_has_ended():
    # zapit-v2: InitITI zipped with this trial's draw, then ArmZapit, then FinishedITI
    beep = group("BeepTimer").find(W + "Workflow")
    nodes, edges = list(beep.find(W + "Nodes")), beep.find(W + "Edges")
    index = {n.findtext(W + "Name"): i for i, n in enumerate(nodes) if n.findtext(W + "Name")}
    arm = index["ArmZapit"]
    into = [int(e.get("From")) for e in edges if int(e.get("To")) == arm]
    out = [int(e.get("To")) for e in edges if int(e.get("From")) == arm]
    assert len(into) == 1 and nodes[into[0]].findtext(W + "Selector") == "Item2"
    assert "FinishedITI" in ET.tostring(nodes[out[0]], encoding="unicode")


def test_the_lead_time_moves_the_first_pip_and_what_waits_for_it():
    # zapit-v2: TaskLogic's first pip, IsTrialEnd and IsTimeOut skip leadTime / 50 ms beeps in an optogenetics experiment
    text = ET.tostring(MAIN, encoding="unicode")
    assert text.count(">CueStartBeep<") == 3


def test_the_safety_stop_and_startup_checks_are_there():
    text = ET.tostring(MAIN, encoding="unicode")
    for name in ("StimulusLeftOn?", "ZapitError?", "ConditionCountReply?", "SoftwareTriggeredRealMouse?", "Settings_ZapitStimConfig"):
        assert ">%s<" % name in text, name


def test_the_trial_row_is_built_at_the_outcome_not_two_seconds_later():
    # A Stop in the 2 s before TrialEnded lost the trial's row, its reward already logged.
    first = group("TrialRow").find(W + "Workflow").find(W + "Nodes")[0]
    assert first.findtext(W + "Name") == "TrialOutcome"
    assert any(e.findtext(W + "Name") == "TrialOutcome" and e.get(XSI) == "MulticastSubject"
               for e in group("ProcessTrialEnd").find(W + "Workflow").find(W + "Nodes"))
