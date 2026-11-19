"""src\\main.bonsai's rules: shared blocks, every task wait on the Harp clock, the logs' completion rules and the names
the stage and README depend on."""

import json
import re
import xml.etree.ElementTree as ET
from pathlib import Path

import open_rigs.rigs
import yaml
from open_rigs.rigs import workflow_checks
from open_rigs.rigs import workflow_lint as lint
from open_rigs.rigs.lab_rig import LabRig

ROOT = Path(__file__).parents[1]
# The workflow checks every open-rigs task runs (lib-open-rigs workflow_checks, one copy); this task's own follow, and a
# test defined below with a shared one's name (the hardware's pinned devices) replaces it.
globals().update(workflow_checks.tests(ROOT))
W = "{https://bonsai-rx.org/2018/workflow}"
XSI = "{http://www.w3.org/2001/XMLSchema-instance}type"
TEXT = (ROOT / "src" / "main.bonsai").read_text(encoding="utf-8-sig")
MAIN = ET.fromstring(TEXT.encode("utf-8"))
OUTCOME_EFFECTS = "OpenRigs.Trials:TrialOutcomeEffects.bonsai"
PLAY_REWARD_TONE = "OpenRigs.Audio:PlayRewardTone.bonsai"
ZAPIT_TRIALS = "OpenRigs.Trials:ZapitTrials.bonsai"


def group(name: str) -> ET.Element:
    return next(e for e in MAIN.iter(W + "Expression") if e.findtext(W + "Name") == name and e.find(W + "Workflow") is not None)


def combinators(element: ET.Element) -> list[str]:
    return [c.get(XSI) for c in element.iter(W + "Combinator")]


def test_no_wall_clock_wait_inside_task_logic():
    # No wall-clock wait inside TaskLogic: the laser cap's backstop (LaserBackstop), should the board stall, is ZapitTrials'.
    assert lint.wall_clock_waits(WORKFLOW) == []
    assert "ext:RunTrials" in combinators(group("TaskLogic"))


def test_the_shared_blocks_replace_the_local_copies():
    includes = {e.get("Path") for e in MAIN.iter(W + "Expression") if e.get(XSI) == "IncludeWorkflow"}
    assert {"OpenRigs.Devices:DomeHardware.bonsai", lint.REWARDS,
            "OpenRigs.Devices:ConnectForTask.bonsai", "OpenRigs.Logging:ExperimentLogs.bonsai",
            lint.LAUNCH_SETTINGS, lint.BACKGROUND_NOISE, lint.EXPERIMENT_RECORDS} <= includes
    # The speakers and the projector are DomeHardware's (every task has background noise and a screen).
    assert not includes & {lint.SPEAKER_MIXER, lint.PROJECTOR_DISPLAY}
    assert not [p for p in includes if p.startswith("Extensions")]
    assert sorted(p.name for p in (ROOT / "src" / "Extensions").iterdir()) == [
        "Curriculum.cs", "README.md", "Sweep.cs", "TaskSchema.Generated.cs", "TrialCondition.cs", "TrialLogic.cs", "TrialRow.cs",
        "UclOpenCoenDomeNavigationTask.Generated.cs", "UclOpenCoenDomeNavigationTaskParameters.cs"]
    assert "Disable" not in {e.get(XSI) for e in MAIN.iter(W + "Expression")}


def test_the_manifest_records_rig_yml_and_every_file_it_names():
    # The pips, reward tone and penalty sound are equalised with rig.yml's speaker filters, so the manifest hashes them:
    # ExperimentManifest's default ConfigFiles, {RigFiles}, is rig.yml, every file it names and the mouse's spout positions.
    assert "<mf:ConfigFiles>" not in TEXT


def test_the_launch_properties():
    # No MissingCalibration: a reward valve without a calibration always stops the workflow.
    top = MAIN.find(W + "Workflow").find(W + "Nodes")
    exposed = {p.get("Name") for e in top if e.get(XSI) == "ExternalizedMapping" for p in e}
    assert {"SubjectId", "SessionId", "LaunchId", "TaskPath", "DisplayDevice", "ConfigRoot"} == exposed
    assert lint.launch_id_problems(WORKFLOW) == []


