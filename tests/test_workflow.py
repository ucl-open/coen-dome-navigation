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

def test_zapit_is_the_shared_include_given_the_conditions_file():
    # Audit round 3: the connect, check and refusal chain is lib-open-rigs' Zapit.ConnectZapit (its stops are tested C#
    # there, a real mouse on a rig without hardware triggering among them), shared with coen-dome-decision-making.
    nodes, edges = _scope(group("Zapit"))
    include = next(i for i, n in enumerate(nodes) if n.get("Path") == "OpenRigs.Devices:Zapit.ConnectZapit.bonsai")
    path = [nodes[i] for i in _upstream(edges, include)]
    assert any(n.findtext(W + "Selector") == "TaskParameters.Optogenetics.ZapitConfigPath" for n in path)
    assert any(n.get(XSI) == "rx:Condition" and "Optogenetics.UseZapit" in ET.tostring(n, encoding="unicode") for n in path)
    for local in ("zap:ZapitClient", "zap:CheckZapitConditionCount", "core:ThrowError", "ConnectZapit</Name>"):
        assert local not in TEXT


AUD = "{clr-namespace:OpenRigs.Audio;assembly=OpenRigs.Audio}"


def _audio_chain(subject: str) -> tuple[list[ET.Element], set[int]]:
    """The Audio group's nodes and the ones upstream of the AsyncSubject or AppendBuffer named by subject."""
    nodes, edges = _scope(group("Audio"))
    end = next(i for i, n in enumerate(nodes) if n.get(XSI) in ("rx:AsyncSubject", "rx:BehaviorSubject") and n.findtext(W + "Name") == subject)
    return nodes, _upstream(edges, end)


def _labels(nodes: list[ET.Element], indices: set[int]) -> list[str]:
    return [combinators(nodes[i])[0] if combinators(nodes[i]) else (nodes[i].findtext(W + "Name") or nodes[i].findtext(W + "Selector")) for i in sorted(indices)]


def test_every_speaker_needs_a_filter_at_start():
    # Pip, 13 Nov 2026: as coen-dome-decision-making, a speaker without a filter stops the workflow at Start, naming it.
    # From 14 Nov 2026 OpenRigs.Audio's LoadSpeakers checks it as it loads rig.yml's speakers, once, into Speakers.
    nodes, upstream = _audio_chain("Speakers")
    assert _labels(nodes, upstream) == ["RigFile", "aud:LoadSpeakers"]
    assert nodes[min(upstream)].get(XSI) == "SubscribeSubject"
    assert "RequireSpeakerFilters" not in TEXT and "RequireSpeakerFilters" not in (ROOT / "src" / "Extensions" / "Corridor.cs").read_text(encoding="utf-8")
    template = (ROOT / "config" / "rig-template.yml").read_text(encoding="utf-8")
    assert "unequalised" not in template and "Every speaker needs one" in template and "RequireSpeakerFilters" not in template


def _speaker_buffer(nodes: list[ET.Element], upstream: set[int]) -> ET.Element:
    found = [c for i in upstream for c in nodes[i].iter(W + "Combinator") if c.get(XSI) == "aud:SpeakerBuffer"]
    assert len(found) == 1
    return found[0]


def test_the_pips_and_the_reward_tone_play_equalised_on_every_speaker():
    # Audit, 14 Nov 2026: each sound is one row laid out by OpenRigs.Audio's SpeakerBuffer on rig.yml's channels
    # (no Tile 32), equalised for each speaker; no Channels is every rig.yml speaker.
    for subject, reader in (("PipBuffers", "all_waveforms.bin"), ("RewardTone", "reward_tone.bin")):
        nodes, upstream = _audio_chain(subject)
        labels = _labels(nodes, upstream)
        assert "Speakers" in labels and "rx:Zip" in labels and "dsp:Tile" not in labels, labels
        assert any(reader in ET.tostring(nodes[i], encoding="unicode") for i in upstream)
        buffer = _speaker_buffer(nodes, upstream)
        assert buffer.findtext(AUD + "Equalise") == "true" and buffer.findtext(AUD + "Gain") == "1"
        assert not (buffer.findtext(AUD + "Channels") or "").strip()
    nodes, upstream = _audio_chain("PipBuffers")
    assert _labels(nodes, upstream)[-2:] == ["rx:Zip", "aud:SpeakerBuffer"] and "rx:ToList" in _labels(nodes, upstream)


