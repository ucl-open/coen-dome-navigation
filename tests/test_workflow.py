"""src\\main.bonsai's rules: shared blocks, every task wait on the Harp clock, the logs' completion rules and the names
the stage and README depend on."""

import re
import xml.etree.ElementTree as ET
import zipfile
from pathlib import Path

ROOT = Path(__file__).parents[1]
W = "{https://bonsai-rx.org/2018/workflow}"
XSI = "{http://www.w3.org/2001/XMLSchema-instance}type"
TEXT = (ROOT / "src" / "main.bonsai").read_text(encoding="utf-8-sig")
MAIN = ET.fromstring(TEXT.encode("utf-8"))


def group(name: str) -> ET.Element:
    return next(e for e in MAIN.iter(W + "Expression") if e.findtext(W + "Name") == name and e.find(W + "Workflow") is not None)


def combinators(element: ET.Element) -> list[str]:
    return [c.get(XSI) for c in element.iter(W + "Combinator")]


def test_every_task_wait_is_on_the_harp_clock():
    # Plan v11: no wall-clock wait inside TaskLogic but the laser cap's backstop, should the board stall.
    logic = group("TaskLogic")
    waits = [c for c in combinators(logic) if c in ("rx:Timer", "rx:Delay", "rx:Throttle", "rx:DelaySubscription", "rx:Timeout", "rx:Interval")]
    assert waits == ["rx:Timer"]
    assert combinators(group("LaserOnUntilOutcome")).count("rx:Timer") == 1
    assert "TimeOfDay" not in TEXT and "HarpBeat" not in TEXT
    assert "core:HarpDelay" in combinators(logic) and "ext:CorridorTask" in combinators(logic)


def test_the_shared_blocks_replace_the_local_copies():
    includes = {e.get("Path") for e in MAIN.iter(W + "Expression") if e.get(XSI) == "IncludeWorkflow"}
    assert {"OpenRigs.Devices:DomeHardware.bonsai", "OpenRigs.Devices:DomeHarpLogs.bonsai",
            "OpenRigs.Devices:BehaviorBoard.DeliverReward.bonsai", "OpenRigs.Vision:DomeDisplay.bonsai"} <= includes
    assert not [p for p in includes if p.startswith("Extensions")]
    assert sorted(p.name for p in (ROOT / "src" / "Extensions").iterdir()) == [
        "Corridor.cs", "ReadTextFile.cs", "UclOpenCoenDomeNavigation.Generated.cs", "UclOpenCoenDomeNavigationTask.Generated.cs"]
    assert "Disable" not in {e.get(XSI) for e in MAIN.iter(W + "Expression")}


def test_video_is_logged_after_the_experiment_folder():
    # Dome Building Blocks: LogVideo stays in the workflow, after PathPrefix.
    logs = ET.tostring(group("ExperimentLogs"), encoding="unicode")
    assert logs.count("OpenRigs.Logging:LogVideo.bonsai") == 3


def test_missing_calibration_is_a_launch_property():
    # app-rig-control launches the test mouse with DeliverNothing, every other with Refuse.
    top = MAIN.find(W + "Workflow").find(W + "Nodes")
    exposed = {p.get("Name") for e in top if e.get(XSI) == "ExternalizedMapping" for p in e}
    assert {"SubjectId", "SessionId", "TaskPath", "MissingCalibration", "DisplayDevice"} == exposed


def test_the_metrics_are_the_view_s():
    names = {c.findtext("{clr-namespace:OpenRigs.Control;assembly=OpenRigs.Control}Name") for c in MAIN.iter(W + "Combinator") if c.get(XSI) == "orcl:ReportMetric"}
    assert names == {"trialNumber", "trainingStage", "rewardSizeUl", "laserCondition", "path"}


def test_no_async_event_sender_completes_async_events():
    # A MulticastSubject passes on its source's completion: a sender that logs once takes its one value after the multicast.
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
                      if nodes[a].find(W + "Combinator") is not None and nodes[a].find(W + "Combinator").get(XSI) in ("rx:Take", "rx:TakeLast", "rx:First", "rx:Last")]
            assert not finite, finite
    assert senders == 2  # RngSeed and Control; DeliverReward sends RewardDelivered itself


def test_stimulus_state_reaches_disk_row_by_row():
    log = next(e for e in group("ExperimentLogs").iter(W + "Expression")
               if e.get("Path") == "OpenRigs.Logging:LogData.bonsai" and e.findtext(W + "LogName") == "BonsaiData_StimulusState")
    assert log.findtext(W + "AutoFlush") == "true"


