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


def test_the_zapit_trigger_line_is_held_until_after_the_outcome():
    # Tom Childs and Pip, 10 Nov 2026: the masking LEDs on the trigger line lasted only rig.yml's 3000 ms pulse while the
    # laser lasts to the outcome. The board no longer pulses that line: TriggerPulse sets it at beep 0 and OptoOnset
    # clears it (ZapitTriggerClear) 1 s after the outcome, past the laser's ramp-down (at most 500 ms).
    text = ET.tostring(MAIN, encoding="unicode")
    assert 'Selector="Settings.PulseController.OutputPulseEnable"' not in text  # rig.yml's lines reach the board only through:
    raw = (Path(__file__).parents[1] / "src" / "main.bonsai").read_text(encoding="utf-8-sig")
    assert raw.count('xsi:type="ext:WithoutPulseLine"') == 1 and ">Zapit.TriggerLine<" in text
    board = ET.tostring(group("BehaviorBoards"), encoding="unicode")
    assert ">ZapitTriggerClear<" in board and "CreateOutputClearPayload" in board and "DigitalOutputLine" in board
    onset = ET.tostring(group("OptoOnset"), encoding="unicode")
    assert onset.count(">ZapitTriggerClear<") == 1 and ">TriggerPulse<" in onset
    delays = [e.text for e in group("OptoOnset").iter() if e.tag.endswith("}DueTime")]
    assert "PT1S" in delays
    source = (Path(__file__).parents[1] / "src" / "Extensions" / "ZapitConditions.cs").read_text(encoding="utf-8-sig")
    assert float(re.search(r"MaximumRampDownMs = ([\d.]+);", source).group(1)) <= 500


def test_the_zapit_trigger_line_is_cleared_however_the_workflow_stops():
    # Tom Childs, 10 Nov 2026: a Stop mid-trial left DO3 high, and the masking LEDs on it lit until the next launch.
    # ClearOutputsOnStop sits on the Behavior board include's output, so stopping disposes the board first (its port
    # closes) and then writes OutputClear DO3 on the board's port, mapped from the group's PortName as the board's is.
    workflow = group("BehaviorBoards").find(W + "Workflow")
    nodes = list(workflow.find(W + "Nodes"))
    edges = [(int(e.get("From")), int(e.get("To")), e.get("Label")) for e in workflow.find(W + "Edges")]
    clear = [i for i, n in enumerate(nodes)
             if n.find(W + "Combinator") is not None and n.find(W + "Combinator").get(XSI) == "ext:ClearOutputsOnStop"]
    assert len(clear) == 1
    clear = clear[0]
    ext = "{clr-namespace:;assembly=Extensions}"
    assert nodes[clear].find(W + "Combinator").findtext(ext + "Outputs") == "DO3"
    board = next(i for i, n in enumerate(nodes) if n.get("Path") == "OpenRigs.Devices:BehaviorBoard.BehaviorBoard.bonsai")
    assert (board, clear, "Source1") in edges
    mapping = next(a for a, b, _ in edges if b == board and nodes[a].get(XSI) == "ExternalizedMapping")
    assert [p.get("Name") for p in nodes[mapping]] == ["PortName"] and any(a == mapping and b == clear for a, b, _ in edges)
    assert not any(a == clear for a, b, _ in edges)  # a sink: its lifetime is the board's
    source = (Path(__file__).parents[1] / "src" / "Extensions" / "ClearOutputsOnStop.cs").read_text(encoding="utf-8-sig")
    assert re.search(r"subscription\.Dispose\(\);\s*Clear\(portName, outputs\);", source)


def test_the_laser_is_continuous_and_stopped_by_bonsai():
    # zapit-v2: Zapit only gets continuous stimuli (it ignores StopStimulus for a timed one, so a StimDuration or StartDelay
    # sent to it would leave the laser on past the outcome); LaserOnUntilOutcome stops each trial's once, at the outcome
    # (TrialActive back to 0) or after OptogeneticMaxDuration. The trigger line is raised on every trial, as av-dome's (held, not pulsed: below).
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
    # zapit-v2: TaskLogic's first pip, IsTrialEnd and IsTimeOut skip laserToStimulusSeconds / 50 ms beeps in an optogenetics experiment
    text = ET.tostring(MAIN, encoding="unicode")
    assert text.count(">StimulusStartBeep<") == 3