def test_the_background_noise_plays_unequalised_on_every_speaker():
    nodes, edges = _scope(group("Audio"))
    append = next(i for i, n in enumerate(nodes) if "mix:AppendBuffer" in combinators(n) and any(
        nodes[a].findtext(W + "Name") == "BackgroundSource" for a in _upstream(edges, i)))
    upstream = _upstream(edges, append)
    assert "Speakers" in _labels(nodes, upstream)
    buffer = _speaker_buffer(nodes, upstream)
    assert buffer.findtext(AUD + "Equalise") == "false" and not (buffer.findtext(AUD + "Channels") or "").strip()


def test_the_error_sound_is_the_lab_s_at_the_task_s_gain():
    # Audit, 14 Nov 2026: OpenRigs.Audio's ErrorSoundBuffer (rig.yml's errorSoundChannel, equalised), its Gain set from
    # the task's punishment.sound.gain before each buffer (InputMapping), so the speakers arriving first cannot use 1.
    nodes, upstream = _audio_chain("ErrorTone")
    assert _labels(nodes, upstream) == ["TaskConfig", "TaskParameters.Punishment.Sound.Gain", "Speakers", "rx:Zip", "Item2", "aud:ErrorSoundBuffer"]
    mapping = next(nodes[i] for i in upstream if nodes[i].get(XSI) == "InputMapping")
    assert [(p.get("Name"), p.get("Selector")) for p in mapping.iter(W + "Property")] == [("Gain", "Item1")]
    assert "error_tone.wav" not in TEXT and "al:AudioReader" not in TEXT and "Sound.Path" not in TEXT
    assert 'xmlns:aud="clr-namespace:OpenRigs.Audio;assembly=OpenRigs.Audio"' in TEXT


def test_the_speakers_are_declared_before_every_user():
    # A GroupWorkflow does not scope subjects; keep the declaration ahead of its subscribers.
    nodes, _ = _scope(group("Audio"))
    declared = next(i for i, n in enumerate(nodes) if n.get(XSI) == "rx:AsyncSubject" and n.findtext(W + "Name") == "Speakers")
    users = [i for i, n in enumerate(nodes) if n.get(XSI) == "SubscribeSubject" and n.findtext(W + "Name") == "Speakers"]
    assert len(users) == 4 and min(users) > declared


def test_the_manual_reward_is_capped_as_every_reward():
    # Audit round 4: ManualRewardUl's Maximum is lib-open-rigs' MAX_REWARD_UL (DeliverReward's MaxRewardUl).
    from open_rigs.rigs.dome import MAX_REWARD_UL

    parameter = next(p for p in MAIN.iter(W + "Parameter") if p.findtext(W + "Name") == "ManualRewardUl")
    assert float(parameter.findtext(W + "Maximum")) == MAX_REWARD_UL


def test_the_trial_events_leave_the_clock_s_thread_unswitched():
    # Plan v11 W3, audit round 4: CorridorTask serialises its inputs, and nothing downstream blocks, so no ObserveOn.
    assert "rx:ObserveOn" not in combinators(group("TaskLogic"))


def test_the_layout_is_tracked_and_the_schemas_are_the_generator_s():
    # Audit round 4: a tracked layout replaces a stale per-machine one at Setup; calibration.json had no config file.
    assert (ROOT / "config" / "main.layout").read_text(encoding="utf-8").count("<VisualizerLayout") == 1
    assert "..\\config\\main.layout" in (ROOT / ".bonsai" / "Setup.ps1").read_text(encoding="utf-8-sig")
    assert sorted(p.name for p in (ROOT / "src" / "DataSchemas").iterdir()) == [
        "ucl_open_coen_dome_navigation.json", "ucl_open_coen_dome_navigation_task.json"]


