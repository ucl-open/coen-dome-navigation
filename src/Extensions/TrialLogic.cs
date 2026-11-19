using Bonsai;
using OpenRigs.Core;
using OpenRigs.Trials;
using OpenRigs.Devices.Zapit;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.IO;
using System.Reactive.Linq;
using OpenRigs.Devices.Rigs;
using UclOpenCoenDomeNavigationTask;
using YamlDotNet.RepresentationModel;
using BuiltTrialCondition = OpenRigs.Trials.BuiltTrialCondition<UclOpenCoenDomeNavigationTask.UclOpenCoenDomeNavigationTaskParameters>;

// The task in tested C#: each trial's draw, its timing and its run along the virtual track, as the rules
// lib-open-rigs' TrialLoop runs on the Harp clock (RunTrials). main.bonsai drives the hardware from the loop's events;
// tests\Extensions.Tests runs the same loop and rules on stand-in clocks.

/// <summary>
/// The distance along the track in wheel cm: the distance run since the trial's start, less the furthest the mouse has run
/// backwards (it cannot go behind the start), and no more than the track's length (Tom Childs' TrialDistFloor).
/// </summary>
public class VirtualTrack
{
    readonly double length;
    readonly double startCm;
    double floor;

    public VirtualTrack(double lengthCm, double startWheelCm)
    {
        length = lengthCm;
        startCm = startWheelCm;
    }

    public double Position { get; private set; }

    public double Update(double wheelCm)
    {
        var run = wheelCm - startCm;
        floor = Math.Min(floor, run);
        Position = Math.Min(run - floor, length);
        return Position;
    }

    public bool AtEnd { get { return Position >= length; } }
}

/// <summary>One trial as drawn: the lab's conditioned trial (lib-open-rigs ConditionedTrial, the trial log's shared columns) and
/// this task's own draws; every other value the rules read from its combination (values).</summary>
public class Trial : ConditionedTrial
{
    public double trackLengthWheelCm;   // wheel cm to the track's end: the drawn track cm / the combination's track.wheelGain
    public double stimulusOffFraction;  // a stimulus-off trial's track fraction (stimulusOff.trackFractions); NaN: every stimulus stays on
    public bool auditoryOff;            // on a stimulus-off trial, whether the tone switches off (stimulusOff.stimuli)
    public bool visualOff;              // and the grating
    internal UclOpenCoenDomeNavigationTaskParameters values;  // its combination's values, which the rules read per trial

    /// <summary>The track's length drawn for it, in track cm.</summary>
    public double TrackLengthCm { get { return values.Track.LengthsCm[0]; } }

    /// <summary>Track cm per wheel cm, its combination's track.wheelGain.</summary>
    public double WheelGain { get { return values.Track.WheelGain; } }
}

/// <summary>A trial's outcome (lib-open-rigs' TrialOutcome): its row, the reward at the left valve, the penalty at the trial's
/// level, the timeout before the next interval, and the reward tone's level (PlayRewardTone).</summary>
public class TrialOutcome : OpenRigs.Trials.TrialOutcome<TrialRow>
{
    public TrialOutcome(TrialRow row, double penaltyLevelDbfs, double rewardToneLevelDbfs) : base(row, "left", penaltyLevelDbfs)
    {
        RewardToneLevelDbfs = rewardToneLevelDbfs;
    }

    /// <summary>The trial's rewardTone.levelDbfs in dBFS RMS, NaN when mute: what PlayRewardTone plays a rewarded outcome at.</summary>
    public double RewardToneLevelDbfs { get; private set; }
}

/// <summary>
/// This task's rules for lib-open-rigs' TrialLoop, with everything fixed for the experiment that the trials are
/// drawn from and run on. The loop times each trial: its interval from the last outcome (after any
/// timeout; trial 1 from the first clock tick), a hardware-triggered Zapit armed during it once the last trial's trigger
/// line has fallen, the trial start (step 0) at the interval's end less the lead, max(laserToStimulusSeconds, 0), or later
/// until the quiet waits hold, the trigger laserToStimulusSeconds before the first pip, the first pip the lead
/// after the start. These rules run the trial along the virtual track: a pip every 50 ms on the trial start's grid, the
/// left spout's licks from the first pip, the end of the track, and the outcome.
/// </summary>
public class TrialRules : ITrialRules<Trial, StimulusFrame, TrialOutcome>
{
    public const double StepSeconds = 0.05;       // task.py STEP_SECONDS
    public const double LaserCapMarginSeconds = 0.25; // task.py LASER_CAP_MARGIN_SECONDS
    public const double MaxLeadSeconds = 0.2;            // task.py MAX_LEAD_SECONDS: the furthest the trigger may come before the first pip
    public const double UnlimitedWindowTriggerAfterSeconds = 2.0; // task.py UNLIMITED_WINDOW_TRIGGER_AFTER_SECONDS: the furthest after the first pip it may come on a stage without responseWindowSeconds