def test_the_metrics_are_the_view_s():
    names = {c.findtext("{clr-namespace:OpenRigs.Control;assembly=OpenRigs.Control}Name") for c in MAIN.iter(W + "Combinator") if c.get(XSI) == "orcl:ReportMetric"}
    assert names == {"curriculumStage"}
    # The shared ones (trialNumber, trialType as coen-dome-decision-making reports it: the view's type_metric, rewardOfferedUl,
    # laserCondition), OpenRigs.Control's ReportTrialMetrics at its default Members, once, on each trial at its start.
    nodes, edges = _scope(group("Status"))
    (report,) = [i for i, n in enumerate(nodes) if n.find(W + "Combinator") is not None and n.find(W + "Combinator").get(XSI) == "orcl:ReportTrialMetrics"]
    assert TEXT.count("orcl:ReportTrialMetrics") == 1 and "Members" not in ET.tostring(nodes[report], encoding="unicode")
    assert [nodes[a].findtext(W + "Name") for a, b, *_ in edges if b == report] == ["Trial"]
    # curriculumStage is TaskFile's: the task file's curriculum as code-version, found well formed by CheckTaskAtGo.
    assert "TrainingStage" not in TEXT and "trainingStage" not in TEXT and "TrainingCodeVersion" not in TEXT
    nodes, edges = _scope(group("TaskFile"))
    (subject,) = [i for i, n in enumerate(nodes) if n.findtext(W + "Name") == "CurriculumStage"]
    (source,) = [a for a, b, *_ in edges if b == subject]
    assert nodes[source].get(XSI) == "MemberSelector" and nodes[source].findtext(W + "Selector") == "CurriculumStage"
    (metric,) = [b for a, b, *_ in edges if a == subject]
    assert "curriculumStage" in ET.tostring(nodes[metric], encoding="unicode")


def test_stimulus_state_reaches_disk_row_by_row():
    log = next(e for e in group("Logging").iter(W + "Expression")
               if e.get("Path") == "OpenRigs.Logging:LogWorkflowData.bonsai" and e.findtext(W + "LogName") == "WorkflowData_StimulusState")
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


TRL_KIND = "{clr-namespace:OpenRigs.Trials;assembly=OpenRigs.Trials}Kind"


def _picked(nodes: list[ET.Element], i: int) -> str | None:
    """The TrialEvents kind node i picks (lib-open-rigs' PickTrialEvents), or None."""
    return next(nodes[i].iter(TRL_KIND)).text if lint.kind(nodes[i]) == "trl:PickTrialEvents" else None


def test_zapit_s_trials_are_the_shared_include_fed_the_rules():
    # OpenRigs.Trials ZapitTrials, once, given TrialRules: in an optogenetics experiment only (the rules' Laser, so a rig.yml
    # without a zapit block works) each trial's StartStimulus (at Arm on a hardware-triggered rig, rig.yml's
    # zapit.hardwareTriggered through the rules' LaserTiming, else at the Trigger), the laser's stop at the outcome, LaserCap or
    # LaserBackstop, and the ZapitTrigger line raised at Trigger and cleared at TriggerOff. None of it is built here.
    nodes, edges = _scope(group("TaskLogic"))
    (zapit,) = [i for i, n in enumerate(nodes) if n.get("Path") == ZAPIT_TRIALS]
    assert [nodes[a].findtext(W + "Name") for a, b in edges if b == zapit] == ["TrialRules"]
    assert TEXT.count(f'Path="{ZAPIT_TRIALS}"') == 1
    for built in ('"trl:ZapitTrialCommand"', '"zap:StopZapit"', f'"{lint.SET_ZAPIT_TRIGGER_LINE}"', "<Name>Optogenetics?<", "<Name>LaserBackstop<"):
        assert built not in TEXT, built
    logic = (ROOT / "src" / "Extensions" / "TrialLogic.cs").read_text(encoding="utf-8-sig")
    assert "x.Item2.Zapit != null && x.Item2.Zapit.HardwareTriggered" in logic and "LaserTiming.UntilOutcome(hardwareTriggered," in logic


