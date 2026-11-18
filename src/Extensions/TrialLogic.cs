using Bonsai;
using OpenRigs.Core;
using OpenRigs.Trials;
using OpenRigs.Devices.Zapit;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reactive.Linq;
using OpenRigs.Devices.Rigs;
using UclOpenCoenDomeNavigationTask;

// The task in tested C#: each trial's draw, its timing and its run along the virtual track, as the rules
// lib-open-rigs' TrialLoop runs on the Harp clock (RunTrials). main.bonsai drives the hardware from the loop's events;
// tests\Extensions.Tests runs the same loop and rules on stand-in clocks.

/// <summary>
/// The distance along the track: the distance run since the trial's start, less the furthest the mouse has run
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

/// <summary>One trial as drawn, with what its stage asks; fields named as the trial log's columns where they are one.</summary>
public class Trial : IZapitTrial
{
    int ITrial.TrialNumber { get { return trialNumber; } }
    int IZapitTrial.LaserCondition { get { return laserCondition; } }
    bool IZapitTrial.LaserOn { get { return laserOn; } }
    double IZapitTrial.LaserPowerMw { get { return laserPowerMw; } }

    public int trialNumber;
    public string trialType;            // TrialDraw.TypeNames: what the trial shows, its condition's type
    public int trialCondition;          // the 0-based index of its condition in the task's trialConditions
    public double itiSeconds;           // the interval before this trial: the last outcome (after any timeout; trial 1: the first clock tick) to the first pip, with a quiet mouse
    public double lickQuietSeconds;     // the quiet waits, from the interval's start (QuietWait): the stage's lick, trialStructure's wheel; 0: off
    public double wheelQuietSeconds;
    public double wheelQuietCm;
    public double trackLengthCm;        // wheel cm to the track's end: the drawn track cm / wheelGain
    public double stimulusOffFraction;  // a stimulus-off trial's track fraction (stimulusOff.trackFractions); NaN: every stimulus stays on
    public bool auditoryOff;            // on a stimulus-off trial, whether the tone switches off (stimulusOff.stimuli)
    public bool visualOff;              // and the grating
    public double rewardFloorHz;
    public double rewardCeilingHz;
    public double rewardOfferedUl;     // the task file's reward.rewardSizeUl: what a reward on this trial gives
    public bool autoReward;             // drawn with the stage's autoRewardProbability: rewarded on reaching the floor, licks ignored
    public int wrongLickLimit;
    public double responseWindowSeconds;     // from the first pip; NaN: none
    public double wheelGain;            // the stage's: track cm per wheel cm
    public double autoProgressPerSecond; // the stage's: fraction of the track the stimulus advances per second on its own
    public int firstPipStep;            // the lead, max(laserToStimulusSeconds, 0), in steps after the trial start (step 0), in every experiment
    public bool optogenetics;
    public int laserCondition;          // 0 without optogenetics
    public bool laserOn;
    public double laserPowerMw;         // 0 with the laser off
    public double laserMaxSeconds;      // the workflow's wall-clock backstop (LaserBackstop); the loop's LaserCap is TrialRules.Laser's
    public int rngSeed;                 // the experiment's seed, for the RngSeed event
}