    readonly UclOpenCoenDomeNavigationTaskParameters task;
    readonly Sweep sweep;
    readonly TrialDraw draw;
    readonly LaserTiming laser;
    VirtualTrack track;
    StimulusFrame last;
    int nextStep, wrongLicks;
    double auditoryOffPositionCm, visualOffPositionCm;  // where each switched off this trial, in track cm; NaN: it never did

    /// <summary>The rules for a task file's text; zapitConditions gives a conditions file's count and max ramp-down in
    /// seconds (null: ZapitConditions.Check reads the file).</summary>
    public TrialRules(string taskText, Sweep sweep, bool hardwareTriggered, Func<string, ZapitConditionsFile> zapitConditions, EqualisedSpeakerArray speakers = null)
    {
        this.sweep = sweep;
        draw = new TrialDraw(taskText, sweep, zapitConditions, speakers);
        task = draw.Task;
        // Every StartStimulus continuous: the laser on from the trigger until the outcome or LaserCap (the cap derived at
        // Go), the trigger line (and the masking light on it) held the conditions file's max ramp-down past the outcome,
        // the next trial armed once it falls.
        laser = task.Optogenetics.Enabled ? LaserTiming.UntilOutcome(hardwareTriggered, draw.MaxRampDownSeconds, draw.LaserCapSeconds) : null;
    }

    /// <summary>The rules for a task file's text (TaskText) with the sweep, Zapit's hardware triggering and rig.yml's speakerArray
    /// (null: none, so only initialAzimuthsDeg "all"); throws naming every problem (TrialDraw.CheckTask).</summary>
    public static TrialRules Create(string taskText, Sweep sweep, bool hardwareTriggered, Func<string, ZapitConditionsFile> zapitConditions, EqualisedSpeakerArray speakers = null)
    {
        return new TrialRules(taskText, sweep, hardwareTriggered, zapitConditions, speakers);
    }

    /// <summary>The stage as code-version (S03A-k7f2) from the task file's curriculum, which the check has found well formed.</summary>
    public string CurriculumStage { get { return task.Curriculum.Code + "-" + task.Curriculum.Version; } }

    /// <summary>The highest tone the task's conditions can draw (OpenRigs.Trials MaxSoundLevel.Of), for the pips' peak check (LayOutPips).</summary>
    public MaxSoundLevel MaxStimulusLevel { get { return MaxSoundLevel.Of(draw.Conditions, t => t.AuditoryStimulus.LevelDbfs); } }

    /// <summary>The highest penalty.sound.levelDbfs any condition can draw (NaN: every one mute), PlayPenaltySound's buffer level.</summary>
    public double MaxPenaltyLevelDbfs { get { return MaxSoundLevel.Of(draw.Conditions, t => t.Penalty.Sound.LevelDbfs).LevelDbfs; } }

    /// <summary>The highest rewardTone.levelDbfs any condition can draw (NaN: every one mute), PlayRewardTone's buffer level.</summary>
    public double MaxRewardToneLevelDbfs { get { return MaxSoundLevel.Of(draw.Conditions, t => t.RewardTone.LevelDbfs).LevelDbfs; } }

    /// <summary>Settings_Combinations' text (OpenRigs.Trials TrialConditions.CombinationsJson): every combination built at Go, each a row the trial
    /// log's trialCombinationId names; logged by ConditionedTaskLogs in the Logging group.</summary>
    public string CombinationsJson { get { return TrialConditions.CombinationsJson(task, draw.Conditions, TrialDraw.Schema); } }

    /// <summary>The seed every draw follows from (TrialDraw.Seed: the task's rngSeed, or one drawn at Go when it is null),
    /// logged once at trial 1's start (LogRngSeed).</summary>
    public int Seed { get { return draw.Seed; } }

    /// <summary>The laser in an optogenetics experiment (the trigger line only rises in one); null without.</summary>
    public LaserTiming Laser { get { return laser; } }

    public Trial Draw(TrialOutcome previous)
    {
        return draw.Next();
    }

    /// <summary>The first pip's step after the trial start: the lead, max(laserToStimulusSeconds, 0), in steps, in every experiment.</summary>
    public static int FirstPipStep(Trial trial)
    {
        return Math.Max((int)Math.Round(trial.values.Optogenetics.LaserToStimulusSeconds / StepSeconds), 0);
    }

    public TrialTiming Timing(Trial trial)
    {
        var structure = trial.values.TrialStructure;
        return new TrialTiming
        {
            ItiSeconds = trial.itiSeconds,
            // The lead applies in every experiment, so timing never reveals Zapit.
            LaserToStimulusSeconds = trial.values.Optogenetics.LaserToStimulusSeconds,
            LickQuietSeconds = structure.LickQuietSeconds,
            WheelQuietSeconds = structure.WheelQuietSeconds,
            WheelQuietCm = structure.WheelQuietCm,
            // Every trial of an optogenetics experiment sends Zapit a condition; laser-off trials at 0 mW (Pip, decision 11).
            Laser = task.Optogenetics.Enabled,
            ResponseWindowSeconds = structure.ResponseWindowSeconds ?? double.PositiveInfinity
        };
    }