def test_the_trial_loop_starts_on_the_clock_thread():
    # The dome tasks' one TaskLogic shape (lib-open-rigs TrialLoop): TrialRules reaches RunTrials from lib-open-rigs'
    # OnNextTick on the Harp clock's thread, not from a wall-clock Delay, and RunTrials' events go into TrialEvents.
    nodes, edges = _scope(group("TaskLogic"))
    (task,) = [i for i, n in enumerate(nodes) if "ext:RunTrials" in combinators(n)]
    (tick,) = [i for i, n in enumerate(nodes) if "core:OnNextTick" in combinators(n)]
    rules = [i for i, n in enumerate(nodes) if n.get(XSI) == "SubscribeSubject" and n.findtext(W + "Name") == "TrialRules"]
    assert tick in _upstream(edges, task) and set(rules) & _upstream(edges, task)
    assert [nodes[b].findtext(W + "Name") for a, b in edges if a == task] == ["TrialEvents"]
    (trial,) = [i for i, n in enumerate(nodes) if n.get(XSI) == "MulticastSubject" and n.findtext(W + "Name") == "Trial"]
    assert [_picked(nodes, a) for a in _upstream(edges, trial) if _picked(nodes, a)] == ["Start"]   # the trial published at its Start
    assert "rx:Delay" not in TEXT


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
    assert "RequireSpeakerFilters" not in TEXT and "RequireSpeakerFilters" not in (ROOT / "src" / "Extensions" / "Sweep.cs").read_text(encoding="utf-8")

def _speaker_buffer(nodes: list[ET.Element], upstream: set[int]) -> ET.Element:
    found = [c for i in upstream for c in nodes[i].iter(W + "Combinator") if c.get(XSI) == "aud:SpeakerBuffer"]
    assert len(found) == 1
    return found[0]


def test_the_pips_and_the_reward_tone_play_equalised_on_every_speaker():
    # Each sound is one row laid out on rig.yml's channels, equalised for each speaker: the reward tone by OpenRigs.Audio's
    # PlayRewardTone (SpeakerBuffer at the highest rewardTone.levelDbfs on every speaker, played at the trial's) on each rewarded
    # outcome TrialOutcomeEffects passes on, its input the trial's RewardToneLevelDbfs, from this repo's file; the pips by
    # LayOutPips (PipSet: SpeakerSet.Layout on every speaker as stored, checked at the highest level the conditions draw,
    # TrialRules.MaxStimulusLevel), fed Zip(MaxStimulusLevel, Sweep, Zip(Speakers, pips)).
    nodes, edges = _scope(group("TaskLogic"))
    (tone,) = [i for i, n in enumerate(nodes) if n.get("Path") == PLAY_REWARD_TONE]
    assert nodes[tone].findtext(W + "RewardToneFile") == "./sounds/reward_tone.bin"
    assert [nodes[i].get("Path") or nodes[i].findtext(W + "Name") or nodes[i].findtext(W + "Selector") for i in sorted(_upstream(edges, tone))][-3:] == [
        OUTCOME_EFFECTS, "Rewarded?", "RewardToneLevelDbfs"]
    (level,) = [a for a, b in edges if b == tone]
    assert nodes[level].get(XSI) == "MemberSelector" and nodes[level].findtext(W + "Selector") == "RewardToneLevelDbfs"
    (before,) = [a for a, b in edges if b == level]
    assert nodes[before].findtext(W + "Name") == "Rewarded?"
    assert "RewardToneBuffer" not in ET.tostring(group("Audio"), encoding="unicode")
    nodes, upstream = _audio_chain("StimulusSoundBuffers")
    labels = _labels(nodes, upstream)
    assert any("./sounds/pips.bin" in ET.tostring(nodes[i], encoding="unicode") for i in upstream)
    assert not any("all_waveforms" in ET.tostring(n, encoding="unicode") for n in nodes)
    assert {"rx:Zip", "ext:LayOutPips", "rx:ToList", "Speakers", "TrialRules", "MaxStimulusLevel", "Sweep"} <= set(labels), labels
    # LayOutPips is fed Zip(MaxStimulusLevel, Sweep, Zip(Speakers, pips)): the highest level, the sweep, then the speakers with the pips.
    _, labelled = lint.scope(group("Audio"))
    (layout,) = [i for i, n in enumerate(nodes) if "ext:LayOutPips" in combinators(n)]
    (zipped,) = [a for a, b, _ in labelled if b == layout]

    def name(i: int) -> str:
        return nodes[i].findtext(W + "Selector") or nodes[i].findtext(W + "Name") or combinators(nodes[i])[0]

    feeds = {label: a for a, b, label in labelled if b == zipped}
    assert [name(feeds[k]) for k in ("Source1", "Source2", "Source3")] == ["MaxStimulusLevel", "Sweep", "rx:Zip"]
    inner = {label: a for a, b, label in labelled if b == feeds["Source3"]}
    assert [name(inner[k]) for k in ("Source1", "Source2")] == ["Speakers", "rx:ToList"]
    assert "aud:SpeakerBuffer" not in labels and "dsp:Tile" not in labels
    sweep = (ROOT / "src" / "Extensions" / "Sweep.cs").read_text(encoding="utf-8-sig")
    assert "speakers.Layout(pip, null, 1, true, true)" in sweep