def test_the_onset_column_says_where_the_moved_pip_is():
    # Audit, 10 Nov 2026 (round 1): stimulusOnsetTime was described as beep 1 always; with optogenetics on the first pip is
    # laserToStimulusSeconds after beep 0 (StimulusStartBeep sets how many beeps Skip drops before LogStimulus's first row).
    columns = yaml.safe_load((Path(__file__).parents[1] / "schemas" / "trial-columns.yml").read_text(encoding="utf-8"))
    assert "laserToStimulusSeconds after beep 0" in columns["stimulusOnsetTime"]["description"]
    log = next(e for e in MAIN.iter(W + "Expression") if e.findtext(W + "Name") == "LogStimulus")
    assert "StimulusStartBeep" in log.findtext(W + "Description")


def test_the_safety_stop_and_startup_checks_are_there():
    text = ET.tostring(MAIN, encoding="unicode")
    for name in ("StimulusLeftOn?", "ZapitError?", "ConditionCountReply?", "SoftwareTriggeredRealMouse?", "Settings_ZapitConditions"):
        assert ">%s<" % name in text, name


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
    assert log.findtext(W + "LogName") == "BonsaiData_StimulusState"
    assert log.findtext(W + "AutoFlush") == "true"


def test_stimulus_state_is_in_bonsai_data_with_its_units():
    # Pip, 9 Nov 2026: everything Bonsai computes is in BonsaiData (Trial, Async, StimulusState), so the manifest
    # expects no StimulusState folder. Only what the trial row and the Harp logs cannot give (beep, the sweep's step and
    # the grating's 0/1 opacity follow from them), named with the trial row's audio/visual prefixes.
    text = (Path(__file__).parents[1] / "src" / "main.bonsai").read_text(encoding="utf-8")
    row = re.search(r"new\(new\(it\.Item2 as trialNumber.*?\) as Row", text).group(0)
    assert re.findall(r" as (\w+)", row)[:-1] == ["trialNumber", "positionCm", "toneFrequencyHz", "stimulusActive",
                                             "visualSpatialFrequencyCpd"]
    assert "StimulusState/" not in text and "<LogName>StimulusState</LogName>" not in text
    assert "<LogName>BonsaiData_ZapitReplies</LogName>" in text and "<LogName>Zapit</LogName>" not in text


def test_the_manifest_hashes_the_rig_s_calibration_folder_as_av_dome_s():
    # One calibration folder per rig, beside its rig.yml, read by every workflow on it (av-dome's layout);
    # production's C:\RigConfigs is copied there when a rig switches over, not read (BLUE-OPTO-2, 9 Nov 2026).
    mf = "{clr-namespace:OpenRigs.Manifest;assembly=OpenRigs.Manifest}"
    files = next(e for e in MAIN.iter(W + "Expression") if e.findtext(W + "Name") == "ExperimentManifest").findtext(mf + "ConfigFiles").split(";")
    rig = "C:\\Coen-Lab-Local\\RigConfigs\\%COMPUTERNAME%\\"
    assert files == [rig + "rig.yml", rig + r"calibration\MeshMap.csv", rig + r"calibration\gammalut.bmp",
                     rig + r"calibration\LickSpoutPositions\{SubjectId}.csv"]


def test_descriptions_use_the_task_file_s_stage_rows():
    # Audit, 10 Nov 2026, round 2: stages are trainingStages rows from 0 (Tom Childs' 1 to 6 are rows 0 to 5), and the
    # beep log is BonsaiData_StimulusState; the trial log's endReason meaning (copied into every manifest) and two
    # descriptions still said "stages 1-2", "stage 6" and "StimulusLog row".
    root = Path(__file__).parents[1]
    columns = yaml.safe_load((root / "schemas" / "trial-columns.yml").read_text(encoding="utf-8"))
    assert "lickToReward" in columns["endReason"]["description"]
    descriptions = [e.findtext(W + "Description") or "" for e in MAIN.iter(W + "Expression")]
    for text in descriptions + [c["description"] for c in columns.values()]:
        assert "(stage 6)" not in text and "(stages 1-2)" not in text and "StimulusLog row" not in text