def _stage_commands() -> set[str]:
    """The OnCommand names OpenRigs.Devices' HarpLickSpoutStage listens for, read from the package this repo uses."""
    nupkg = next((ROOT / "local_packages").glob("OpenRigs.Devices.*.nupkg"))
    with zipfile.ZipFile(nupkg) as z:
        dll = z.read(next(n for n in z.namelist() if n.endswith("lib/net472/OpenRigs.Devices.dll"))).decode("latin-1")
    start = dll.index("The two-driver lick spout stage")
    stage = dll[start:dll.index("</WorkflowBuilder>", start)]
    return set(re.findall(r'OnCommand">\s*<\w+:Name>(\w+)<', stage))


def test_the_workflow_declares_every_command_the_stage_listens_for():
    # Plan v11: ControlServer refuses an undeclared command, so a stage command the workflow does not declare never fires.
    declared = set(re.findall(r"<Command>\s*<Name>(\w+)</Name>", TEXT))
    stage = _stage_commands()
    assert len(stage) == 14 and stage <= declared
    assert declared - stage == {"RewardLeft", "RewardRight"}


def _scope(element: ET.Element) -> tuple[list[ET.Element], list[tuple[int, int]]]:
    workflow = element.find(W + "Workflow")
    return list(workflow.find(W + "Nodes")), [(int(e.get("From")), int(e.get("To"))) for e in workflow.find(W + "Edges")]


def _upstream(edges: list[tuple[int, int]], node: int) -> set[int]:
    seen, frontier = set(), [node]
    while frontier:
        to = frontier.pop()
        for a, b in edges:
            if b == to and a not in seen:
                seen.add(a)
                frontier.append(a)
    return seen


def test_the_mixer_never_starts_sooner_than_3_s():
    # Deliberate: a fault while the sound card boots can blue-screen the PC. Never remove or shorten it.
    nodes, edges = _scope(group("Mixer"))
    delay = next(i for i, n in enumerate(nodes) if n.findtext(W + "Name") == "MixerBootDelay")
    assert nodes[delay].find(W + "Combinator").get(XSI) == "rx:DelaySubscription"
    assert nodes[delay].find(W + "Combinator").findtext("{clr-namespace:Bonsai.Reactive;assembly=Bonsai.Core}DueTime") == "PT3S"
    feeding = [nodes[a] for a in _upstream(edges, delay)]
    expressions = [n.findtext("{clr-namespace:Bonsai.Scripting.Expressions;assembly=Bonsai.Scripting.Expressions}Expression") for n in feeding]
    assert "TimeSpan.FromSeconds(Math.Max(3.0, SpeakerArray.Device.BootDelay))" in expressions


def test_the_trigger_line_rises_and_falls_only_in_an_optogenetics_experiment():
    # BACKLOG (Decided; Pip, 13 Nov 2026): the trigger line and the masking LEDs on it only in optogenetics experiments.
    nodes, edges = _scope(group("TaskLogic"))
    lines = [i for i, n in enumerate(nodes) if (n.find(W + "Combinator") is not None and n.find(W + "Combinator").get(XSI) == "zap:ZapitLineMessage")]
    assert len(lines) == 2
    for line in lines:
        conditions = [nodes[a] for a in _upstream(edges, line) if nodes[a].get(XSI) == "rx:Condition"]
        assert [c.findtext(".//{clr-namespace:Bonsai.Scripting.Expressions;assembly=Bonsai.Scripting.Expressions}Expression") for c in conditions] == ["Trial.optogenetics"]


def test_the_trigger_event_raises_the_line_and_starts_a_software_triggered_zapit():
    # Pip, 13 Nov 2026: the trigger comes laserToStimulusSeconds before the first pip (CorridorTask's Trigger), not at Start.
    nodes, edges = _scope(group("TaskLogic"))
    kind = "{clr-namespace:;assembly=Extensions}Kind"
    for name in ("Optogenetics?", "SoftwareTriggered?"):
        target = next(i for i, n in enumerate(nodes) if n.findtext(W + "Name") == name)
        feeding = [nodes[a].find(W + "Combinator") for a, b in edges if b == target]
        assert [f.findtext(kind) for f in feeding if f is not None] == ["Trigger"]


def test_the_readme_lists_every_open_rigs_block():
    # Every node from an OpenRigs package (any xmlns naming an OpenRigs namespace, and every OpenRigs include) has a row.
    readme = (ROOT / "README.md").read_text(encoding="utf-8")
    table = readme[readme.index("## Shared Blocks"):]
    table = table[: table.index("\n## ", 4)] if "\n## " in table[4:] else table
    prefixes = [p for p, ns in re.findall(r'xmlns:(\w+)="clr-namespace:([\w.]+);', TEXT) if ns.startswith("OpenRigs")]
    used = {name for p in prefixes for name in re.findall(r'xsi:type="' + p + r':(\w+)"', TEXT)}
    used |= {p.split(":")[1].replace(".bonsai", "").split(".")[-1] for p in re.findall(r'Path="(OpenRigs\.[^"]+)"', TEXT)}
    missing = sorted(b for b in used if f"`{b}`" not in table)
    assert not missing, missing
    assert "dome-building-blocks" in table