    public void Start(Trial trial, TrialTimes times, double wheelCm)
    {
        var wheel = double.IsNaN(wheelCm) ? 0 : wheelCm;
        track = new VirtualTrack(trial.trackLengthWheelCm, wheel);
        track.Update(wheel);
        nextStep = FirstPipStep(trial);
        wrongLicks = 0;
        last = null;
        auditoryOffPositionCm = double.NaN;
        visualOffPositionCm = double.NaN;
    }

    public void Stimulus(Trial trial, TrialTimes times, ICollection<StimulusFrame> updates)
    {
        // The stimulus's tick is the first pip (the lead is whole steps): Tick plays it.
    }

    /// <summary>A pip every 50 ms on the trial start's grid; after a gap in the Harp clock only the latest step due, the
    /// missed pips not played at once. An auto-reward trial ends on the pip that reaches the reward zone's floor.</summary>
    public TrialOutcome Tick(Trial trial, TrialTimes times, ICollection<StimulusFrame> updates)
    {
        var due = Math.Max((int)Math.Floor((times.Now - times.TrialStartTime + 1e-9) / StepSeconds), FirstPipStep(trial));
        if (due < nextStep) return null;
        nextStep = due + 1;
        last = Frame(trial, due);
        updates.Add(last);
        return trial.autoReward && last.Row.auditoryFrequencyHz >= trial.values.RewardZoneMinHz ? End(trial, "rewardZoneReached", times) : null;
    }

    /// <summary>The left spout's licks count from the first pip: in the zone rewarded, outside toward wrongLickLimit;
    /// an auto-reward trial's licks are ignored.</summary>
    public TrialOutcome Lick(Trial trial, int spout, TrialTimes times, ICollection<StimulusFrame> updates)
    {
        if (spout != 0 || last == null || trial.autoReward) return null;
        if (OnTarget(trial, last.Row.auditoryFrequencyHz)) return End(trial, "correctLick", times);
        wrongLicks++;
        var limit = trial.values.WrongLickLimit;
        return limit > 0 && wrongLicks >= limit ? End(trial, "wrongLickLimit", times) : null;
    }

    /// <summary>The track's position from the trial start, and the trial's end at the end of the track (during the lead too).
    /// On an auto-reward trial the next pip ends it instead: the track's end is the track's top tone, above every reward floor,
    /// so that pip reaches the zone (rewardZoneReached), as a mouse running past the floor between two pips expects.</summary>
    public TrialOutcome Wheel(Trial trial, double cm, TrialTimes times, ICollection<StimulusFrame> updates)
    {
        track.Update(cm);
        return !trial.autoReward && track.AtEnd ? End(trial, "trackEnd", times) : null;
    }

    /// <summary>responseWindowSeconds from the first pip.</summary>
    public TrialOutcome ResponseEnd(Trial trial, TrialTimes times)
    {
        return End(trial, "responseWindowEnd", times);
    }

    StimulusFrame Frame(Trial trial, int step)
    {
        // The mouse's place on the track, from the wheel alone (wheel cm over the trial's wheel cm to the end, so gain
        // included); the stimulus's progress adds the auto-progress since the trial start. Tone and grating are
        // both set from that one progress, so they stay in step.
        var position = track.Position;
        var fraction = trial.trackLengthWheelCm > 0 ? position / trial.trackLengthWheelCm : 0;
        var progress = Math.Max(0, Math.Min(1, fraction + trial.values.Track.AutoProgressPerSecond * step * StepSeconds));
        var hz = sweep.FrequencyAt(progress);
        var visual = trial.values.VisualStimulus;
        var gratingStart = visual.SpatialFrequencyStartCpd;
        var grating = gratingStart * Math.Pow(visual.SpatialFrequencyEndCpd / gratingStart, progress);
        // A stimulus-off trial's listed stimuli switch off once the mouse has run its fraction of the track.
        var off = !double.IsNaN(trial.stimulusOffFraction) && Math.Round(fraction, 3) >= trial.stimulusOffFraction;
        // The tone switched off plays nothing, logged at level NaN in each pip's row.
        var auditoryOff = off && trial.auditoryOff;
        var visualOn = !(off && trial.visualOff);
        var contrast = visualOn ? trial.visualContrast : 0;
        var trackCm = position * trial.WheelGain;
        if (auditoryOff && double.IsNaN(auditoryOffPositionCm)) auditoryOffPositionCm = trackCm;
        if (!visualOn && double.IsNaN(visualOffPositionCm)) visualOffPositionCm = trackCm;
        var level = auditoryOff ? double.NaN : trial.auditoryLevelDbfs;
        return new StimulusFrame
        {
            Row = new StimulusStateRow { trialNumber = trial.trialNumber, positionCm = trackCm, auditoryFrequencyHz = hz, auditoryLevelDbfs = level, visualSpatialFrequencyCpd = grating, visualContrast = contrast },
            PipIndex = sweep.PipIndexOf(hz),
            AuditoryGain = sweep.GainOf(level),
            SpeakerChannel = trial.speakerChannel,
            Grating = new GratingState { SpatialFrequencyCpd = grating, Opacity = contrast > 0 ? 1 : 0, Contrast = trial.visualContrast, PhaseRad = trial.visualPhaseRad }
        };
    }

