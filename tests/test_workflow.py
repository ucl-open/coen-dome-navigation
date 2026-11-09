"""What the audit of 9 Nov 2026 found wrong in src\\main.bonsai stays fixed."""

import re
import xml.etree.ElementTree as ET
from pathlib import Path

import yaml

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


def test_the_laser_starts_as_av_dome_s_and_lasts_the_trial():
    # Pip, 9 Nov 2026: av-dome's parameters, and its start: Zapit gets StartDelay and StimDuration, the
    # trigger is pulsed on every trial (the opto-only condition disabled, as av-dome's StartLaser), the
    # cue no longer waits a lead time. But in this task the laser lasts the trial: StopStimulus at the outcome.
    text = ET.tostring(MAIN, encoding="unicode")
    assert "LeadTime" not in text and "CueStartBeep" not in text
    end = ET.tostring(group("ProcessTrialEnd"), encoding="unicode")
    assert "CreateStopStimulus" in end and ">TrialActive<" in end
    assert text.count('Selector="OptogeneticDelay"') == 2  # both StartStimulus: armed and software-timed
    onset = group("OptoOnset").find(W + "Workflow").find(W + "Nodes")
    pulse_gate = next(e for e in onset if e.get(XSI) == "Disable")
    assert pulse_gate.find(W + "Builder").findtext(W + "Name") == "HardwareTriggeredOptoTrial?"


def test_the_trial_row_writes_the_schema_s_columns_in_its_order():
    # schemas\trial-columns.yml is what rig-control checks and the manifest carries; BuildTrialRow is what is written.
    scr = "{clr-namespace:Bonsai.Scripting.Expressions;assembly=Bonsai.Scripting.Expressions}"
    build = next(e for e in group("TrialRow").iter() if e.findtext(scr + "Name") == "BuildTrialRow")
    written = re.findall(r" as (\w+)", build.findtext(scr + "Expression"))
    schema = yaml.safe_load((Path(__file__).parents[1] / "schemas" / "trial-columns.yml").read_text(encoding="utf-8"))
    assert written == [c for c in schema if c != "Seconds"]


def test_the_trial_row_is_built_at_the_outcome_not_two_seconds_later():
    # A Stop in the 2 s before TrialEnded lost the trial's row, its reward already logged.
    first = group("TrialRow").find(W + "Workflow").find(W + "Nodes")[0]
    assert first.findtext(W + "Name") == "TrialOutcome"
    assert any(e.findtext(W + "Name") == "TrialOutcome" and e.get(XSI) == "MulticastSubject"
               for e in group("ProcessTrialEnd").find(W + "Workflow").find(W + "Nodes"))


def test_the_trial_outcome_is_not_completed_at_the_end_of_trial_1():
    # A MulticastSubject passes on its source's completion, and ProcessTrialEnd's stream completes
    # at the end of every trial: the shared TrialOutcome PublishSubject completed after trial 1, and
    # the trial log had one row for nine trials (test-1729, 9 Nov 2026). A Take(1) straight after the
    # multicast unsubscribes from upstream as the outcome passes, so the completion never reaches it.
    workflow = group("ProcessTrialEnd").find(W + "Workflow")
    nodes = list(workflow.find(W + "Nodes"))
    outcome = next(i for i, e in enumerate(nodes)
                   if e.findtext(W + "Name") == "TrialOutcome" and e.get(XSI) == "MulticastSubject")
    after = [int(e.get("To")) for e in workflow.find(W + "Edges") if int(e.get("From")) == outcome]
    assert len(after) == 1
    take = nodes[after[0]].find(W + "Combinator")
    assert take is not None and take.get(XSI) == "rx:Take"
    assert take.findtext("{clr-namespace:Bonsai.Reactive;assembly=Bonsai.Core}Count") == "1"


def test_no_async_event_sender_completes_async_events():
    # A MulticastSubject passes on its source's completion. RngSeed's Take(1) sat ahead of its
    # multicast and completed the shared AsyncEvents subject a second after Go: no reward or Stop
    # was logged after it, and rig-control counted no water (test-1802, 9 Nov 2026). A sender that
    # logs once takes its one value after the multicast.
    senders = 0
    for workflow in MAIN.iter(W + "Workflow"):
        nodes = list(workflow.find(W + "Nodes"))
        edges = [(int(e.get("From")), int(e.get("To"))) for e in workflow.find(W + "Edges")]
        for i, node in enumerate(nodes):
            if node.get(XSI) != "MulticastSubject" or node.findtext(W + "Name") != "AsyncEvents":
                continue
            senders += 1
            upstream, frontier = set(), [i]
            while frontier:
                to = frontier.pop()
                for a, b in edges:
                    if b == to and a not in upstream:
                        upstream.add(a)
                        frontier.append(a)
            finite = [nodes[a].find(W + "Combinator").get(XSI) for a in upstream
                      if nodes[a].find(W + "Combinator") is not None
                      and nodes[a].find(W + "Combinator").get(XSI) in ("rx:Take", "rx:TakeLast", "rx:First", "rx:Last")]
            assert not finite, f"an AsyncEvents sender completes after {finite}"
    assert senders == 3  # RngSeed, RewardDelivered, Control


def test_stimulus_state_reaches_disk_row_by_row():
    # Buffered, it reached disk in 4 KB blocks: the manifest's end-of-run write, before this writer
    # closes, recorded a size short of the file (test-1802 and test-1813, 9 Nov 2026).
    log = next(e for e in group("StimulusLogger").iter(W + "Expression")
               if e.get(XSI) == "IncludeWorkflow" and e.get("Path") == "OpenRigs.Logging:LogData.bonsai")
    assert log.findtext(W + "LogName") == "StimulusState"
    assert log.findtext(W + "AutoFlush") == "true"


def test_the_manifest_hashes_the_rig_s_calibration_folder_as_av_dome_s():
    # One calibration folder per rig, beside its rig.yml, read by every workflow on it (av-dome's layout);
    # production's C:\RigConfigs is copied there when a rig switches over, not read (BLUE-OPTO-2, 9 Nov 2026).
    mf = "{clr-namespace:OpenRigs.Manifest;assembly=OpenRigs.Manifest}"
    files = next(e for e in MAIN.iter(W + "Expression") if e.findtext(W + "Name") == "ExperimentManifest").findtext(mf + "ConfigFiles").split(";")
    rig = "C:\\Coen-Lab-Local\\RigConfigs\\%COMPUTERNAME%\\"
    assert files == [rig + "rig.yml", rig + r"calibration\MeshMap.csv", rig + r"calibration\gammalut.bmp",
                     rig + r"calibration\LickSpoutPositions\{SubjectId}.csv"]
