"""src\\main.bonsai's rules: shared blocks, every task wait on the Harp clock, the logs' completion rules and the names
the stage and README depend on."""

import re
import xml.etree.ElementTree as ET
from pathlib import Path

from open_rigs.rigs import workflow_lint as lint

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
    assert {"OpenRigs.Devices:RigHardware.bonsai", "OpenRigs.Devices:HarpLogs.bonsai",
            "OpenRigs.Devices:BehaviorBoard.DeliverReward.bonsai", "OpenRigs.Vision:ProjectorDisplay.bonsai"} <= includes
    assert not [p for p in includes if p.startswith("Extensions")]
    assert sorted(p.name for p in (ROOT / "src" / "Extensions").iterdir()) == [
        "Corridor.cs", "UclOpenCoenDomeNavigationTask.Generated.cs"]
    assert "Disable" not in {e.get(XSI) for e in MAIN.iter(W + "Expression")}


def test_the_launch_properties():
    # No MissingCalibration: a reward valve without a calibration always stops the workflow (Pip, 15 Nov 2026).
    top = MAIN.find(W + "Workflow").find(W + "Nodes")
    exposed = {p.get("Name") for e in top if e.get(XSI) == "ExternalizedMapping" for p in e}
    assert {"SubjectId", "SessionId", "TaskPath", "DisplayDevice"} == exposed


def test_the_metrics_are_the_view_s():
    names = {c.findtext("{clr-namespace:OpenRigs.Control;assembly=OpenRigs.Control}Name") for c in MAIN.iter(W + "Combinator") if c.get(XSI) == "orcl:ReportMetric"}
    assert names == {"trialNumber", "trainingStage", "rewardSizeUl", "laserCondition", "path"}


def test_stimulus_state_reaches_disk_row_by_row():
    log = next(e for e in group("ExperimentLogs").iter(W + "Expression")
               if e.get("Path") == "OpenRigs.Logging:LogData.bonsai" and e.findtext(W + "LogName") == "BonsaiData_StimulusState")
    assert log.findtext(W + "AutoFlush") == "true"


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


AUD = "{clr-namespace:OpenRigs.Audio;assembly=OpenRigs.Audio}"


def _audio_chain(subject: str) -> tuple[list[ET.Element], set[int]]:
    """The Audio group's nodes and the ones upstream of the AsyncSubject or AppendBuffer named by subject."""
    nodes, edges = _scope(group("Audio"))
    end = next(i for i, n in enumerate(nodes) if n.get(XSI) in ("rx:AsyncSubject", "rx:BehaviorSubject") and n.findtext(W + "Name") == subject)
    return nodes, _upstream(edges, end)


def _labels(nodes: list[ET.Element], indices: set[int]) -> list[str]:
    return [combinators(nodes[i])[0] if combinators(nodes[i]) else (nodes[i].findtext(W + "Name") or nodes[i].findtext(W + "Selector")) for i in sorted(indices)]


def test_the_speakers_and_the_mixer_are_the_shared_block_s():
    # OpenRigs.Audio's SpeakerMixer loads rig.yml's speakers once into Speakers (every filter checked, so a speaker without
    # one stops the workflow at Start, naming it) and starts the mixer no sooner than 3 s after Start.
    assert lint.speaker_mixer_problems(WORKFLOW) == []
    assert "RequireSpeakerFilters" not in TEXT and "RequireSpeakerFilters" not in (ROOT / "src" / "Extensions" / "Corridor.cs").read_text(encoding="utf-8")

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
        assert buffer.findtext(AUD + "Equalise") == "true" and buffer.findtext(AUD + "Gain") is None
        assert not (buffer.findtext(AUD + "Channels") or "").strip()
    nodes, upstream = _audio_chain("PipBuffers")
    assert _labels(nodes, upstream)[-3:] == ["rx:Zip", "Item2", "aud:SpeakerBuffer"] and "rx:ToList" in _labels(nodes, upstream)
    assert "TaskParameters.TonePips.LevelDbfs" in _labels(nodes, upstream)


def test_the_background_noise_plays_unequalised_on_every_speaker():
    nodes, edges = _scope(group("Audio"))
    sent = next(i for i, n in enumerate(nodes) if n.get(XSI) == "MulticastSubject" and n.findtext(W + "Name") == "BackgroundNoise")
    upstream = _upstream(edges, sent)
    assert {"Speakers", "BackgroundNoiseEnabled?", "TaskParameters.BackgroundNoise.LevelDbfs"} <= set(_labels(nodes, upstream))
    buffer = _speaker_buffer(nodes, upstream)
    assert buffer.findtext(AUD + "Equalise") == "false" and not (buffer.findtext(AUD + "Channels") or "").strip()