    static bool OnTarget(Trial trial, double hz)
    {
        return hz >= trial.values.RewardZoneMinHz && hz <= trial.values.RewardZoneMaxHz;
    }

    /// <summary>The outcome (Tom Childs' ProcessTrialEnd): a trial that ends on a lick in the zone or the zone reached
    /// is rewarded; one that runs out of time or track in the zone is penalised, as is one ending outside it. Reaching the
    /// zone is the floor alone, as in Tom's FreqAboveThreshold: a fast stage's tone (high wheelGain or auto-progress) can
    /// pass the ceiling on the step it crosses the floor, and is rewarded.</summary>
    TrialOutcome End(Trial trial, string reason, TrialTimes times)
    {
        var hz = last != null ? last.Row.auditoryFrequencyHz : 0;
        var onTarget = reason == "rewardZoneReached" || OnTarget(trial, hz);
        var ranOut = reason == "responseWindowEnd" || reason == "trackEnd";
        var rewarded = onTarget && (reason == "correctLick" || reason == "rewardZoneReached");
        var penalised = !onTarget || ranOut;
        var p = trial.values;
        // The shared block (trialNumber, the times, rewardOfferedUl, rewarded, the penalty, the laser) is lib-open-rigs';
        // the conditions, stimulus and autoReward blocks are the trial's (ConditionedTrial). Distances in track cm.
        var row = SharedTrialColumns.Fill(new TrialRow
        {
            trialType = trial.trialType,
            // The zone reached is an auto-reward trial's (its licks ignored): the auto-reward answered it, outcome auto.
            outcome = reason == "rewardZoneReached" ? TrialOutcomes.Auto : rewarded ? TrialOutcomes.Correct : ranOut ? TrialOutcomes.NoResponse : TrialOutcomes.Incorrect,
            trialConditionId = trial.trialConditionId,
            trialCombinationId = trial.trialCombinationId,
            visualContrast = trial.visualContrast,
            visualPhaseRad = trial.visualPhaseRad,
            auditoryLevelDbfs = trial.auditoryLevelDbfs,
            auditoryInitialAzimuthDeg = trial.auditoryInitialAzimuthDeg,
            auditoryElevationDeg = trial.auditoryElevationDeg,
            autoReward = trial.autoReward,
            outcomeReason = reason,
            trackLengthCm = trial.TrackLengthCm,
            outcomePositionCm = track.Position * trial.WheelGain,
            outcomeAuditoryFrequencyHz = hz,
            outcomeVisualSpatialFrequencyCpd = last != null ? last.Row.visualSpatialFrequencyCpd : 0,
            auditoryOffPositionCm = auditoryOffPositionCm,
            visualOffPositionCm = visualOffPositionCm
        }, trial, times, trial.rewardOfferedUl, rewarded, penalised, p.Penalty.TimeoutSeconds);
        return new TrialOutcome(row, SoundLevels.Parse(p.Penalty.Sound.LevelDbfs), SoundLevels.Parse(p.RewardTone.LevelDbfs));
    }
}

/// <summary>Draws every trial of an experiment from one random stream seeded with the task's rngSeed.</summary>
public class TrialDraw
{
    /// <summary>What the draw reads from the task schema (TaskSchema, generated).</summary>
    public static readonly ConditionsSchema Schema = new ConditionsSchema(TaskSchema.SessionFields, TaskSchema.ListFields, TaskSchema.MaxCombinations, TaskSchema.TrialTypes);

    /// <summary>Each trial condition as drawn (OpenRigs.Trials TrialConditions.Build, the Python build's rule): its set and
    /// pairs over the task's text, every combination read back, each combination's Id its row in Settings_Combinations. Adds a
    /// problem for each condition that cannot be built, and returns null then.</summary>
    public static List<BuiltTrialCondition> BuildConditions(string taskText, UclOpenCoenDomeNavigationTaskParameters task, List<string> problems)
    {
        return TrialConditions.Build(taskText, task, t => TaskYaml.Read<UclOpenCoenDomeNavigationTaskLogic>(t).TaskParameters, Schema, problems);
    }

    readonly UclOpenCoenDomeNavigationTaskParameters task;
    readonly EqualisedSpeakerArray speakers;
    readonly List<BuiltTrialCondition> conditions;
    readonly Random random;
    readonly int conditionCount;
    int drawn;

    /// <summary>The seed the random stream started from: the task's rngSeed, or one drawn when it is null.</summary>
    public int Seed { get; private set; }

    /// <summary>The Zapit conditions file's max ramp-down, in seconds: how long the trigger line is held past the outcome
    /// (0 without optogenetics).</summary>
    public double MaxRampDownSeconds { get; private set; }

    /// <summary>The laser's cap from the trigger, derived at Go: the max responseWindowSeconds + laserToStimulusSeconds any
    /// weighted combination can draw, plus TrialRules.LaserCapMarginSeconds (infinity without optogenetics).</summary>
    public double LaserCapSeconds { get; private set; }