def test_the_background_noise_is_the_shared_include():
    # OpenRigs.Audio's BackgroundNoise: the noise unequalised on every speaker
    # from Ready. The noise is the lab's one file, rig.yml's
    # speakerArray.backgroundNoise (on the server's config\shared): the include takes no file and the repo keeps none (the lint).
    assert lint.background_noise_problems(WORKFLOW) == []
    (noise,) = [e for e in MAIN.iter(W + "Expression") if e.get("Path") == lint.BACKGROUND_NOISE]
    assert noise.find(W + "FileName") is None
    assert not list((ROOT / "src").rglob("*noise*.wav"))


def test_the_speakers_are_declared_before_every_user():
    # A GroupWorkflow does not scope subjects; keep the declaration (DomeHardware's SpeakerMixer) ahead of its subscribers.
    top, _ = _scope(MAIN)
    declared = next(i for i, n in enumerate(top) if n.get("Path") == "OpenRigs.Devices:DomeHardware.bonsai")
    users = [i for i, n in enumerate(top) for s in n.iter(W + "Expression")
             if s.get(XSI) == "SubscribeSubject" and s.findtext(W + "Name") == "Speakers"]
    users += [i for i, n in enumerate(top) for s in n.iter(W + "Expression") if s.get("Path") in (lint.BACKGROUND_NOISE, OUTCOME_EFFECTS, PLAY_REWARD_TONE)]
    assert len(users) == 4 and min(users) > declared


def test_the_trial_events_leave_the_clock_s_thread_unswitched():
    # RunTrials (TrialLoop.Observe) serialises its inputs, and nothing downstream blocks, so no ObserveOn.
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
    # TrialLoop times every task wait itself (the trigger line's hold is its TriggerOff); a HarpDelay added must read Timestamp.
    _, off = lint.harp_delays_off_the_clock(WORKFLOW)
    assert off == []


def test_every_sound_plays_at_the_task_file_s_level():
    assert lint.sound_level_problems(WORKFLOW) == []


def test_every_reward_goes_through_the_lab_s_rewards():
    # OpenRigs.Devices' Rewards (RewardRequests, RewardDelivered), then one RewardValve per valve, before TaskLogic.
    assert lint.reward_problems(WORKFLOW) == []


def test_the_rng_seed_is_logged_at_trial_1s_start():
    # OpenRigs.Logging's LogRngSeed at TaskLogic's top level, given TrialRules' Seed at each trial's Start.
    assert lint.rng_seed_problems(WORKFLOW) == []
    nodes, edges = lint.scope(WORKFLOW.named("TaskLogic"))
    (log,) = [i for i, n in enumerate(nodes) if n.get("Path") == lint.LOG_RNG_SEED]
    (seed,) = [a for a, b, _ in edges if b == log]
    (pair,) = [a for a, b, _ in edges if b == seed]
    assert nodes[seed].findtext(W + "Selector") == "Item2.Seed" and lint.kind(nodes[pair]) == "rx:WithLatestFrom"
    sources = {label: a for a, b, label in edges if b == pair}
    assert _picked(nodes, sources["Source1"]) == "Start" and nodes[sources["Source2"]].findtext(W + "Name") == "TrialRules"


def test_every_device_is_opened_by_its_piece():
    assert lint.hand_opened_devices(WORKFLOW) == []


def test_each_trial_s_sound_carries_its_sync_copy():
    # rig.yml's syncChannel at syncGain for each pip (SpeakerSet.Layout's syncCopy, kept unscaled at any amplitude: PipSetTests),
    # as coen-dome-decision-making's stimulus.
    sweep = (ROOT / "src" / "Extensions" / "Sweep.cs").read_text(encoding="utf-8-sig")
    assert "speakers.Layout(pip, null, 1, true, true)" in sweep and "ext:LayOutPips" in _labels(*_audio_chain("StimulusSoundBuffers"))


