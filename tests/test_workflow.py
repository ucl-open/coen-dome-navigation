"""src\\main.bonsai's rules: shared blocks, every task wait on the Harp clock, the logs' completion rules and the names
the stage and README depend on."""

import json
import re
import xml.etree.ElementTree as ET
from pathlib import Path

import open_rigs.rigs
import yaml
from open_rigs.rigs import workflow_lint as lint
from open_rigs.rigs.lab_rig import LabRig

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
    assert {"OpenRigs.Devices:DomeHardware.bonsai", "OpenRigs.Devices:BehaviorBoard.ManualRewards.bonsai",
            "OpenRigs.Devices:Zapit.ConnectForTask.bonsai", "OpenRigs.Logging:LogLabExperiment.bonsai",
            lint.LAUNCH_SETTINGS, lint.BACKGROUND_NOISE, lint.EXPERIMENT_RECORDS} <= includes
    # The speakers and the projector are DomeHardware's (Pip, 15 Nov 2026: every task has background noise and a screen).
    assert not includes & {lint.SPEAKER_MIXER, lint.PROJECTOR_DISPLAY}
    assert not [p for p in includes if p.startswith("Extensions")]
    assert sorted(p.name for p in (ROOT / "src" / "Extensions").iterdir()) == [
        "Corridor.cs", "UclOpenCoenDomeNavigationTask.Generated.cs"]
    assert "Disable" not in {e.get(XSI) for e in MAIN.iter(W + "Expression")}


def test_every_config_root_is_the_lab_s():
    # check-workflow holds every node that reads rig.yml to one root (3 nodes: ReadLabRig, ExperimentRecords, LiveView); this holds that root to the lab's.
    assert set(re.findall(r"<(?:\w+:)?ConfigRoot>([^<]*)</", TEXT)) == {r"C:\Coen-Lab-Local\RigConfigs"}


def test_the_manifest_records_rig_yml_and_every_file_it_names():
    # The pips, reward tone and penalty sound are equalised with rig.yml's speaker filters, so the manifest hashes them:
    # ExperimentManifest's default ConfigFiles, {RigFiles}, is rig.yml, every file it names and the mouse's spout positions.
    assert "<mf:ConfigFiles>" not in TEXT


def test_the_launch_properties():
    # No MissingCalibration: a reward valve without a calibration always stops the workflow (Pip, 15 Nov 2026).
    top = MAIN.find(W + "Workflow").find(W + "Nodes")
    exposed = {p.get("Name") for e in top if e.get(XSI) == "ExternalizedMapping" for p in e}
    assert {"SubjectId", "SessionId", "TaskPath", "DisplayDevice"} == exposed


def test_the_metrics_are_the_view_s():
    names = {c.findtext("{clr-namespace:OpenRigs.Control;assembly=OpenRigs.Control}Name") for c in MAIN.iter(W + "Combinator") if c.get(XSI) == "orcl:ReportMetric"}
    # path is LogLabExperiment's.
    assert names == {"trialNumber", "rewardSizeUl", "laserCondition"}
    # trainingStage is OpenRigs.Control's TrainingStage's (16 Nov 2026).
    assert lint.training_stage_problems(WORKFLOW) == []


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
    lines = [i for i, n in enumerate(nodes) if n.get("Path") == lint.SET_ZAPIT_TRIGGER_LINE]
    assert [nodes[i].findtext(W + "Set") for i in lines] == ["true", "false"]
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


def test_the_background_noise_is_the_shared_include():
    # OpenRigs.Audio's BackgroundNoise (16 Nov 2026): the noise unequalised on every speaker at the launched task file's
    # level from Ready, Go stopping on a backgroundNoise changed after launch. The noise is the lab's one file, rig.yml's
    # speakerArray.backgroundNoise (16 Nov 2026, this repo's old background_noise.wav byte for byte, now on the server's
    # config\shared): the include takes no file and the repo keeps none (the lint).
    assert lint.background_noise_problems(WORKFLOW) == []
    (noise,) = [e for e in MAIN.iter(W + "Expression") if e.get("Path") == lint.BACKGROUND_NOISE]
    assert noise.find(W + "FileName") is None
    assert not list((ROOT / "src").rglob("*noise*.wav"))