    /// <summary>The draw for the task file's text (TaskText), checked by CheckTask; zapitConditions gives a conditions file's
    /// count and max ramp-down (null: ZapitConditions.Check reads it). With optogenetics, refuses a ramp-down that leaves
    /// the trigger line up past the next trial's arming in any combination.</summary>
    public TrialDraw(string taskText, Sweep sweep, Func<string, ZapitConditionsFile> zapitConditions, EqualisedSpeakerArray speakers = null)
    {
        this.speakers = speakers;
        conditions = CheckTask(taskText, sweep, speakers, out task);
        var opto = task.Optogenetics;
        LaserCapSeconds = double.PositiveInfinity;
        if (opto.Enabled)
        {
            var file = zapitConditions != null ? zapitConditions(opto.ZapitConditionsPath) : ZapitConditions.Check(opto.ZapitConditionsPath);
            conditionCount = file.Count;
            MaxRampDownSeconds = file.MaxRampDownSeconds;
            var weighted = Weighted(conditions).ToList();
            LaserCapSeconds = weighted.Max(c => c.Task.TrialStructure.ResponseWindowSeconds.Value + c.Task.Optogenetics.LaserToStimulusSeconds) + TrialRules.LaserCapMarginSeconds;
            var problems = new List<string>();
            foreach (var condition in conditions)
            {
                foreach (var combination in condition.Combinations)
                {
                    var p = combination.Task;
                    var holdMax = p.TrialStructure.ItiMinSeconds - p.Optogenetics.LaserToStimulusSeconds - ZapitTrial.ReplyMarginSeconds;
                    if (MaxRampDownSeconds > holdMax + 1e-9)
                    {
                        problems.Add(string.Format("{0}: the Zapit conditions file's max ramp-down ({1} s) must be at most {2} s (itiMinSeconds - laserToStimulusSeconds - {3} s): the trigger line is held that long past the outcome, the next trial is armed once it falls, and Zapit's reply needs {3} s before its trigger",
                            condition.Where + (combination.Picks.Length == 0 ? "" : " with " + combination.Picks), MaxRampDownSeconds, holdMax, ZapitTrial.ReplyMarginSeconds));
                    }
                }
            }
            if (problems.Count > 0) throw new InvalidOperationException("The task cannot be run: " + string.Join("; ", problems.Distinct()) + ".");
        }
        Seed = RngSeed.Resolve(task.RngSeed);
        random = new Random(Seed);
    }

    static IEnumerable<TrialConditionCombination<UclOpenCoenDomeNavigationTaskParameters>> Weighted(IEnumerable<BuiltTrialCondition> conditions)
    {
        return conditions.Where(c => c.Weight > 0).SelectMany(c => c.Combinations);
    }

    /// <summary>The conditions as built at Go, in trialConditions' order.</summary>
    public IList<BuiltTrialCondition> Conditions { get { return conditions; } }

    /// <summary>The task as read from its text (ReadTask's reader).</summary>
    public UclOpenCoenDomeNavigationTaskParameters Task { get { return task; } }

    /// <summary>Checks the task file's text can be run with the sweep and gives the task as read (ReadTask's reader) and its
    /// conditions as drawn (BuildConditions, over the text as written): its curriculum (the only record of the stage, filled by
    /// app-rig-control) is present and well formed, and what pydantic would check (Bonsai reads the task file without it) and what
    /// only the sweep can say holds on the task and every combination of every condition; throws naming every problem.</summary>
    public static List<BuiltTrialCondition> CheckTask(string taskText, Sweep sweep, EqualisedSpeakerArray speakers, out UclOpenCoenDomeNavigationTaskParameters task)
    {
        task = TaskYaml.Read<UclOpenCoenDomeNavigationTaskLogic>(taskText).TaskParameters;
        var curriculumProblem = CurriculumCheck.Problem(task.Curriculum);
        if (curriculumProblem != null) throw new InvalidOperationException("The task file's curriculum " + curriculumProblem + ": app-rig-control fills it from the Mouse Curriculum at launch.");
        var problems = new List<string>();
        Check(problems, task, sweep, speakers);
        var built = problems.Count == 0 ? BuildConditions(taskText, task, problems) : null;
        if (built != null)
        {
            // Every combination of every condition, each problem named by its condition and picks.
            foreach (var condition in built)
            {
                foreach (var combination in condition.Combinations)
                {
                    var own = new List<string>();
                    Check(own, combination.Task, sweep, speakers);
                    problems.AddRange(own.Select(p => condition.Where + (combination.Picks.Length == 0 ? "" : " with " + combination.Picks) + ": " + p));
                }
            }
        }
        if (problems.Count > 0) throw new InvalidOperationException("The task cannot be run: " + string.Join("; ", problems.Distinct()) + ".");
        return built;
    }