/// <summary>A trial's outcome (lib-open-rigs' TrialOutcome): its row, the reward at the left valve, the penalty sound
/// and the timeout before the next interval.</summary>
public class TrialOutcome : OpenRigs.Trials.TrialOutcome<TrialRow> { public TrialOutcome(TrialRow row) : base(row, "left") { } }

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
    public const double LaserMaxMarginSeconds = 0.25; // task.py LASER_MAX_MARGIN_SECONDS
    public const double MaxLeadSeconds = 0.2;            // task.py MAX_LEAD_SECONDS: the furthest the trigger may come before the first pip
    public const double UnlimitedWindowTriggerAfterSeconds = 2.0; // task.py UNLIMITED_WINDOW_TRIGGER_AFTER_SECONDS: the furthest after the first pip it may come on a stage without responseWindowSeconds

    readonly UclOpenCoenDomeNavigationTaskParameters task;
    readonly Sweep sweep;
    readonly TrialDraw draw;
    readonly LaserTiming laser;
    VirtualTrack track;
    StimulusFrame last;
    int nextStep, wrongLicks;
    double auditoryOffPositionCm, visualOffPositionCm;  // where each switched off this trial; NaN: it never did

    public TrialRules(UclOpenCoenDomeNavigationTaskParameters task, int stage, Sweep sweep, bool hardwareTriggered, Func<string, int> zapitConditionCount)
    {
        this.task = task;
        this.sweep = sweep;
        draw = new TrialDraw(task, stage, sweep, zapitConditionCount);
        var opto = task.Optogenetics;
        // Every StartStimulus continuous: the laser on from the trigger until the outcome or LaserCap, the trigger line
        // (and the masking light on it) held triggerHoldSeconds past the outcome, the next trial armed once it falls.
        laser = opto.Enabled ? LaserTiming.UntilOutcome(hardwareTriggered, opto.TriggerHoldSeconds, opto.LaserMaxSeconds) : null;
    }

    /// <summary>The rules for a task file's stage with the sweep and Zapit's hardware triggering; throws naming every
    /// problem (TrialDraw.CheckTask).</summary>
    public static TrialRules Create(UclOpenCoenDomeNavigationTaskParameters task, int stage, Sweep sweep, bool hardwareTriggered, Func<string, int> zapitConditionCount)
    {
        return new TrialRules(task, stage, sweep, hardwareTriggered, zapitConditionCount);
    }

    /// <summary>The laser in an optogenetics experiment (the trigger line only rises in one); null without.</summary>
    public LaserTiming Laser { get { return laser; } }

    public Trial Draw(TrialOutcome previous)
    {
        return draw.Next();
    }

    public TrialTiming Timing(Trial trial)
    {
        return new TrialTiming
        {
            ItiSeconds = trial.itiSeconds,
            // The lead applies in every experiment, so timing never reveals Zapit.
            LaserToStimulusSeconds = task.Optogenetics.LaserToStimulusSeconds,
            LickQuietSeconds = trial.lickQuietSeconds,
            WheelQuietSeconds = trial.wheelQuietSeconds,
            WheelQuietCm = trial.wheelQuietCm,
            // Every trial of an optogenetics experiment sends Zapit a condition; laser-off trials at 0 mW (Pip, decision 11).
            Laser = trial.optogenetics,
            ResponseWindowSeconds = double.IsNaN(trial.responseWindowSeconds) ? double.PositiveInfinity : trial.responseWindowSeconds
        };
    }

    public void Start(Trial trial, TrialTimes times, double wheelCm)
    {
        var wheel = double.IsNaN(wheelCm) ? 0 : wheelCm;
        track = new VirtualTrack(trial.trackLengthCm, wheel);
        track.Update(wheel);
        nextStep = trial.firstPipStep;
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
    /// missed pips not played at once. An auto-reward trial ends on the pip that reaches the reward floor.</summary>
    public TrialOutcome Tick(Trial trial, TrialTimes times, ICollection<StimulusFrame> updates)
    {
        var due = Math.Max((int)Math.Floor((times.Now - times.TrialStartTime + 1e-9) / StepSeconds), trial.firstPipStep);
        if (due < nextStep) return null;
        nextStep = due + 1;
        last = Frame(trial, due);
        updates.Add(last);
        return trial.autoReward && last.Row.auditoryFrequencyHz >= trial.rewardFloorHz ? End(trial, "FrequencyReached", times) : null;
    }

    /// <summary>The left spout's licks count from the first pip: in the zone rewarded, outside toward wrongLickLimit;
    /// an auto-reward trial's licks are ignored.</summary>
    public TrialOutcome Lick(Trial trial, int spout, TrialTimes times, ICollection<StimulusFrame> updates)
    {
        if (spout != 0 || last == null || trial.autoReward) return null;
        if (OnTarget(trial, last.Row.auditoryFrequencyHz)) return End(trial, "Lick", times);
        wrongLicks++;
        return trial.wrongLickLimit > 0 && wrongLicks >= trial.wrongLickLimit ? End(trial, "WrongLickLimit", times) : null;
    }

    /// <summary>The track's position from the trial start, and the trial's end at the end of the track (during the lead too).
    /// On an auto-reward trial the next pip ends it instead: the track's end is the sweep's end, above every reward floor,
    /// so that pip reaches the zone (FrequencyReached), as a mouse running past the floor between two pips expects.</summary>
    public TrialOutcome Wheel(Trial trial, double cm, TrialTimes times, ICollection<StimulusFrame> updates)
    {
        track.Update(cm);
        return !trial.autoReward && track.AtEnd ? End(trial, "TrackEnd", times) : null;
    }

    /// <summary>responseWindowSeconds from the first pip.</summary>
    public TrialOutcome ResponseEnd(Trial trial, TrialTimes times)
    {
        return End(trial, "ResponseWindowEnd", times);
    }

    StimulusFrame Frame(Trial trial, int step)
    {
        // The mouse's place on the track, from the wheel alone (wheel cm over the trial's wheel cm to the end, so gain
        // included); the stimulus's progress adds the stage's auto-progress since the trial start. Tone and grating are
        // both set from that one progress, so they stay in step.
        var position = track.Position;
        var fraction = trial.trackLengthCm > 0 ? position / trial.trackLengthCm : 0;
        var progress = Math.Max(0, Math.Min(1, fraction + trial.autoProgressPerSecond * step * StepSeconds));
        var tone = sweep.FrequencyAt(progress);
        var visual = task.VisualStimulus;
        var gratingStart = visual.SpatialFrequencyStartCpd;
        var grating = gratingStart * Math.Pow(visual.SpatialFrequencyEndCpd / gratingStart, progress);
        // A stimulus-off trial's listed stimuli switch off once the mouse has run its fraction of the track.
        var off = !double.IsNaN(trial.stimulusOffFraction) && Math.Round(fraction, 3) >= trial.stimulusOffFraction;
        var auditoryOn = !(off && trial.auditoryOff);
        var visualOn = !(off && trial.visualOff);
        if (!auditoryOn && double.IsNaN(auditoryOffPositionCm)) auditoryOffPositionCm = position;
        if (!visualOn && double.IsNaN(visualOffPositionCm)) visualOffPositionCm = position;
        return new StimulusFrame
        {
            Row = new StimulusStateRow { trialNumber = trial.trialNumber, positionCm = position, auditoryFrequencyHz = tone, auditoryOn = auditoryOn, visualSpatialFrequencyCpd = grating, visualOn = visualOn },
            Bin = sweep.BinOf(tone),
            ToneGain = auditoryOn && trial.trialType != TrialDraw.Visual ? 1 : 0,
            GratingOpacity = visualOn && trial.trialType != TrialDraw.Auditory ? 1 : 0
        };
    }

    static bool OnTarget(Trial trial, double hz)
    {
        return hz >= trial.rewardFloorHz && hz <= trial.rewardCeilingHz;
    }

    /// <summary>The outcome (Tom Childs' ProcessTrialEnd): a trial that ends on a lick in the zone or the zone reached
    /// is rewarded; one that runs out of time or track in the zone is penalised, as is one ending outside it. Reaching the
    /// zone is the floor alone, as in Tom's FreqAboveThreshold: a fast stage's tone (high wheelGain or auto-progress) can
    /// pass the ceiling on the step it crosses the floor, and is rewarded.</summary>
    TrialOutcome End(Trial trial, string reason, TrialTimes times)
    {
        var tone = last != null ? last.Row.auditoryFrequencyHz : 0;
        var onTarget = reason == "FrequencyReached" || OnTarget(trial, tone);
        var ranOut = reason == "ResponseWindowEnd" || reason == "TrackEnd";
        var rewarded = onTarget && (reason == "Lick" || reason == "FrequencyReached");
        var penalised = !onTarget || ranOut;
        // The shared block (trialNumber, the times, rewardOfferedUl, rewarded, the penalty, the laser) is lib-open-rigs'.
        var row = SharedTrialColumns.Fill(new TrialRow
        {
            trialType = trial.trialType,
            trialCondition = trial.trialCondition,
            outcome = rewarded ? TrialOutcomes.Correct : ranOut ? TrialOutcomes.NoResponse : TrialOutcomes.Incorrect,
            outcomeReason = reason == "Lick" ? (rewarded ? "CorrectLick" : "WrongLickLimit") : reason,
            trackLengthCm = trial.trackLengthCm,
            wheelGain = trial.wheelGain,
            outcomePositionCm = track.Position,
            outcomeAuditoryFrequencyHz = tone,
            outcomeVisualSpatialFrequencyCpd = last != null ? last.Row.visualSpatialFrequencyCpd : 0,
            auditoryOffPositionCm = auditoryOffPositionCm,
            visualOffPositionCm = visualOffPositionCm,
            autoRewarded = trial.autoReward && rewarded
        }, trial, times, trial.rewardOfferedUl, rewarded, penalised, task.Penalty.TimeoutSeconds, task.Penalty.Sound.Enabled);
        return new TrialOutcome(row);
    }
}

