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
    public string trialType;            // TrialDraw.TypeNames: what the trial shows, drawn from the trialWeights (the stage's, else the task's)
    public double itiSeconds;           // the interval before this trial: the last outcome (after any timeout; trial 1: the first clock tick) to the first pip, with a quiet mouse
    public double lickQuietSeconds;     // the stage's quiet waits, from the interval's start (QuietWait); 0: off
    public double wheelQuietSeconds;
    public double wheelQuietCm;
    public double trackLengthCm;
    public double truncationFraction;   // 1: not truncated
    public double rewardFloorHz;
    public double rewardCeilingHz;
    public double rewardOfferedUl;     // the stage's rewardSizeUl: what a reward on this trial gives
    public bool autoReward;             // drawn with the stage's autoRewardProbability: rewarded on reaching the floor, licks ignored
    public int wrongLickLimit;
    public double responseWindowSeconds;     // from the first pip; NaN: none
    public bool frequencyDrift;
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
/// until the stage's quiet waits hold, the trigger laserToStimulusSeconds before the first pip, the first pip the lead
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
        var positionStep = Math.Max(1, task.PositionStepCm);
        var position = Math.Round(track.Position / positionStep) * positionStep;
        var fraction = trial.trackLengthCm > 0 ? position / trial.trackLengthCm : 0;
        var tone = sweep.FrequencyAt(fraction);
        var visual = task.VisualStimulus;
        var gratingStart = visual.SpatialFrequencyStartCpd;
        var gratingRatio = visual.SpatialFrequencyEndCpd / gratingStart;
        var grating = gratingStart * Math.Pow(gratingRatio, Math.Max(0, Math.Min(1, fraction)));
        if (trial.frequencyDrift)
        {
            var drift = step * StepSeconds / task.DriftSeconds;
            tone += sweep.StartHz * Math.Pow(sweep.TrackRatio, drift) - sweep.StartHz;
            grating += gratingStart * Math.Pow(gratingRatio, drift) - gratingStart;
        }
        var active = trial.truncationFraction >= 1 || Math.Round(sweep.FractionOf(tone), 3) < trial.truncationFraction;
        return new StimulusFrame
        {
            Row = new StimulusStateRow { trialNumber = trial.trialNumber, positionCm = position, auditoryFrequencyHz = tone, stimulusActive = active, visualSpatialFrequencyCpd = grating },
            Bin = sweep.BinOf(tone),
            ToneGain = active && trial.trialType != TrialDraw.Visual ? 1 : 0,
            GratingOpacity = active && trial.trialType != TrialDraw.Auditory ? 1 : 0
        };
    }

    static bool OnTarget(Trial trial, double hz)
    {
        return hz >= trial.rewardFloorHz && hz <= trial.rewardCeilingHz;
    }

    /// <summary>The outcome (Tom Childs' ProcessTrialEnd): a trial that ends on a lick in the zone or the zone reached
    /// is rewarded; one that runs out of time or track in the zone is penalised, as is one ending outside it. Reaching the
    /// zone is the floor alone, as in Tom's FreqAboveThreshold: a drifting stage's tone can pass the ceiling (and the
    /// sweep's end) on the step it crosses the floor, and is rewarded.</summary>
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
            outcome = rewarded ? TrialOutcomes.Correct : ranOut ? TrialOutcomes.NoResponse : TrialOutcomes.Incorrect,
            endReason = reason == "Lick" ? (rewarded ? "CorrectLick" : "WrongLickLimit") : reason,
            trackLengthCm = trial.trackLengthCm,
            endPositionCm = track.Position,
            auditoryEndFrequencyHz = tone,
            visualEndSpatialFrequencyCpd = last != null ? last.Row.visualSpatialFrequencyCpd : 0,
            truncationFraction = trial.truncationFraction,
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
    /// <summary>The trial types the corridor shows, the trial row's trialType and the trialWeights' first keys (coen-dome-decision-making's names).</summary>
    public static readonly string[] TypeNames = { Visual, Auditory, AudiovisualCoherent };

    /// <summary>The weights a stage draws with: its own trialWeights, else the task's (the mouse's).</summary>
    public static TrialTypeWeights Effective(UclOpenCoenDomeNavigationTaskParameters task, TrainingStage stage)
    {
        return stage.TrialWeights ?? task.TrialWeights;
    }

    /// <summary>The effective trialWeights in TypeNames' order.</summary>
    public static int[] Weights(UclOpenCoenDomeNavigationTaskParameters task, TrainingStage stage)
    {
        var w = Effective(task, stage);
        return w == null ? new int[TypeNames.Length] : new[] { w.Visual, w.Auditory, w.AudiovisualCoherent };
    }

    /// <summary>What is wrong with the weights a stage draws with (task.py's TrialTypeWeights rules).</summary>
    public static List<string> WeightProblems(UclOpenCoenDomeNavigationTaskParameters task, TrainingStage stage)
    {
        var problems = new List<string>();
        var w = Effective(task, stage);
        var name = stage.TrialWeights != null ? "stage " + task.TrainingStages.IndexOf(stage) + "'s trialWeights" : "trialWeights";
        if (w == null) { problems.Add(name + " is missing"); return problems; }
        if (w.AudiovisualConflict > 0) problems.Add(name + ".audiovisualConflict must be 0: that trial is not defined in this task yet");
        if (w.Neutral > 0) problems.Add(name + ".neutral must be 0: that trial is not defined in this task yet");
        var all = new[] { w.Visual, w.Auditory, w.AudiovisualCoherent, w.AudiovisualConflict, w.Neutral };
        if (all.Any(x => x < 0) || all.Sum() <= 0) problems.Add(name + " needs every weight 0 or more and one above 0");
        return problems;
    }

    readonly UclOpenCoenDomeNavigationTaskParameters task;
    readonly TrainingStage stage;
    readonly Random random;
    readonly int conditionCount;
    int drawn;

    /// <summary>The seed the random stream started from: the task's rngSeed, or one drawn when it is null.</summary>
    public int Seed { get; private set; }

    /// <summary>The stage's row of the task file's trainingStages.</summary>
    public TrainingStage Stage { get { return stage; } }

    public TrialDraw(UclOpenCoenDomeNavigationTaskParameters task, int stage, Sweep sweep, Func<string, int> zapitConditionCount)
    {
        this.task = task;
        this.stage = CheckTask(task, stage, sweep);
        var opto = task.Optogenetics;
        if (opto.Enabled) conditionCount = zapitConditionCount != null ? zapitConditionCount(opto.ZapitConditionsPath) : ZapitConditions.Check(opto.ZapitConditionsPath);
        Seed = RngSeed.Resolve(task.RngSeed);
        random = new Random(Seed);
    }

    /// <summary>The stage's row, once the stage is in the table and the task can be run with the sweep: what pydantic
    /// would check (Bonsai reads the task file without it) and what only the sweep can say; throws naming every problem.</summary>
    public static TrainingStage CheckTask(UclOpenCoenDomeNavigationTaskParameters task, int stage, Sweep sweep)
    {
        if (stage < 0 || stage >= task.TrainingStages.Count)
        {
            throw new InvalidOperationException(string.Format("TrainingStage is {0}, but the task file's trainingStages has rows 0 to {1}.", stage, task.TrainingStages.Count - 1));
        }
        var row = task.TrainingStages[stage];
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
        // The quiet waits, the lab's one check (OpenRigs.Core QuietWait: finite, 0 or more, at most 10 s and 20 cm, the
        // wheel's pair both set or both 0): a long wait and a licking mouse would hold the trial start with no message.
        foreach (var p in QuietWait.SettingsProblems(row.LickQuietSeconds, row.WheelQuietSeconds, row.WheelQuietCm)) problems.Add("stage " + stage + ": " + p);
        number("stage " + stage + "'s trackLengthScale", row.TrackLengthScale, true);
        foreach (var p in TaskBlocks.AutoRewardProblems(row.AutoRewardProbability)) problems.Add("stage " + stage + ": " + p);
        number("driftSeconds", task.DriftSeconds, true);
        number("visualStimulus.spatialFrequencyStartCpd", task.VisualStimulus.SpatialFrequencyStartCpd, true);
        number("visualStimulus.spatialFrequencyEndCpd", task.VisualStimulus.SpatialFrequencyEndCpd, true);
        problems.AddRange(TrialDraw.WeightProblems(task, row));
        foreach (var p in TaskBlocks.ResponseWindowProblems(row.ResponseWindowSeconds)) problems.Add("stage " + stage + ": " + p);
        // The lead: the first pip comes max(laserToStimulusSeconds, 0) after the trial start in every experiment, the
        // trigger laserToStimulusSeconds before the first pip; whole steps, at most 0.2 s before it, inside the shortest interval
        // with Zapit's reply margin, and no later after it than the stage's response window.
        var lead = task.Optogenetics.LaserToStimulusSeconds;
        var leadSteps = lead / TrialRules.StepSeconds;
        if (!(Math.Abs(leadSteps - Math.Round(leadSteps)) < 1e-6)) problems.Add("optogenetics.laserToStimulusSeconds must be a whole number of 0.05 s steps");
        if (lead > TrialRules.MaxLeadSeconds + 1e-9) problems.Add(string.Format("optogenetics.laserToStimulusSeconds ({0} s) must be at most {1} s", lead, TrialRules.MaxLeadSeconds));
        var leadProblem = ZapitTrial.LeadProblem(lead, task.TrialStructure.ItiMinSeconds);
        if (leadProblem != null) problems.Add(leadProblem);
        var after = row.ResponseWindowSeconds ?? TrialRules.UnlimitedWindowTriggerAfterSeconds;
        if (-lead > after + 1e-9) problems.Add(string.Format("optogenetics.laserToStimulusSeconds ({0} s) puts the trigger more than {1} s after the first pip, past stage {2}'s {3}", lead, after, stage, row.ResponseWindowSeconds.HasValue ? "responseWindowSeconds" : "limit for a stage without responseWindowSeconds"));
        var hold = task.Optogenetics.TriggerHoldSeconds;
        var holdMax = task.TrialStructure.ItiMinSeconds - lead - ZapitTrial.ReplyMarginSeconds;
        if (task.Optogenetics.Enabled && hold > holdMax + 1e-9) problems.Add(string.Format("optogenetics.triggerHoldSeconds ({0} s) must be at most {1} s (itiMinSeconds - laserToStimulusSeconds - {2} s): the next trial is armed once the line falls, and Zapit's reply needs {2} s before its trigger", hold, holdMax, ZapitTrial.ReplyMarginSeconds));
        if (row.TruncatedTrialProbability > 0 && (task.Truncation.SweepFractions == null || task.Truncation.SweepFractions.Count == 0)) problems.Add("stage " + stage + " truncates trials but truncation.sweepFractions is empty");
        if (!(row.RewardFloorHz > sweep.StartHz && row.RewardFloorHz < row.RewardCeilingHz && row.RewardCeilingHz <= sweep.EndHz)) problems.Add(string.Format("stage {0}'s reward zone ({1} to {2} Hz) is not inside the sweep ({3} to {4} Hz)", stage, row.RewardFloorHz, row.RewardCeilingHz, sweep.StartHz, sweep.EndHz));
        var opto = task.Optogenetics;
        if (opto.Enabled)
        {
            if (!(opto.LaserPowerMw > 0)) problems.Add("optogenetics.laserPowerMw must be a power in mW above 0 with optogenetics on");
            number("optogenetics.laserMaxSeconds", opto.LaserMaxSeconds, true);
            number("optogenetics.triggerHoldSeconds", opto.TriggerHoldSeconds, false);
            // The laser runs from the trigger (the first pip less laserToStimulusSeconds) to the outcome, at the latest the
            // first pip plus responseWindowSeconds: responseWindowSeconds + laserToStimulusSeconds.
            var limits = task.TrainingStages.Where(s => s.ResponseWindowSeconds.HasValue).Select(s => s.ResponseWindowSeconds.Value).ToList();
            if (limits.Count > 0 && opto.LaserMaxSeconds < limits.Max() + opto.LaserToStimulusSeconds + TrialRules.LaserMaxMarginSeconds - 1e-9)
            {
                problems.Add(string.Format("optogenetics.laserMaxSeconds must be at least {0} s (the longest responseWindowSeconds plus laserToStimulusSeconds plus {1} s)", limits.Max() + opto.LaserToStimulusSeconds + TrialRules.LaserMaxMarginSeconds, TrialRules.LaserMaxMarginSeconds));
            }
        }
        if (problems.Count > 0) throw new InvalidOperationException("The task cannot be run: " + string.Join("; ", problems) + ".");
        return row;
    }

    public Trial Next()
    {
        var t = new Trial();
        t.trialNumber = ++drawn;
        t.rngSeed = Seed;
        var iti = task.TrialStructure.ItiMinSeconds + (task.TrialStructure.ItiMaxSeconds - task.TrialStructure.ItiMinSeconds) * random.NextDouble();
        t.itiSeconds = iti;  // trial 1 too, from the first clock tick, so Zapit is armed before its trigger
        var lengths = task.TrialStructure.TrackLengthsCm;
        t.trackLengthCm = lengths[random.Next(lengths.Count)] * stage.TrackLengthScale;
        var u = random.NextDouble();
        var fractions = task.Truncation.SweepFractions;
        t.truncationFraction = stage.TruncatedTrialProbability > 0 && u < stage.TruncatedTrialProbability
            ? fractions[(int)Math.Floor(u / stage.TruncatedTrialProbability * fractions.Count)]
            : 1.0;
        t.lickQuietSeconds = stage.LickQuietSeconds;
        t.wheelQuietSeconds = stage.WheelQuietSeconds;
        t.wheelQuietCm = stage.WheelQuietCm;
        t.rewardFloorHz = stage.RewardFloorHz;
        t.rewardCeilingHz = stage.RewardCeilingHz;
        t.rewardOfferedUl = stage.RewardSizeUl;
        t.wrongLickLimit = stage.WrongLickLimit;
        t.responseWindowSeconds = stage.ResponseWindowSeconds ?? double.NaN;
        t.frequencyDrift = stage.FrequencyDrift;
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
        t.trialType = TypeNames[WeightedType(task, stage)];
        t.autoReward = random.NextDouble() < stage.AutoRewardProbability;   // so 1 is every trial and 0 none
        return t;
    }

    /// <summary>A trial type drawn with the effective trialWeights as relative chances (TrialRules.Create refuses all 0).</summary>
    int WeightedType(UclOpenCoenDomeNavigationTaskParameters task, TrainingStage stage)
    {
        var weights = Weights(task, stage);
        int draw = random.Next(weights.Sum());
        for (int type = 0; type < weights.Length; type++)
        {
            draw -= weights[type];
            if (draw < 0) return type;
        }
        throw new InvalidOperationException("No trial type drawn.");
    }
}

[Combinator]
[Description("Checks the task at Go and makes the rules the trials are drawn from and run by. Input: (task file, TrainingStage, rig.yml, sweep). Stops the workflow on a stage past the table, a reward zone outside the sweep or the optogenetics rules. A real mouse on a rig without hardware triggering is lib-open-rigs' ConnectZapit's refusal.")]
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