def test_the_speakers_are_declared_before_every_user():
    # A GroupWorkflow does not scope subjects; keep the declaration (DomeHardware's SpeakerMixer) ahead of its subscribers.
    top, _ = _scope(MAIN)
    declared = next(i for i, n in enumerate(top) if n.findtext(W + "Name") == "Hardware")
    users = [i for i, n in enumerate(top) for s in n.iter(W + "Expression")
             if s.get(XSI) == "SubscribeSubject" and s.findtext(W + "Name") == "Speakers"]
    users += [i for i, n in enumerate(top) for s in n.iter(W + "Expression") if s.get("Path") in (lint.BACKGROUND_NOISE, lint.PLAY_PENALTY_SOUND)]
    assert len(users) == 4 and min(users) > declared


def test_the_trial_events_leave_the_clock_s_thread_unswitched():
    # Plan v11 W3, audit round 4: CorridorTask serialises its inputs, and nothing downstream blocks, so no ObserveOn.
    assert "rx:ObserveOn" not in combinators(group("TaskLogic"))


def test_the_hardware_is_the_lab_s_pieces():
    # DomeHardware's pieces, one clock, Ready waiting for every stage they announce, MixerStarted included.
    assert lint.hardware_problems(WORKFLOW) == []
    # DomeHardware's devices, pinned: dropping one from the list fails here, not at the rig.
    assert lint.required_devices(WORKFLOW) == [
        ("TimestampGeneratorGen3-Main", "timestampGeneratorGen3"), ("BehaviorBoard-Main", "behaviorBoard"),
        ("LicketySplit-Left", "licketySplit"), ("LicketySplit-Right", "licketySplit"),
        ("StepperDriver-A", "stepperDriver"), ("StepperDriver-B", "stepperDriver"),
        ("Camera-Face", "camera"), ("Camera-Pupil", "camera"), ("Camera-Body", "camera")]

# The lab's rules for every workflow (open_rigs.rigs.workflow_lint, one copy in lib-open-rigs).
WORKFLOW = lint.Workflow(ROOT)


def test_every_harp_delay_runs_on_the_timestamp_clock():
    count, off = lint.harp_delays_off_the_clock(WORKFLOW)
    assert count >= 1 and off == []


def test_no_async_event_sender_completes_async_events():
    senders, finite = lint.finite_async_event_senders(WORKFLOW)
    assert senders == 0 and finite == []  # LogRngSeed sends RngSeed, LogControlEvents Control and DeliverReward RewardDelivered


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


def test_every_command_is_declared_once_by_the_task_or_its_piece():
    # The pieces declare their own (LickSpoutStage's spout commands and SpoutStepSize, ManualRewards' RewardLeft
    # and RewardRight, 16 Nov 2026): a task's copy stops ControlServer at Start.
    assert lint.undeclared_commands(WORKFLOW) == [] and lint.declared_twice(WORKFLOW) == []


def test_spout_step_size_is_the_lick_spout_stage_s():
    assert lint.spout_step_size_problems(WORKFLOW) == []


def test_the_experiment_records_are_the_shared_include():
    assert lint.experiment_records_problems(WORKFLOW) == []


def test_the_launch_settings_are_the_shared_include():
    assert lint.launch_settings_problems(WORKFLOW) == []


def test_the_layout_is_tracked():
    assert lint.layout_problems(WORKFLOW) == []


def test_the_experiment_logs_are_the_lab_s():
    assert lint.experiment_log_problems(WORKFLOW) == []


def test_every_file_the_workflow_writes_is_in_the_manifest_s_expected_files():
    assert lint.expected_files_problems(WORKFLOW) == []