    // A sound's levelDbfs: a number of dBFS RMS from -120 to -1, or mute (OpenRigs.Trials SoundLevels; task.py SoundLevelDbfs).
    static void Level(List<string> problems, string name, object value)
    {
        double level;
        try { level = SoundLevels.Parse(value); }
        catch (InvalidOperationException e) { problems.Add(name + ": " + e.Message); return; }
        if (!double.IsNaN(level) && !(level >= -120 && level <= -1)) problems.Add(string.Format("{0} ({1}) must be from -120 to -1 dBFS, or {2}", name, level, SoundLevels.Mute));
    }

    // What one task's values must be (task.py's bounds; Bonsai reads the task file without pydantic), on the task or one
    // combination; an azimuth needs rig.yml's speakers, the reward zone the sweep. x-rules are Python's alone.
    static void Check(List<string> problems, UclOpenCoenDomeNavigationTaskParameters task, Sweep sweep, EqualisedSpeakerArray speakers)
    {
        if (task.AuditoryStimulus == null || task.Reward == null || task.TrialStructure == null || task.Optogenetics == null || task.StimulusOff == null || task.VisualStimulus == null || task.Penalty == null || task.Track == null)
        {
            problems.Add("auditoryStimulus, reward, trialStructure, optogenetics, stimulusOff, visualStimulus, penalty and track are required");
            return;
        }
        // A NaN or infinite value (YAML's .nan or .inf) would hold the trial start or a penalty timeout for ever, with no error.
        Action<string, double, bool> number = (name, value, aboveZero) =>
        {
            if (!(aboveZero ? value > 0 : value >= 0) || double.IsInfinity(value)) problems.Add(name + (aboveZero ? " must be a finite number above 0" : " must be a finite number, 0 or more"));
        };
        var structure = task.TrialStructure;
        var trackBlock = task.Track;
        if (trackBlock.LengthsCm == null || trackBlock.LengthsCm.Count == 0 || trackBlock.LengthsCm.Any(l => !(l > 0) || double.IsInfinity(l))) problems.Add("track.lengthsCm needs at least one length, each a finite number above 0");
        // The lab's blocks, checked by their one C# check each (OpenRigs.Core TaskBlocks, QuietWait).
        foreach (var p in TaskBlocks.ItiProblems(structure.ItiMinSeconds, structure.ItiMaxSeconds)) problems.Add("trialStructure." + p);
        number("penalty.timeoutSeconds", task.Penalty.TimeoutSeconds, false);
        // The quiet waits, the lab's one check each (OpenRigs.Core QuietWait: finite, 0 or more, at most 10 s and 20 cm): a long
        // wait and a licking mouse would hold the trial start with no message.
        foreach (var p in QuietWait.LickProblems(structure.LickQuietSeconds)) problems.Add("trialStructure." + p);
        foreach (var p in QuietWait.WheelProblems(structure.WheelQuietSeconds, structure.WheelQuietCm)) problems.Add("trialStructure." + p);
        number("track.wheelGain", trackBlock.WheelGain, true);
        number("track.autoProgressPerSecond", trackBlock.AutoProgressPerSecond, false);
        number("visualStimulus.spatialFrequencyStartCpd", task.VisualStimulus.SpatialFrequencyStartCpd, true);
        number("visualStimulus.spatialFrequencyEndCpd", task.VisualStimulus.SpatialFrequencyEndCpd, true);
        var contrasts = task.VisualStimulus.Contrasts;
        if (contrasts == null || contrasts.Count == 0 || contrasts.Any(c => !(c >= 0) || double.IsInfinity(c))) problems.Add("visualStimulus.contrasts must each be a finite number, 0 or more, at least one");
        var sound = task.AuditoryStimulus;
        Level(problems, "auditoryStimulus.levelDbfs", sound.LevelDbfs);
        Level(problems, "penalty.sound.levelDbfs", task.Penalty.Sound == null ? null : task.Penalty.Sound.LevelDbfs);
        Level(problems, "rewardTone.levelDbfs", task.RewardTone == null ? null : task.RewardTone.LevelDbfs);
        if (!(sound.ElevationDeg >= -90 && sound.ElevationDeg <= 90)) problems.Add("auditoryStimulus.elevationDeg must be in [-90, 90]");
        var azimuths = (sound.InitialAzimuthsDeg ?? new List<object>()).Select(SoundAzimuths.Parse).ToList();
        if (azimuths.Count == 0 || azimuths.Any(a => !(a.HasValue && (double.IsNaN(a.Value) || a.Value >= -180 && a.Value <= 180))))
            problems.Add("auditoryStimulus.initialAzimuthsDeg must each be all or a number in [-180, 180], at least one");
        else if (sound.LevelDbfs as string != SoundLevels.Mute && (speakers == null || speakers.Speakers == null || speakers.Speakers.Count == 0))
            problems.Add("rig.yml has no speakerArray.speakers to play auditoryStimulus.initialAzimuthsDeg on");
        foreach (var p in TaskBlocks.RewardSizeProblems(task.Reward.SizeUl, LoadRewardLine.DefaultMaxRewardUl)) problems.Add("reward." + p);
        foreach (var p in TaskBlocks.LargeRewardProblems(task.Reward.SizeUl, task.Reward.LargeMultiplier, task.Reward.LargeProbability, LoadRewardLine.DefaultMaxRewardUl)) problems.Add("reward." + p);
        foreach (var p in TaskBlocks.AutoRewardProblems(task.AutoRewardProbability)) problems.Add(p);
        foreach (var p in TaskBlocks.LaserOnProbabilityProblems(task.Optogenetics.LaserOnProbability)) problems.Add("optogenetics." + p);
        foreach (var p in TaskBlocks.ResponseWindowProblems(structure.ResponseWindowSeconds)) problems.Add("trialStructure." + p);
        // The lead: the first pip comes max(laserToStimulusSeconds, 0) after the trial start in every experiment, the
        // trigger laserToStimulusSeconds before the first pip; whole steps, at most 0.2 s before it, inside the shortest interval
        // with Zapit's reply margin, and no later after it than the response window.
        var lead = task.Optogenetics.LaserToStimulusSeconds;
        var leadSteps = lead / TrialRules.StepSeconds;
        if (!(Math.Abs(leadSteps - Math.Round(leadSteps)) < 1e-6)) problems.Add("optogenetics.laserToStimulusSeconds must be a whole number of 0.05 s steps");
        if (lead > TrialRules.MaxLeadSeconds + 1e-9) problems.Add(string.Format("optogenetics.laserToStimulusSeconds ({0} s) must be at most {1} s", lead, TrialRules.MaxLeadSeconds));
        var leadProblem = ZapitTrial.LeadProblem(lead, structure.ItiMinSeconds);
        if (leadProblem != null) problems.Add(leadProblem);
        var after = structure.ResponseWindowSeconds ?? TrialRules.UnlimitedWindowTriggerAfterSeconds;
        if (-lead > after + 1e-9) problems.Add(string.Format("optogenetics.laserToStimulusSeconds ({0} s) puts the trigger more than {1} s after the first pip, past the {2}", lead, after, structure.ResponseWindowSeconds.HasValue ? "responseWindowSeconds" : "limit without responseWindowSeconds"));
        var off = task.StimulusOff;
        if (off.Stimuli != StimulusOffStimuli.None && (off.TrackFractions == null || off.TrackFractions.Count == 0 || off.TrackFractions.Any(f => !(f > 0 && f <= 1))))
            problems.Add("stimulusOff.stimuli is " + off.Stimuli + " but stimulusOff.trackFractions is empty or not all above 0 and at most 1");
        // The reward zone lies in the tones the track plays: above its start, at most its top (Sweep.TrackEndHz, 24 kHz).
        if (!(task.RewardZoneMinHz > sweep.StartHz && task.RewardZoneMinHz < task.RewardZoneMaxHz && task.RewardZoneMaxHz <= sweep.TrackEndHz)) problems.Add(string.Format("the reward zone ({0} to {1} Hz) is not inside the track's tones ({2} to {3} Hz)", task.RewardZoneMinHz, task.RewardZoneMaxHz, sweep.StartHz, sweep.TrackEndHz));
        var opto = task.Optogenetics;
        if (opto.Enabled)
        {
            if (!(opto.LaserPowerMw > 0)) problems.Add("optogenetics.laserPowerMw must be a power in mW above 0 with optogenetics on");
            // The laser's cap is derived from the response window (TrialDraw.LaserCapSeconds).
            if (!structure.ResponseWindowSeconds.HasValue) problems.Add("optogenetics needs trialStructure.responseWindowSeconds: the laser's cap is derived from it");
        }
    }