/// <summary>Draws every trial of an experiment from one random stream seeded with the task's rngSeed.</summary>
public class TrialDraw
{
    public const string Visual = "visual";                           // the grating alone
    public const string Auditory = "auditory";                       // the tone sweep alone
    public const string AudiovisualCoherent = "audiovisualCoherent"; // both
    /// <summary>The trial types the corridor shows, the trial row's trialType and trialConditions' types (coen-dome-decision-making's names).</summary>
    public static readonly string[] TypeNames = { Visual, Auditory, AudiovisualCoherent };
    /// <summary>The most a condition's weight may be (task.py's TrialCondition.weight le), so the weights' sum stays an int.</summary>
    public const int MaxWeight = 1000000;

    /// <summary>What is wrong with the task's trialConditions (task.py's rules): a type the corridor does not show, a set
    /// (the corridor has no condition fields yet, x-conditionFields), a weight below 0, none above 0.</summary>
    public static List<string> ConditionProblems(UclOpenCoenDomeNavigationTaskParameters task)
    {
        var problems = new List<string>();
        var conditions = task.TrialConditions;
        if (conditions == null || conditions.Count == 0) { problems.Add("trialConditions needs a condition"); return problems; }
        for (int i = 0; i < conditions.Count; i++)
        {
            var c = conditions[i];
            var where = string.Format("trialConditions[{0}] ({1})", i, c.Type);
            if (!TypeNames.Contains(c.Type)) problems.Add(where + ": its type is not one of " + string.Join(", ", TypeNames));
            if (c.Weight < 0) problems.Add(where + ": its weight is below 0");
            if (c.Weight > MaxWeight) problems.Add(where + ": its weight is above " + MaxWeight);
            var set = c.Set as System.Collections.IDictionary;
            if (set != null) foreach (var path in set.Keys) problems.Add(where + " sets " + path + ", which is not a condition field of " + c.Type + " (x-conditionFields)");
            else if (c.Set != null) problems.Add(where + ": its set is not a map");
        }
        if (conditions.Sum(c => (long)c.Weight) <= 0) problems.Add("trialConditions needs a condition with a weight above 0");
        if (conditions.Sum(c => (long)c.Weight) > int.MaxValue) problems.Add("trialConditions' weights add to more than " + int.MaxValue + ", the most the draw can sum");
        return problems;
    }

