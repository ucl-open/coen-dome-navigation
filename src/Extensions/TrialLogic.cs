using Bonsai;
using OpenRigs.Core;
using OpenRigs.Devices;
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
    public string trialType;            // TrialDraw.TypeNames: what the trial shows, drawn from the stage's trialWeights
    public double itiSeconds;           // the interval before this trial: the last outcome (after any timeout; trial 1: the first clock tick) to the first pip, with a quiet mouse
    public double lickQuietSeconds;     // the stage's quiet waits, from the interval's start (QuietWait); 0: off
    public double wheelQuietSeconds;
    public double wheelQuietCm;
    public double trackLengthCm;
    public double truncationFraction;   // 1: not truncated
    public double rewardFloorHz;
    public double rewardCeilingHz;
    public double rewardSizeUl;
    public bool lickToReward;
    public int wrongLickLimit;
    public bool penaliseWrongZone;
    public double responseWindowSeconds;     // from the first pip; NaN: none
    public bool trackEndEndsTrial;
    public bool frequencyDrift;
    public int firstPipBeep;            // the lead, max(laserToStimulusSeconds, 0), in beeps after the trial start (beep 0), in every experiment
    public bool optogenetics;
    public int laserCondition;          // 0 without optogenetics
    public bool laserOn;
    public double laserPowerMw;         // 0 with the laser off
    public double laserMaxSeconds;      // the workflow's wall-clock backstop (LaserBackstop); the loop's LaserCap is TrialRules.Laser's
    public int rngSeed;                 // the experiment's seed, for the RngSeed event
}

/// <summary>A trial's outcome: its row, its reward or penalty and the timeout before the next interval.</summary>
public class TrialOutcome : ITrialOutcome
{
    public TrialRow Row;
    public bool Rewarded;
    public bool PenaltySound;                 // the penalty sound plays
    public Tuple<string, double> Reward;      // (side, microlitres) for Rewards: the left spout, the trial's rewardSizeUl

    public double PenaltyTimeoutSeconds { get { return Row.penaltyTimeoutSeconds; } }
}

/// <summary>
/// This task's rules for lib-open-rigs' TrialLoop, with everything fixed for the experiment that the trials are
/// drawn from and run on. The loop times each trial (Pip, 13 Nov 2026): its interval from the last outcome (after any
/// timeout; trial 1 from the first clock tick), a hardware-triggered Zapit armed during it once the last trial's trigger
/// line has fallen, the trial start (beep 0) at the interval's end less the lead, max(laserToStimulusSeconds, 0), or later
/// until the stage's quiet waits hold, the trigger laserToStimulusSeconds before the first pip, the first pip the lead
/// after the start. These rules run the trial along the virtual track: a pip every 50 ms on the trial start's grid, the
/// left spout's licks from the first pip, the end of the track, and the outcome.
/// </summary>
public class TrialRules : ITrialRules<Trial, StimulusFrame, TrialOutcome>
{
    public const double BeepSeconds = 0.05;       // task.py BEEP_SECONDS
    public const double LaserMaxMarginSeconds = 0.25; // task.py LASER_MAX_MARGIN_SECONDS
    public const double MaxLeadSeconds = 0.2;            // task.py MAX_LEAD_SECONDS: the furthest the trigger may come before the first pip
    public const double UnlimitedWindowTriggerAfterSeconds = 2.0; // task.py UNLIMITED_WINDOW_TRIGGER_AFTER_SECONDS: the furthest after the first pip it may come on a stage without responseWindowSeconds
    public const int DriftBeeps = 500;           // a drift stage's frequency reaches the sweep's end on its own after this many beeps (25 s)
    public const double GratingStartCpd = 0.03;   // the grating's spatial frequency at the track's start
    public const double GratingEndCpd = 0.3;      // and at its end