def test_the_background_noise_starts_at_ready_at_the_launch_s_level():
    # Pip, 15 Nov 2026: the noise starts at Ready, not Go, at the level of the task file as launched (LaunchTaskConfig);
    # its Ready is RigHardware's, the stages its ReadyWaitsFor names.
    nodes, edges = _scope(group("Audio"))
    sent = next(i for i, n in enumerate(nodes) if n.get(XSI) == "MulticastSubject" and n.findtext(W + "Name") == "BackgroundNoise")
    upstream = _upstream(edges, sent)
    names = {nodes[i].findtext(W + "Name") for i in upstream}
    assert {"LaunchTaskConfig", "Ready"} <= names and "TaskConfig" not in names
    # Ready's stages: HarpClockSet, SpoutsHomed (LickSpoutStage.Homed) and MixerStarted (SpeakerMixer's on ReadyStages).
    ready_nodes, ready_edges = _scope(group("Ready"))
    zip_ = next(i for i, n in enumerate(ready_nodes) if combinators(n) == ["rx:Zip"])
    subjects = {ready_nodes[i].findtext(W + "Name") for i in _upstream(ready_edges, zip_) if ready_nodes[i].get(XSI) == "SubscribeSubject"}
    assert subjects == {"HarpClockSet", "LickSpoutStage.Homed", "ReadyStages"}
    hardware = next(e for e in MAIN.iter(W + "Expression") if e.get("Path") == "OpenRigs.Devices:RigHardware.bonsai")
    assert hardware.findtext(W + "ReadyWaitsFor") == ", ".join(lint.READY_STAGES_WITH_MIXER) == "HarpClockSet, SpoutsHomed, MixerStarted"


def test_go_stops_on_a_background_noise_changed_after_launch():
    nodes, edges = _scope(group("Task"))
    (stop,) = [i for i, n in enumerate(nodes) if "core:ThrowError" in combinators(n)]
    labels = _labels(nodes, _upstream(edges, stop))
    assert {"TaskConfig", "LaunchTaskConfig", "BackgroundNoiseChanged?"} <= set(labels)
    read_at_go = next(i for i, n in enumerate(nodes) if n.get(XSI) == "rx:AsyncSubject" and n.findtext(W + "Name") == "TaskConfig")
    assert "ExperimentGo" in _labels(nodes, _upstream(edges, read_at_go))


def test_the_speakers_are_declared_before_every_user():
    # A GroupWorkflow does not scope subjects; keep the declaration ahead of its subscribers.
    nodes, _ = _scope(group("Audio"))
    declared = next(i for i, n in enumerate(nodes) if n.get("Path") == lint.SPEAKER_MIXER)
    users = [i for i, n in enumerate(nodes) if n.get(XSI) == "SubscribeSubject" and n.findtext(W + "Name") == "Speakers"]
    assert len(users) == 4 and min(users) > declared


def test_the_trial_events_leave_the_clock_s_thread_unswitched():
    # Plan v11 W3, audit round 4: CorridorTask serialises its inputs, and nothing downstream blocks, so no ObserveOn.
    assert "rx:ObserveOn" not in combinators(group("TaskLogic"))


def test_ready_the_cameras_and_the_watchdog_are_rig_hardware_s():
    assert lint.rig_hardware_problems(WORKFLOW) == []

# The lab's rules for every workflow (open_rigs.rigs.workflow_lint, one copy in lib-open-rigs).
WORKFLOW = lint.Workflow(ROOT)


def test_every_harp_delay_runs_on_the_timestamp_clock():
    count, off = lint.harp_delays_off_the_clock(WORKFLOW)
    assert count >= 1 and off == []


def test_no_async_event_sender_completes_async_events():
    senders, finite = lint.finite_async_event_senders(WORKFLOW)
    assert senders == 2 and finite == []  # RngSeed and Control; DeliverReward sends RewardDelivered itself


def test_no_trial_graph_closes_a_shared_subject():
    assert lint.subjects_closed_by_a_nested_multicast(WORKFLOW) == []


def test_every_sound_plays_at_the_task_file_s_level():
    assert lint.sound_level_problems(WORKFLOW) == []


def test_zapit_is_the_shared_include_given_the_conditions_file():
    assert lint.zapit_include_problems(WORKFLOW) == []


def test_the_manual_reward_is_the_fixed_drop():
    assert lint.manual_reward_problems(WORKFLOW) == []


def test_the_penalty_sound_is_the_lab_s_at_the_task_s_level():
    assert lint.penalty_sound_problems(WORKFLOW) == []


def test_the_readme_lists_every_open_rigs_block():
    assert lint.readme_missing_blocks(WORKFLOW) == []


def test_the_workflow_declares_every_command_it_handles():
    assert lint.undeclared_commands(WORKFLOW) == []


def test_the_layout_is_tracked():
    assert lint.layout_problems(WORKFLOW) == []


def test_the_cameras_are_logged_by_log_cameras():
    assert lint.camera_log_problems(WORKFLOW) == []


def test_the_rng_seed_is_logged_at_trial_1s_go():
    # Once, at TaskLogic's top level from the first trial's Start (its Go), as coen-dome-decision-making; the Take after the multicast.
    nodes, edges = lint.scope(WORKFLOW.named("TaskLogic"))
    (seed,) = [i for i, n in enumerate(nodes) if n.findtext(lint.W + "Name") == "RngSeed"]
    (start,) = [a for a, b, _ in edges if b == seed]
    assert lint.kind(nodes[start]) == "ext:CorridorEvents" and next(nodes[start].iter("{clr-namespace:;assembly=Extensions}Kind")).text == "Start"
    assert WORKFLOW.text.count("<Name>RngSeed</Name>") == 1


def test_the_pips_carry_their_sync_copy():
    # rig.yml's syncChannel at syncGain for each pip (OpenRigs.Audio SpeakerBuffer.SyncCopy), as coen-dome-decision-making's stimulus.
    nodes, upstream = _audio_chain("PipBuffers")
    assert _speaker_buffer(nodes, upstream).findtext("{clr-namespace:OpenRigs.Audio;assembly=OpenRigs.Audio}SyncCopy") == "true"