    readonly UclOpenCoenDomeNavigationTaskParameters task;
    readonly Random random;
    readonly int conditionCount;
    int drawn;

    /// <summary>The seed the random stream started from: the task's rngSeed, or one drawn when it is null.</summary>
    public int Seed { get; private set; }

    public TrialDraw(UclOpenCoenDomeNavigationTaskParameters task, int stage, Sweep sweep, Func<string, int> zapitConditionCount)
    {
        this.task = task;
        CheckTask(task, stage, sweep);
        var opto = task.Optogenetics;
        if (opto.Enabled) conditionCount = zapitConditionCount != null ? zapitConditionCount(opto.ZapitConditionsPath) : ZapitConditions.Check(opto.ZapitConditionsPath);
        Seed = RngSeed.Resolve(task.RngSeed);
        random = new Random(Seed);
    }

    /// <summary>Checks the task can be run as the stage with the sweep: its values came from that stage of the training
    /// pipeline (trainingPipeline.id, filled by rig-control, equals the TrainingStage setting, so one stage's values never
    /// run under another's id), what pydantic would check (Bonsai reads the task file without it) and what only the
    /// sweep can say; throws naming every problem.</summary>
    public static void CheckTask(UclOpenCoenDomeNavigationTaskParameters task, int stage, Sweep sweep)
    {
        var pipeline = task.TrainingPipeline;
        // No id or no sha256: not filled by rig-control, so the summary could not say which pipeline ran (as decision-making)
        if (pipeline == null || !pipeline.Id.HasValue || string.IsNullOrEmpty(pipeline.Sha256))
        {
            throw new InvalidOperationException(string.Format("TrainingStage is {0}, but the task file's trainingPipeline names no stage or no pipeline sha256: rig-control fills it from the training pipeline at launch.", stage));
        }
        if (pipeline.Id.Value != stage)
        {
            throw new InvalidOperationException(string.Format("TrainingStage is {0}, but the task file's values are training pipeline stage {1}'s (trainingPipeline.id): one stage's values never run under another's id.", stage, pipeline.Id.Value));
        }
        var problems = new List<string>();
        // A NaN or infinite value (YAML's .nan or .inf) would hold the trial start or a penalty timeout for ever, with no error.
        Action<string, double, bool> number = (name, value, aboveZero) =>
        {
            if (!(aboveZero ? value > 0 : value >= 0) || double.IsInfinity(value)) problems.Add(name + (aboveZero ? " must be a finite number above 0" : " must be a finite number, 0 or more"));
        };
        if (task.TrialStructure.TrackLengthsCm == null || task.TrialStructure.TrackLengthsCm.Count == 0 || task.TrialStructure.TrackLengthsCm.Any(l => !(l > 0) || double.IsInfinity(l))) problems.Add("trialStructure.trackLengthsCm needs at least one length, each a finite number above 0");
        // The lab's blocks, checked by their one C# check each (OpenRigs.Core TaskBlocks, QuietWait).
        foreach (var p in TaskBlocks.ItiProblems(task.TrialStructure.ItiMinSeconds, task.TrialStructure.ItiMaxSeconds)) problems.Add("trialStructure." + p);
        number("penalty.timeoutSeconds", task.Penalty.TimeoutSeconds, false);
        // The quiet waits, the lab's one check each (OpenRigs.Core QuietWait: finite, 0 or more, at most 10 s and 20 cm, the
        // wheel's pair both set or both 0): a long wait and a licking mouse would hold the trial start with no message.
        foreach (var p in QuietWait.LickProblems(task.LickQuietSeconds)) problems.Add(p);
        foreach (var p in QuietWait.WheelProblems(task.TrialStructure.WheelQuietSeconds, task.TrialStructure.WheelQuietCm)) problems.Add("trialStructure." + p);
        number("wheelGain", task.WheelGain, true);
        number("autoProgressPerSecond", task.AutoProgressPerSecond, false);
        foreach (var p in TaskBlocks.AutoRewardProblems(task.AutoRewardProbability)) problems.Add(p);
        number("visualStimulus.spatialFrequencyStartCpd", task.VisualStimulus.SpatialFrequencyStartCpd, true);
        number("visualStimulus.spatialFrequencyEndCpd", task.VisualStimulus.SpatialFrequencyEndCpd, true);
        problems.AddRange(TrialDraw.ConditionProblems(task));
        foreach (var p in TaskBlocks.ResponseWindowProblems(task.ResponseWindowSeconds)) problems.Add(p);
        // The lead: the first pip comes max(laserToStimulusSeconds, 0) after the trial start in every experiment, the
        // trigger laserToStimulusSeconds before the first pip; whole steps, at most 0.2 s before it, inside the shortest interval
        // with Zapit's reply margin, and no later after it than the response window.
        var lead = task.Optogenetics.LaserToStimulusSeconds;
        var leadSteps = lead / TrialRules.StepSeconds;
        if (!(Math.Abs(leadSteps - Math.Round(leadSteps)) < 1e-6)) problems.Add("optogenetics.laserToStimulusSeconds must be a whole number of 0.05 s steps");
        if (lead > TrialRules.MaxLeadSeconds + 1e-9) problems.Add(string.Format("optogenetics.laserToStimulusSeconds ({0} s) must be at most {1} s", lead, TrialRules.MaxLeadSeconds));
        var leadProblem = ZapitTrial.LeadProblem(lead, task.TrialStructure.ItiMinSeconds);
        if (leadProblem != null) problems.Add(leadProblem);
        var after = task.ResponseWindowSeconds ?? TrialRules.UnlimitedWindowTriggerAfterSeconds;
        if (-lead > after + 1e-9) problems.Add(string.Format("optogenetics.laserToStimulusSeconds ({0} s) puts the trigger more than {1} s after the first pip, past the {2}", lead, after, task.ResponseWindowSeconds.HasValue ? "responseWindowSeconds" : "limit without responseWindowSeconds"));
        var hold = task.Optogenetics.TriggerHoldSeconds;
        var holdMax = task.TrialStructure.ItiMinSeconds - lead - ZapitTrial.ReplyMarginSeconds;
        if (task.Optogenetics.Enabled && hold > holdMax + 1e-9) problems.Add(string.Format("optogenetics.triggerHoldSeconds ({0} s) must be at most {1} s (itiMinSeconds - laserToStimulusSeconds - {2} s): the next trial is armed once the line falls, and Zapit's reply needs {2} s before its trigger", hold, holdMax, ZapitTrial.ReplyMarginSeconds));
        if (task.StimulusOffProbability > 0)
        {
            var off = task.StimulusOff;
            if (off.TrackFractions == null || off.TrackFractions.Count == 0 || off.TrackFractions.Any(f => !(f > 0 && f < 1))) problems.Add("stimulusOffProbability is above 0 but stimulusOff.trackFractions is empty or not all between 0 and 1");
            if (off.Stimuli == null || off.Stimuli.Count == 0 || off.Stimuli.Distinct().Count() != off.Stimuli.Count) problems.Add("stimulusOffProbability is above 0 but stimulusOff.stimuli is empty or repeats a stimulus");
        }
        if (!(task.RewardFloorHz > sweep.StartHz && task.RewardFloorHz < task.RewardCeilingHz && task.RewardCeilingHz <= sweep.EndHz)) problems.Add(string.Format("the reward zone ({0} to {1} Hz) is not inside the sweep ({2} to {3} Hz)", task.RewardFloorHz, task.RewardCeilingHz, sweep.StartHz, sweep.EndHz));
        var opto = task.Optogenetics;
        if (opto.Enabled)
        {
            if (!(opto.LaserPowerMw > 0)) problems.Add("optogenetics.laserPowerMw must be a power in mW above 0 with optogenetics on");
            number("optogenetics.laserMaxSeconds", opto.LaserMaxSeconds, true);
            number("optogenetics.triggerHoldSeconds", opto.TriggerHoldSeconds, false);
            // The laser runs from the trigger (the first pip less laserToStimulusSeconds) to the outcome, at the latest the
            // first pip plus responseWindowSeconds: responseWindowSeconds + laserToStimulusSeconds.
            var window = task.ResponseWindowSeconds;
            if (window.HasValue && opto.LaserMaxSeconds < window.Value + opto.LaserToStimulusSeconds + TrialRules.LaserMaxMarginSeconds - 1e-9)
            {
                problems.Add(string.Format("optogenetics.laserMaxSeconds must be at least {0} s (responseWindowSeconds plus laserToStimulusSeconds plus {1} s)", window.Value + opto.LaserToStimulusSeconds + TrialRules.LaserMaxMarginSeconds, TrialRules.LaserMaxMarginSeconds));
            }
        }
        if (problems.Count > 0) throw new InvalidOperationException("The task cannot be run: " + string.Join("; ", problems) + ".");
    }