    public Trial Next()
    {
        var t = new Trial();
        t.trialNumber = ++drawn;
        // The condition, then one of its combinations (every list pick), whose values the trial draws from.
        t.trialConditionId = WeightedCondition();
        var condition = conditions[t.trialConditionId];
        t.trialType = condition.TrialType;
        var combination = condition.Combinations[condition.Combinations.Count == 1 ? 0 : random.Next(condition.Combinations.Count)];
        t.trialCombinationId = combination.Id;
        var p = t.values = combination.Task;
        var structure = p.TrialStructure;
        t.itiSeconds = structure.ItiMinSeconds + (structure.ItiMaxSeconds - structure.ItiMinSeconds) * random.NextDouble();  // trial 1 too, from the first clock tick, so Zapit is armed before its trigger
        t.trackLengthWheelCm = p.Track.LengthsCm[0] / p.Track.WheelGain;  // wheel cm to the end
        // A stimulus-off trial's combination says which stimuli switch off and at which fraction. A fraction of 1 is the
        // track's end, where the trial ends (or, on auto-reward, its last pip plays): never, so the trial is an ordinary one.
        var stimuli = p.StimulusOff.Stimuli;
        var off = stimuli != StimulusOffStimuli.None && p.StimulusOff.TrackFractions[0] < 1;
        t.stimulusOffFraction = off ? p.StimulusOff.TrackFractions[0] : double.NaN;
        t.auditoryOff = stimuli == StimulusOffStimuli.Auditory || stimuli == StimulusOffStimuli.Both;
        t.visualOff = stimuli == StimulusOffStimuli.Visual || stimuli == StimulusOffStimuli.Both;
        t.autoReward = random.NextDouble() < p.AutoRewardProbability;   // so 1 is every trial and 0 none
        t.visualContrast = p.VisualStimulus.Contrasts[0];
        t.auditoryLevelDbfs = SoundLevels.Parse(p.AuditoryStimulus.LevelDbfs);
        var sounding = !double.IsNaN(t.auditoryLevelDbfs);
        // Where the pips play: every speaker ("all"), or rig.yml's speaker nearest the picked azimuth at elevationDeg; nowhere when mute.
        var azimuth = SoundAzimuths.Parse(p.AuditoryStimulus.InitialAzimuthsDeg[0]).Value;
        t.speakerChannel = sounding ? SoundAzimuths.AllSpeakersChannel : -1;
        t.auditoryInitialAzimuthDeg = double.NaN;
        t.auditoryElevationDeg = double.NaN;
        if (sounding && !double.IsNaN(azimuth))
        {
            var speaker = SoundAzimuths.NearestSpeaker(speakers, azimuth, p.AuditoryStimulus.ElevationDeg).Value;
            t.speakerChannel = speaker.Channel;
            t.auditoryInitialAzimuthDeg = speaker.Position.Azimuth;
            t.auditoryElevationDeg = speaker.Position.Elevation;
        }
        if (task.Optogenetics.Enabled)
        {
            // Every trial sends Zapit a condition; laser-off trials at 0 mW (Pip, decision 11).
            var opto = p.Optogenetics;
            ZapitTrial.Draw(random, t.trialNumber, conditionCount, opto.TrialsBeforeLaser, opto.LaserOnProbability, opto.LaserPowerMw.Value,
                out t.laserCondition, out t.laserOn, out t.laserPowerMw);
        }
        // The reward: a large one with the combination's largeProbability.
        var large = random.NextDouble() < p.Reward.LargeProbability;
        t.rewardOfferedUl = large ? p.Reward.SizeUl * p.Reward.LargeMultiplier : p.Reward.SizeUl;
        t.DrawVisualPhase(random);
        return t;
    }