def test_the_sweep_is_read_at_start_and_checked_against_rig_yml_s_rate():
    # Sweep.json is read at Start (LoadSweep, from rig.yml's speakerArray.device.sampleRate), so a wrong
    # working folder or a waveform rate other than the audio device's stops the run at Start, not at Go.
    nodes, edges = _scope(group("TaskFile"))
    (sweep,) = [i for i, n in enumerate(nodes) if n.get(XSI) == "rx:AsyncSubject" and n.findtext(W + "Name") == "Sweep"]
    labels = _labels(nodes, _upstream(edges, sweep))
    assert labels == ["RigConfigs", "SpeakerArray.Device.SampleRate", "ext:LoadSweep"]
    assert "ExperimentGo" not in labels
    (setup,) = [i for i, n in enumerate(nodes) if "ext:CheckTaskAtGo" in combinators(n)]
    zip_ = next(a for a, b in edges if b == setup)
    assert [nodes[a].findtext(W + "Name") for a, b in sorted(edges, key=lambda e: e[0]) if b == zip_] == ["TaskText", "RigConfigs", "Sweep"]
    assert "SweepFile" not in ET.tostring(nodes[setup], encoding="unicode")
    # The pips' reader takes its rate and its 50 ms pip's length from the sweep, not typed values.
    nodes, upstream = _audio_chain("StimulusSoundBuffers")
    (mapping,) = [nodes[i] for i in upstream if nodes[i].get(XSI) == "PropertyMapping"]
    assert {p.get("Name"): p.get("Selector") for p in mapping.iter(W + "Property")} == {"SampleRate": "SampleRate", "BufferLength": "PipSamples"}
    assert "Sweep" in _labels(nodes, upstream)


def test_the_waveforms_are_at_the_lab_rig_s_rate():
    # The committed waveforms' rate (sweep.json) is the rig template's audio device's, as every dome's rig.yml.
    rig = LabRig.model_validate(yaml.safe_load((Path(open_rigs.rigs.__file__).parent / "rig-template.yml").read_text(encoding="utf-8").replace("CHANGE-ME", "X")))
    sweep = json.loads((ROOT / "src" / "sounds" / "sweep.json").read_text(encoding="utf-8"))
    assert sweep["sampleRate"] == rig.speaker_array.device.sample_rate == 48000


def test_run_trials_reads_both_lick_streams():
    # The quiet wait before a trial starts holds for a lick on either spout (as coen-dome-decision-making's): RunTrials takes
    # LickStateLeft as Source4 and LickStateRight as Source5, Zapit's replies Source6.
    logic = group("TaskLogic")
    nodes = [e for e in logic.find(W + "Workflow").find(W + "Nodes")]
    names = {i: (e.findtext(W + "Name") or "") for i, e in enumerate(nodes)}
    run = next(i for i, e in enumerate(nodes) if e.find(W + "Combinator") is not None and e.find(W + "Combinator").get(XSI) == "ext:RunTrials")
    feeds = {edge.get("Label"): names[int(edge.get("From"))] for edge in logic.find(W + "Workflow").find(W + "Edges") if int(edge.get("To")) == run}
    assert feeds["Source4"] == "LickStateLeft"
    assert feeds["Source5"] == "LickStateRight"
    assert feeds["Source6"] == "ZapitReplies"


def test_one_grating_subject_sets_the_grating():
    # One Grating subject (GratingStates over every trial event) maps spatial frequency, opacity and contrast onto
    # DrawGratings at once: each pip's (the trial's visualContrast and visualPhaseRad among them), and opacity 0 at the outcome.
    nodes, edges = _scope(group("Display"))
    (subject,) = [i for i, n in enumerate(nodes) if n.findtext("{clr-namespace:Bonsai.Reactive;assembly=Bonsai.Core}Name") == "Grating"]
    assert nodes[subject].get("TypeArguments") == "ext:GratingState"
    (mapping,) = [b for a, b in edges if a == subject]
    assert [(p.get("Name"), p.get("Selector")) for p in nodes[mapping].iter(W + "Property")] == [
        ("SpatialFrequency", "SpatialFrequencyCpd"), ("Opacity", "Opacity"), ("Contrast", "Contrast"),
        ("Phase", "PhaseRad")]
    assert [nodes[b].get("Path") for a, b in edges if a == mapping] == ["BonVision:Primitives.DrawGratings.bonsai"]
    nodes, edges = _scope(group("TaskLogic"))
    (multicast,) = [i for i, n in enumerate(nodes) if n.get(XSI) == "MulticastSubject" and n.findtext(W + "Name") == "Grating"]
    (states,) = [a for a, b in edges if b == multicast]
    assert combinators(nodes[states]) == ["ext:GratingStates"]
    (source,) = [a for a, b in edges if b == states]
    assert nodes[source].get(XSI) == "SubscribeSubject" and nodes[source].findtext(W + "Name") == "TrialEvents"
    assert not re.search(r"<(?:rx:)?Name>Grating\w", TEXT)