    public Trial Next()
    {
        var t = new Trial();
        t.trialNumber = ++drawn;
        t.rngSeed = Seed;
        var iti = task.TrialStructure.ItiMinSeconds + (task.TrialStructure.ItiMaxSeconds - task.TrialStructure.ItiMinSeconds) * random.NextDouble();
        t.itiSeconds = iti;  // trial 1 too, from the first clock tick, so Zapit is armed before its trigger
        var lengths = task.TrialStructure.TrackLengthsCm;
        t.trackLengthCm = lengths[random.Next(lengths.Count)] / task.WheelGain;  // wheel cm to the end
        var u = random.NextDouble();
        var fractions = task.StimulusOff.TrackFractions;
        var p = task.StimulusOffProbability;
        t.stimulusOffFraction = p > 0 && u < p ? fractions[(int)Math.Floor(u / p * fractions.Count)] : double.NaN;
        t.auditoryOff = !double.IsNaN(t.stimulusOffFraction) && task.StimulusOff.Stimuli.Contains(Stimuli.Auditory);
        t.visualOff = !double.IsNaN(t.stimulusOffFraction) && task.StimulusOff.Stimuli.Contains(Stimuli.Visual);
        t.lickQuietSeconds = task.LickQuietSeconds;
        t.wheelQuietSeconds = task.TrialStructure.WheelQuietSeconds;
        t.wheelQuietCm = task.TrialStructure.WheelQuietCm;
        t.rewardFloorHz = task.RewardFloorHz;
        t.rewardCeilingHz = task.RewardCeilingHz;
        t.rewardOfferedUl = task.Reward.RewardSizeUl;
        t.wrongLickLimit = task.WrongLickLimit;
        t.responseWindowSeconds = task.ResponseWindowSeconds ?? double.NaN;
        t.wheelGain = task.WheelGain;
        t.autoProgressPerSecond = task.AutoProgressPerSecond;
        var opto = task.Optogenetics;
        t.optogenetics = opto.Enabled;
        // The lead applies in every experiment, so timing never reveals Zapit.
        t.firstPipStep = Math.Max((int)Math.Round(opto.LaserToStimulusSeconds / TrialRules.StepSeconds), 0);
        if (opto.Enabled)
        {
            // Every trial sends Zapit a condition; laser-off trials at 0 mW (Pip, decision 11).
            ZapitTrial.Draw(random, t.trialNumber, conditionCount, opto.TrialsBeforeLaser, opto.LaserOnProbability, opto.LaserPowerMw.Value,
                out t.laserCondition, out t.laserOn, out t.laserPowerMw);
            t.laserMaxSeconds = opto.LaserMaxSeconds;
        }
        t.trialCondition = WeightedCondition();
        t.trialType = task.TrialConditions[t.trialCondition].Type;
        t.autoReward = random.NextDouble() < task.AutoRewardProbability;   // so 1 is every trial and 0 none
        return t;
    }

    /// <summary>A trial condition drawn with the weights as relative chances (TrialRules.Create refuses all 0).</summary>
    int WeightedCondition()
    {
        var conditions = task.TrialConditions;
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
[Description("Checks the task at Go and makes the rules the trials are drawn from and run by. Input: (task file, TrainingStage, rig.yml, sweep). Stops the workflow on a task whose trainingPipeline.id or sha256 is missing or whose id is not the TrainingStage, a reward zone outside the sweep or the optogenetics rules. A real mouse on a rig without hardware triggering is lib-open-rigs' ConnectZapit's refusal.")]
[WorkflowElementCategory(ElementCategory.Transform)]
public class CheckTaskAtGo
{
    public IObservable<TrialRules> Process(IObservable<Tuple<UclOpenCoenDomeNavigationTaskLogic, int, LabRig, Sweep>> source)
    {
        return source.Select(x => TrialRules.Create(x.Item1.TaskParameters, x.Item2, x.Item4, x.Item3.Zapit != null && x.Item3.Zapit.HardwareTriggered, null));
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