def test_no_fixed_width_pulse_reaches_a_reward_valve():
    # Every reward is DeliverReward's calibrated pulse; checked against the lab's rig template's valve lines.
    rig = LabRig.model_validate(yaml.safe_load((Path(open_rigs.rigs.__file__).parent / "rig-template.yml").read_text(encoding="utf-8")))
    assert rig.reward_valve_lines() and lint.fixed_width_pulses_on_reward_valves(WORKFLOW, rig.reward_valve_lines()) == []


def test_the_rng_seed_is_logged_at_trial_1s_start():
    # OpenRigs.Logging's LogRngSeed at TaskLogic's top level, given each trial's seed from its Start.
    assert lint.rng_seed_problems(WORKFLOW) == []
    nodes, edges = lint.scope(WORKFLOW.named("TaskLogic"))
    (log,) = [i for i, n in enumerate(nodes) if n.get("Path") == lint.LOG_RNG_SEED]
    (seed,) = [a for a, b, _ in edges if b == log]
    (start,) = [a for a, b, _ in edges if b == seed]
    assert nodes[seed].findtext(W + "Selector") == "Trial.rngSeed"
    assert lint.kind(nodes[start]) == "ext:CorridorEvents" and next(nodes[start].iter("{clr-namespace:;assembly=Extensions}Kind")).text == "Start"


def test_the_task_logs_are_the_shared_include():
    assert lint.task_logs_problems(WORKFLOW) == []


def test_the_pips_carry_their_sync_copy():
    # rig.yml's syncChannel at syncGain for each pip (OpenRigs.Audio SpeakerBuffer.SyncCopy), as coen-dome-decision-making's stimulus.
    nodes, upstream = _audio_chain("PipBuffers")
    assert _speaker_buffer(nodes, upstream).findtext("{clr-namespace:OpenRigs.Audio;assembly=OpenRigs.Audio}SyncCopy") == "true"


def test_the_sweep_is_read_at_start_and_checked_against_rig_yml_s_rate():
    # Pip, 15 Nov 2026: sweep.json is read at Start (LoadSweep, from rig.yml's speakerArray.device.sampleRate), so a wrong
    # working folder or a waveform rate other than the audio device's stops the run at Start, not at Go.
    nodes, edges = _scope(group("Task"))
    (sweep,) = [i for i, n in enumerate(nodes) if n.get(XSI) == "rx:AsyncSubject" and n.findtext(W + "Name") == "Sweep"]
    labels = _labels(nodes, _upstream(edges, sweep))
    assert labels == ["RigConfigs", "SpeakerArray.Device.SampleRate", "ext:LoadSweep"]
    assert "ExperimentGo" not in labels
    (setup,) = [i for i, n in enumerate(nodes) if "ext:CreateCorridorSetup" in combinators(n)]
    zip_ = next(a for a, b in edges if b == setup)
    assert [nodes[a].findtext(W + "Name") for a, b in sorted(edges, key=lambda e: e[0]) if b == zip_] == ["TaskConfig", "TrainingStage", "RigConfigs", "Sweep"]
    assert "SweepFile" not in ET.tostring(nodes[setup], encoding="unicode")
    # The pips' reader takes its rate and its 50 ms pip's length from the sweep, not typed values.
    nodes, upstream = _audio_chain("PipBuffers")
    (mapping,) = [nodes[i] for i in upstream if nodes[i].get(XSI) == "PropertyMapping"]
    assert {p.get("Name"): p.get("Selector") for p in mapping.iter(W + "Property")} == {"SampleRate": "SampleRate", "BufferLength": "PipSamples"}
    assert "Sweep" in _labels(nodes, upstream)


def test_the_waveforms_are_at_the_lab_rig_s_rate():
    # The committed waveforms' rate (sweep.json) is the rig template's audio device's, as every dome's rig.yml.
    rig = LabRig.model_validate(yaml.safe_load((Path(open_rigs.rigs.__file__).parent / "rig-template.yml").read_text(encoding="utf-8")))
    sweep = json.loads((ROOT / "src" / "waveforms" / "sweep.json").read_text(encoding="utf-8"))
    assert sweep["sampleRate"] == rig.speaker_array.device.sample_rate == 48000