    public UclOpenCoenDomeNavigationTaskParameters Task;
    public TrainingStage Stage;
    public int TrainingStage;
    public Sweep Sweep;
    public int ConditionCount;
    public bool HardwareTriggered;
    public int Seed;

    TrialDraw draw;
    LaserTiming laser;
    VirtualTrack track;
    StimulusFrame last;
    int nextBeep, wrongLicks;

    /// <summary>Checks what pydantic would (Bonsai reads the task file without it) and what only the rig and the files
    /// can say; throws naming every problem.</summary>
    public static TrialRules Create(UclOpenCoenDomeNavigationTaskParameters task, int stage, Sweep sweep, bool hardwareTriggered, Func<string, int> zapitConditionCount)
    {
        if (stage < 0 || stage >= task.TrainingStages.Count)
        {
            throw new InvalidOperationException(string.Format("TrainingStage is {0}, but the task file's trainingStages has rows 0 to {1}.", stage, task.TrainingStages.Count - 1));
        }
        var rules = new TrialRules { Task = task, TrainingStage = stage, Stage = task.TrainingStages[stage], Sweep = sweep, HardwareTriggered = hardwareTriggered };
        var row = rules.Stage;
        var problems = new List<string>();
        // A NaN or infinite value (YAML's .nan or .inf) would hold the trial start or a penalty timeout for ever, with no error.
        Action<string, double, bool> number = (name, value, aboveZero) =>
        {
            if (!(aboveZero ? value > 0 : value >= 0) || double.IsInfinity(value)) problems.Add(name + (aboveZero ? " must be a finite number above 0" : " must be a finite number, 0 or more"));
        };
        if (task.TrialStructure.TrackLengthsCm == null || task.TrialStructure.TrackLengthsCm.Count == 0 || task.TrialStructure.TrackLengthsCm.Any(l => !(l > 0) || double.IsInfinity(l))) problems.Add("trialStructure.trackLengthsCm needs at least one length, each a finite number above 0");
        number("trialStructure.itiMinSeconds", task.TrialStructure.ItiMinSeconds, false);
        number("trialStructure.itiMaxSeconds", task.TrialStructure.ItiMaxSeconds, false);
        if (task.TrialStructure.ItiMaxSeconds < task.TrialStructure.ItiMinSeconds) problems.Add("trialStructure.itiMaxSeconds is below itiMinSeconds");
        number("penalty.timeoutSeconds", task.Penalty.TimeoutSeconds, false);
        // The quiet waits, the lab's one check (OpenRigs.Core QuietWait: finite, 0 or more, at most 10 s and 20 cm, the
        // wheel's pair both set or both 0): a long wait and a licking mouse would hold the trial start with no message.
        foreach (var p in QuietWait.SettingsProblems(row.LickQuietSeconds, row.WheelQuietSeconds, row.WheelQuietCm)) problems.Add("stage '" + row.Label + "': " + p);
        number("stage '" + row.Label + "''s trackLengthScale", row.TrackLengthScale, true);
        var weights = TrialDraw.Weights(row);
        if (weights.Any(w => w < 0) || weights.Sum() <= 0) problems.Add("stage '" + row.Label + "''s trialWeights needs every weight 0 or more and one above 0");
        if (row.ResponseWindowSeconds.HasValue) number("stage '" + row.Label + "''s responseWindowSeconds", row.ResponseWindowSeconds.Value, true);
        // The lead (Pip, 13 Nov 2026): the first pip comes max(laserToStimulusSeconds, 0) after the trial start in every experiment, the
        // trigger laserToStimulusSeconds before the first pip; whole beeps, at most 0.2 s before it, inside the shortest interval
        // with Zapit's reply margin, and no later after it than the stage's response window.
        var lead = task.Optogenetics.LaserToStimulusSeconds;
        var leadBeeps = lead / BeepSeconds;
        if (!(Math.Abs(leadBeeps - Math.Round(leadBeeps)) < 1e-6)) problems.Add("optogenetics.laserToStimulusSeconds must be a whole number of 0.05 s beeps");
        if (lead > MaxLeadSeconds + 1e-9) problems.Add(string.Format("optogenetics.laserToStimulusSeconds ({0} s) must be at most {1} s", lead, MaxLeadSeconds));
        var leadProblem = ZapitTrial.LeadProblem(lead, task.TrialStructure.ItiMinSeconds);
        if (leadProblem != null) problems.Add(leadProblem);
        var after = row.ResponseWindowSeconds ?? UnlimitedWindowTriggerAfterSeconds;
        if (-lead > after + 1e-9) problems.Add(string.Format("optogenetics.laserToStimulusSeconds ({0} s) puts the trigger more than {1} s after the first pip, past stage '{2}''s {3}", lead, after, row.Label, row.ResponseWindowSeconds.HasValue ? "responseWindowSeconds" : "limit for a stage without responseWindowSeconds"));
        var hold = task.Optogenetics.TriggerHoldSeconds;
        var holdMax = task.TrialStructure.ItiMinSeconds - lead - ZapitTrial.ReplyMarginSeconds;
        if (task.Optogenetics.UseZapit && hold > holdMax + 1e-9) problems.Add(string.Format("optogenetics.triggerHoldSeconds ({0} s) must be at most {1} s (itiMinSeconds - laserToStimulusSeconds - {2} s): the next trial is armed once the line falls, and Zapit's reply needs {2} s before its trigger", hold, holdMax, ZapitTrial.ReplyMarginSeconds));
        if (row.TruncatedTrialFraction > 0 && (task.Truncation.SweepFractions == null || task.Truncation.SweepFractions.Count == 0)) problems.Add("stage '" + row.Label + "' truncates trials but truncation.sweepFractions is empty");
        if (!(row.RewardFloorHz > sweep.StartHz && row.RewardFloorHz < row.RewardCeilingHz && row.RewardCeilingHz <= sweep.EndHz)) problems.Add(string.Format("stage '{0}''s reward zone ({1} to {2} Hz) is not inside the sweep ({3} to {4} Hz)", row.Label, row.RewardFloorHz, row.RewardCeilingHz, sweep.StartHz, sweep.EndHz));
        var opto = task.Optogenetics;
        if (opto.UseZapit)
        {
            if (!(opto.LaserPowerMw > 0)) problems.Add("optogenetics.laserPowerMw must be a power in mW above 0 with optogenetics on");
            number("optogenetics.laserMaxSeconds", opto.LaserMaxSeconds, true);
            number("optogenetics.triggerHoldSeconds", opto.TriggerHoldSeconds, false);
            // The laser runs from the trigger (the first pip less laserToStimulusSeconds) to the outcome, at the latest the
            // first pip plus responseWindowSeconds: responseWindowSeconds + laserToStimulusSeconds.
            var limits = task.TrainingStages.Where(s => s.ResponseWindowSeconds.HasValue).Select(s => s.ResponseWindowSeconds.Value).ToList();
            if (limits.Count > 0 && opto.LaserMaxSeconds < limits.Max() + opto.LaserToStimulusSeconds + LaserMaxMarginSeconds - 1e-9)
            {
                problems.Add(string.Format("optogenetics.laserMaxSeconds must be at least {0} s (the longest responseWindowSeconds plus laserToStimulusSeconds plus {1} s)", limits.Max() + opto.LaserToStimulusSeconds + LaserMaxMarginSeconds, LaserMaxMarginSeconds));
            }
        }
        if (problems.Count > 0) throw new InvalidOperationException("The task cannot be run: " + string.Join("; ", problems) + ".");
        if (opto.UseZapit)
        {
            rules.ConditionCount = zapitConditionCount != null ? zapitConditionCount(opto.ZapitConfigPath) : ZapitConditions.Check(opto.ZapitConfigPath);
            // Every StartStimulus continuous: the laser on from the trigger until the outcome or LaserCap, the trigger line
            // (and the masking light on it) held triggerHoldSeconds past the outcome, the next trial armed once it falls.
            rules.laser = LaserTiming.UntilOutcome(hardwareTriggered, opto.TriggerHoldSeconds, opto.LaserMaxSeconds);
        }
        rules.Seed = RngSeed.Resolve(task.RngSeed);
        rules.draw = new TrialDraw(rules);
        return rules;
    }