PER_ELEMENT = {"rx:SelectMany", "rx:CreateObservable", "rx:Concat", "rx:Switch"}


def _subjects_closed_by_a_nested_multicast(top):
    """Each MulticastSubject inside a per-element nested graph whose subject is not a BehaviorSubject declared in this
    workflow or inside that graph. Bonsai 2.9 passes a multicast source's completion to a PublishSubject or
    ReplaySubject, so the first trial's graph closes it for good; a declared BehaviorSubject ignores it."""
    declared = {}

    def declare(wf):
        for n in wf.find(f"{W}Nodes"):
            if (n.get(XSI) or "").startswith("rx:") and (n.get(XSI) or "").endswith("Subject"):
                declared[n.findtext("{clr-namespace:Bonsai.Reactive;assembly=Bonsai.Core}Name")] = n.get(XSI)
            inner = n.find(f"{W}Workflow")
            if inner is not None:
                declare(inner)

    declare(top)
    found = []

    def walk(wf, nested, local):
        local = local | {n.findtext("{clr-namespace:Bonsai.Reactive;assembly=Bonsai.Core}Name") for n in wf.find(f"{W}Nodes")
                         if (n.get(XSI) or "").endswith("Subject") and (n.get(XSI) or "").startswith("rx:")}
        for n in wf.find(f"{W}Nodes"):
            if n.get(XSI) == "MulticastSubject" and nested:
                name = n.findtext(f"{W}Name")
                if name not in local and declared.get(name) != "rx:BehaviorSubject":
                    found.append(name)
            inner = n.find(f"{W}Workflow")
            if inner is not None:
                per = n.get(XSI) in PER_ELEMENT
                walk(inner, nested or per, local if not per else set())

    walk(top, False, set())
    return found


def test_no_trial_graph_closes_a_shared_subject():
    """Bonsai 2.9 passes a multicast source's completion to a PublishSubject (BLUE-OPTO-2, 14 Nov 2026): the first laser
    trial's LaserOnUntilOutcome closed ZapitCommands, so ZapitClient sent StopStimulus and disconnected, and every
    later trial's arm went nowhere. ZapitCommands is a BehaviorSubject, which ignores it (ConnectZapit subscribes once)."""
    # AsyncEvents (OpenRigs.Logging's) is multicast in ExperimentLogs, run once per experiment, from Trial, which never
    # completes, so nothing closes it before the workflow ends.
    assert _subjects_closed_by_a_nested_multicast(MAIN.find(f"{W}Workflow")) == ["AsyncEvents"]


def test_nothing_reads_a_startup_watchdog_s_output():
    # StartupWatchdog (OpenRigs.Core) completes once every stage has come and never emits, so a node fed by
    # it never sees a value: rx:Last after it raised "Sequence contains no elements" at every start and Ready
    # never came (GREEN-DOME-2, 14 Nov 2026). Ready is the stages zipped; the watchdog is a branch of its own.
    for workflow in MAIN.iter(W + "Workflow"):
        nodes = list(workflow.find(W + "Nodes"))
        edges = [(int(e.get("From")), int(e.get("To"))) for e in workflow.find(W + "Edges")]
        for i, node in enumerate(nodes):
            if (node.find(W + "Combinator") is not None and node.find(W + "Combinator").get(XSI, "").endswith(":StartupWatchdog")):
                assert not [b for a, b in edges if a == i], "a node reads StartupWatchdog's output, which never comes"


def test_ready_comes_from_the_clock_and_the_homed_spouts():
    nodes, edges = _scope(group("Ready"))
    report = next(i for i, n in enumerate(nodes) if n.find(W + "Combinator") is not None and n.find(W + "Combinator").get(XSI) == "orcl:ReportPhase")
    names = {nodes[a].findtext(W + "Name") for a in _upstream(edges, report)}
    assert {"HarpClockSet", "LickSpoutStage.Homed"} <= names
    assert not any(n.find(W + "Combinator") is not None and n.find(W + "Combinator").get(XSI, "").endswith(":StartupWatchdog") for n in (nodes[a] for a in _upstream(edges, report)))