    /// <summary>A trial condition drawn with the weights as relative chances (TrialRules.Create refuses all 0).</summary>
    int WeightedCondition()
    {
        int draw = random.Next(conditions.Sum(c => c.Weight));
        for (int i = 0; i < conditions.Count; i++)
        {
            draw -= conditions[i].Weight;
            if (draw < 0) return i;
        }
        throw new InvalidOperationException("No trial condition drawn.");
    }
}

[Combinator]
[Description("Reads the task file's text into the task (OpenRigs.Core TaskYaml.Read): as DeserializeFromYaml, but a trial condition's set values keep their YAML types (numbers, bools), so Settings_Task logs them as such.")]
[WorkflowElementCategory(ElementCategory.Transform)]
public class ReadTask
{
    public IObservable<UclOpenCoenDomeNavigationTaskLogic> Process(IObservable<string> source)
    {
        return source.Select(TaskYaml.Read<UclOpenCoenDomeNavigationTaskLogic>);
    }
}

[Combinator]
[Description("Checks the task at Go and makes the rules the trials are drawn from and run by. Input: (the task file's text, TaskText; rig.yml; sweep); the task is read as ReadTask reads it and its conditions built over the text as written; rig.yml's speakerArray places initialAzimuthsDeg's nearest speaker. Stops the workflow on a task whose curriculum is empty or malformed, a trial condition that cannot be built (its set and pairs over the task's values, every combination), a reward zone outside the track's tones or the optogenetics rules (the Zapit conditions file read for its conditions and max ramp-down). A real mouse on a rig without hardware triggering is lib-open-rigs' ConnectZapit's refusal.")]
[WorkflowElementCategory(ElementCategory.Transform)]
public class CheckTaskAtGo
{
    public IObservable<TrialRules> Process(IObservable<Tuple<string, LabRig, Sweep>> source)
    {
        return source.Select(x => TrialRules.Create(x.Item1, x.Item3, x.Item2.Zapit != null && x.Item2.Zapit.HardwareTriggered, null, x.Item2.SpeakerArray));
    }
}

[Combinator]
[Description("Runs the trials (lib-open-rigs TrialLoop) by the task's rules. Inputs: the rules, the Harp clock in seconds, the wheel in cm, the left and right lick states, Zapit's replies. The right spout is not the task's answer, but a lick on it holds the trial start, as the left's does.")]
[WorkflowElementCategory(ElementCategory.Combinator)]
public class RunTrials
{
    public IObservable<TrialEvent<Trial, StimulusFrame, TrialOutcome>> Process(IObservable<TrialRules> rules, IObservable<double> clock,
        IObservable<double> wheelCm, IObservable<bool> lickLeft, IObservable<bool> lickRight, IObservable<ZapitReply> zapitReplies)
    {
        return TrialLoop.Observe(rules, clock, new[] { lickLeft, lickRight }, zapitReplies, wheelCm);
    }
}