    /// <summary>The laser in an optogenetics experiment (the trigger line only rises in one, Pip, 13 Nov 2026); null without.</summary>
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
            // The lead applies in every experiment, so timing never reveals Zapit (Pip, 13 Nov 2026).
            LaserToStimulusSeconds = Task.Optogenetics.LaserToStimulusSeconds,
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
        nextBeep = trial.firstPipBeep;
        wrongLicks = 0;
        last = null;
    }

    public void Stimulus(Trial trial, TrialTimes times, ICollection<StimulusFrame> updates)
    {
        // The stimulus's tick is the first pip (the lead is whole beeps): Tick plays it.
    }

    /// <summary>A pip every 50 ms on the trial start's grid; after a gap in the Harp clock only the latest beep due, the
    /// missed pips not played at once. A stage without lickToReward ends on the pip that reaches the reward floor.</summary>
    public TrialOutcome Tick(Trial trial, TrialTimes times, ICollection<StimulusFrame> updates)
    {
        var due = Math.Max((int)Math.Floor((times.Now - times.TrialStartTime + 1e-9) / BeepSeconds), trial.firstPipBeep);
        if (due < nextBeep) return null;
        nextBeep = due + 1;
        last = Frame(trial, due);
        updates.Add(last);
        return !trial.lickToReward && last.Row.toneFrequencyHz >= trial.rewardFloorHz ? End(trial, "FreqReached", times) : null;
    }

    /// <summary>The left spout's licks count from the first pip: in the zone rewarded, outside toward wrongLickLimit.</summary>
    public TrialOutcome Lick(Trial trial, int spout, TrialTimes times, ICollection<StimulusFrame> updates)
    {
        if (spout != 0 || last == null || !trial.lickToReward) return null;
        if (OnTarget(trial, last.Row.toneFrequencyHz)) return End(trial, "Lick", times);
        wrongLicks++;
        return trial.wrongLickLimit > 0 && wrongLicks >= trial.wrongLickLimit ? End(trial, "WrongLickLimit", times) : null;
    }

    /// <summary>The track's position from the trial start, and the trial's end at the end of the track (during the lead too).</summary>
    public TrialOutcome Wheel(Trial trial, double cm, TrialTimes times, ICollection<StimulusFrame> updates)
    {
        track.Update(cm);
        return trial.trackEndEndsTrial && track.AtEnd ? End(trial, "EndOfTrack", times) : null;
    }

    /// <summary>responseWindowSeconds from the first pip.</summary>
    public TrialOutcome ResponseEnd(Trial trial, TrialTimes times)
    {
        return End(trial, "ResponseWindowEnd", times);
    }

    StimulusFrame Frame(Trial trial, int beep)
    {
        var step = Math.Max(1, Task.PositionStepCm);
        var position = Math.Round(track.Position / step) * step;
        var fraction = trial.trackLengthCm > 0 ? position / trial.trackLengthCm : 0;
        var tone = Sweep.FrequencyAt(fraction);
        var grating = GratingStartCpd * Math.Pow(GratingEndCpd / GratingStartCpd, Math.Max(0, Math.Min(1, fraction)));
        if (trial.frequencyDrift)
        {
            var drift = (double)beep / DriftBeeps;
            tone += Sweep.StartHz * Math.Pow(Sweep.TrackRatio, drift) - Sweep.StartHz;
            grating += GratingStartCpd * Math.Pow(GratingEndCpd / GratingStartCpd, drift) - GratingStartCpd;
        }
        var active = trial.truncationFraction >= 1 || Math.Round(Sweep.FractionOf(tone), 3) < trial.truncationFraction;
        return new StimulusFrame
        {
            Row = new StimulusStateRow { trialNumber = trial.trialNumber, positionCm = position, toneFrequencyHz = tone, stimulusActive = active, visualSpatialFrequencyCpd = grating },
            Bin = Sweep.BinOf(tone),
            ToneGain = active && trial.trialType != TrialDraw.Visual ? 1 : 0,
            GratingOpacity = active && trial.trialType != TrialDraw.Auditory ? 1 : 0
        };
    }

    static bool OnTarget(Trial trial, double hz)
    {
        return hz >= trial.rewardFloorHz && hz <= trial.rewardCeilingHz;
    }

    /// <summary>The outcome (Tom Childs' ProcessTrialEnd): a trial that ends on a lick in the zone or the zone reached
    /// is rewarded; one that runs out of time or track in the zone is penalised, as is one ending outside it on a stage
    /// with penaliseWrongZone. Reaching the zone is the floor alone, as in Tom's FreqAboveThreshold: a drifting stage's
    /// tone can pass the ceiling (and the sweep's end) on the beep it crosses the floor, and is rewarded.</summary>
    TrialOutcome End(Trial trial, string reason, TrialTimes times)
    {
        var tone = last != null ? last.Row.toneFrequencyHz : 0;
        var onTarget = reason == "FreqReached" || OnTarget(trial, tone);
        var ranOut = reason == "ResponseWindowEnd" || reason == "EndOfTrack";
        var rewarded = onTarget && (reason == "Lick" || reason == "FreqReached");
        var penalised = onTarget ? ranOut : trial.penaliseWrongZone;
        // The shared block (trialNumber, the times, penaltySound, penaltyTimeoutSeconds, the laser) is lib-open-rigs'.
        var row = SharedTrialColumns.Fill(new TrialRow
        {
            trialType = trial.trialType,
            outcome = rewarded ? "Correct" : ranOut || !trial.lickToReward ? "NoResponse" : "Incorrect",
            rewardSizeUl = trial.rewardSizeUl,
            endReason = reason == "Lick" ? (rewarded ? "CorrectLick" : "WrongLickLimit") : reason,
            trackLengthCm = trial.trackLengthCm,
            endPositionCm = track.Position,
            toneEndFrequencyHz = tone,
            visualEndSpatialFrequencyCpd = last != null ? last.Row.visualSpatialFrequencyCpd : 0,
            truncationFraction = trial.truncationFraction
        }, trial, times, penalised, Task.Penalty.TimeoutSeconds, Task.Penalty.Sound.Enabled);
        return new TrialOutcome { Row = row, Rewarded = rewarded, PenaltySound = row.penaltySound, Reward = Tuple.Create("left", trial.rewardSizeUl) };
    }
}

/// <summary>Draws every trial of an experiment from one random stream seeded with the task's rngSeed.</summary>
public class TrialDraw
{
    public const string Auditory = "auditory";       // the tone sweep alone
    public const string Visual = "visual";           // the grating alone
    public const string Audiovisual = "audiovisual"; // both
    /// <summary>The trial types, the keys of a stage's trialWeights and the trial row's trialType (coen-dome-decision-making's names where they overlap).</summary>
    public static readonly string[] TypeNames = { Auditory, Visual, Audiovisual };

    /// <summary>A stage's trialWeights in TypeNames' order.</summary>
    public static int[] Weights(TrainingStage stage)
    {
        var w = stage.TrialWeights;
        return w == null ? new int[TypeNames.Length] : new[] { w.Auditory, w.Visual, w.Audiovisual };
    }

    readonly TrialRules rules;
    readonly Random random;
    int drawn;

    public TrialDraw(TrialRules rules)
    {
        this.rules = rules;
        random = new Random(rules.Seed);
    }

    public Trial Next()
    {
        var task = rules.Task;
        var stage = rules.Stage;
        var t = new Trial();
        t.trialNumber = ++drawn;
        t.rngSeed = rules.Seed;
        var iti = task.TrialStructure.ItiMinSeconds + (task.TrialStructure.ItiMaxSeconds - task.TrialStructure.ItiMinSeconds) * random.NextDouble();
        t.itiSeconds = iti;  // trial 1 too, from the first clock tick, so Zapit is armed before its trigger
        var lengths = task.TrialStructure.TrackLengthsCm;
        t.trackLengthCm = lengths[random.Next(lengths.Count)] * stage.TrackLengthScale;
        var u = random.NextDouble();
        var fractions = task.Truncation.SweepFractions;
        t.truncationFraction = stage.TruncatedTrialFraction > 0 && u < stage.TruncatedTrialFraction
            ? fractions[(int)Math.Floor(u / stage.TruncatedTrialFraction * fractions.Count)]
            : 1.0;
        t.lickQuietSeconds = stage.LickQuietSeconds;
        t.wheelQuietSeconds = stage.WheelQuietSeconds;
        t.wheelQuietCm = stage.WheelQuietCm;
        t.rewardFloorHz = stage.RewardFloorHz;
        t.rewardCeilingHz = stage.RewardCeilingHz;
        t.rewardSizeUl = stage.RewardSizeUl;
        t.lickToReward = stage.LickToReward;
        t.wrongLickLimit = stage.WrongLickLimit;
        t.penaliseWrongZone = stage.PenaliseWrongZone;
        t.responseWindowSeconds = stage.ResponseWindowSeconds ?? double.NaN;
        t.trackEndEndsTrial = stage.TrackEndEndsTrial;
        t.frequencyDrift = stage.FrequencyDrift;
        var opto = task.Optogenetics;
        t.optogenetics = opto.UseZapit;
        // The lead applies in every experiment, so timing never reveals Zapit (Pip, 13 Nov 2026).
        t.firstPipBeep = Math.Max((int)Math.Round(opto.LaserToStimulusSeconds / TrialRules.BeepSeconds), 0);
        if (opto.UseZapit)
        {
            // Every trial sends Zapit a condition; laser-off trials at 0 mW (Pip, decision 11).
            ZapitTrial.Draw(random, t.trialNumber, rules.ConditionCount, opto.TrialsBeforeLaser, opto.LaserOnProbability, opto.LaserPowerMw.Value,
                out t.laserCondition, out t.laserOn, out t.laserPowerMw);
            t.laserMaxSeconds = opto.LaserMaxSeconds;
        }
        t.trialType = TypeNames[WeightedType(stage)];   // drawn last, so the draws above are as before trialWeights
        return t;
    }

    /// <summary>A trial type drawn with the stage's trialWeights as relative chances (TrialRules.Create refuses all 0).</summary>
    int WeightedType(TrainingStage stage)
    {
        var weights = Weights(stage);
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
[Description("Runs the trials (lib-open-rigs TrialLoop) by the task's rules. Inputs: the rules, the Harp clock in seconds, the wheel in cm, the left lick state, Zapit's replies. The right spout is not the task's: it neither answers nor holds the trial start.")]
[WorkflowElementCategory(ElementCategory.Combinator)]
public class RunTrials
{
    public IObservable<TrialEvent<Trial, StimulusFrame, TrialOutcome>> Process(IObservable<TrialRules> rules, IObservable<double> clock,
        IObservable<double> wheelCm, IObservable<bool> lickLeft, IObservable<ZapitReply> zapitReplies)
    {
        return TrialLoop.Observe<Trial, StimulusFrame, TrialOutcome>(rules, clock, wheelCm, lickLeft, Observable.Never<bool>(), zapitReplies);
    }
}
