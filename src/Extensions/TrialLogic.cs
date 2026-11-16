using Bonsai;
using Bonsai.Harp;
using Newtonsoft.Json.Linq;
using OpenCV.Net;
using OpenRigs.Core;
using OpenRigs.Devices.Zapit;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using OpenRigs.Devices.Rigs;
using UclOpenCoenDomeNavigationTask;

// The corridor task in tested C# (plan v11, W3): each trial's draw, its interval, its run along the virtual track on
// the Harp clock and its row. main.bonsai drives the hardware from the events CorridorTask gives; tests\Extensions.Tests
// runs the same classes on stand-in clocks.

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
    int IZapitTrial.TrialNumber { get { return trialNumber; } }
    int IZapitTrial.LaserCondition { get { return laserCondition; } }
    bool IZapitTrial.LaserOn { get { return laserOn; } }
    double IZapitTrial.LaserPowerMw { get { return laserPowerMw; } }

    public int trialNumber;
    public string trialType;            // the task's modality in words
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
    public double timeLimitSeconds;     // from the first pip; NaN: none
    public bool trackEndEndsTrial;
    public bool frequencyDrift;
    public int firstPipBeep;            // the lead, max(laserToStimulusSeconds, 0), in beeps after the trial start (beep 0), in every experiment
    public int triggerBeep;             // the trigger's beep after the trial start: firstPipBeep - laserToStimulusSeconds in beeps; optogenetics only
    public bool optogenetics;
    public bool armZapit;               // hardware-triggered Zapit: armed during the interval, before the trigger
    public int laserCondition;          // 0 without optogenetics
    public bool laserOn;
    public double laserPowerMw;         // 0 with the laser off
    public double laserMaxSeconds;
    public double triggerHoldSeconds;   // the trigger line stays high this long after the outcome; optogenetics only
    public int rngSeed;                 // the experiment's seed, for the RngSeed event
}

/// <summary>Everything fixed for the experiment that the trials are drawn from and run on.</summary>
public class CorridorSetup
{
    public const double BeepSeconds = 0.05;       // task.py BEEP_SECONDS
    public const double LaserMaxMarginSeconds = 0.25; // task.py LASER_MAX_MARGIN_SECONDS
    public const double MaxLeadSeconds = 0.2;            // task.py MAX_LEAD_SECONDS: the furthest the trigger may come before the first pip
    public const double NoTimeLimitTriggerAfterSeconds = 2.0; // task.py NO_TIME_LIMIT_TRIGGER_AFTER_SECONDS: the furthest after the first pip it may come on a stage without timeLimitSeconds
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
    public bool Audio;
    public bool Visual;

    /// <summary>Checks what pydantic would (Bonsai reads the task file without it) and what only the rig and the files
    /// can say; throws naming every problem.</summary>
    public static CorridorSetup Create(UclOpenCoenDomeNavigationTaskParameters task, int stage, Sweep sweep, bool hardwareTriggered, Func<string, int> zapitConditionCount)
    {
        if (stage < 0 || stage >= task.TrainingStages.Count)
        {
            throw new InvalidOperationException(string.Format("TrainingStage is {0}, but the task file's trainingStages has rows 0 to {1}.", stage, task.TrainingStages.Count - 1));
        }
        var setup = new CorridorSetup { Task = task, TrainingStage = stage, Stage = task.TrainingStages[stage], Sweep = sweep, HardwareTriggered = hardwareTriggered };
        var row = setup.Stage;
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
        if (row.TimeLimitSeconds.HasValue) number("stage '" + row.Label + "''s timeLimitSeconds", row.TimeLimitSeconds.Value, true);
        // The lead (Pip, 13 Nov 2026): the first pip comes max(laserToStimulusSeconds, 0) after the trial start in every experiment, the
        // trigger laserToStimulusSeconds before the first pip; whole beeps, at most 0.2 s before it, inside the shortest interval
        // with Zapit's reply margin, and no later after it than the stage's time limit.
        var lead = task.Optogenetics.LaserToStimulusSeconds;
        var leadBeeps = lead / BeepSeconds;
        if (!(Math.Abs(leadBeeps - Math.Round(leadBeeps)) < 1e-6)) problems.Add("optogenetics.laserToStimulusSeconds must be a whole number of 0.05 s beeps");
        if (lead > MaxLeadSeconds + 1e-9) problems.Add(string.Format("optogenetics.laserToStimulusSeconds ({0} s) must be at most {1} s", lead, MaxLeadSeconds));
        var leadProblem = ZapitTrial.LeadProblem(lead, task.TrialStructure.ItiMinSeconds);
        if (leadProblem != null) problems.Add(leadProblem);
        var after = row.TimeLimitSeconds ?? NoTimeLimitTriggerAfterSeconds;
        if (-lead > after + 1e-9) problems.Add(string.Format("optogenetics.laserToStimulusSeconds ({0} s) puts the trigger more than {1} s after the first pip, past stage '{2}''s {3}", lead, after, row.Label, row.TimeLimitSeconds.HasValue ? "timeLimitSeconds" : "limit for a stage without timeLimitSeconds"));
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
            // first pip plus timeLimitSeconds: timeLimitSeconds + laserToStimulusSeconds.
            var limits = task.TrainingStages.Where(s => s.TimeLimitSeconds.HasValue).Select(s => s.TimeLimitSeconds.Value).ToList();
            if (limits.Count > 0 && opto.LaserMaxSeconds < limits.Max() + opto.LaserToStimulusSeconds + LaserMaxMarginSeconds - 1e-9)
            {
                problems.Add(string.Format("optogenetics.laserMaxSeconds must be at least {0} s (the longest timeLimitSeconds plus laserToStimulusSeconds plus {1} s)", limits.Max() + opto.LaserToStimulusSeconds + LaserMaxMarginSeconds, LaserMaxMarginSeconds));
            }
        }
        if (problems.Count > 0) throw new InvalidOperationException("The task cannot be run: " + string.Join("; ", problems) + ".");
        if (opto.UseZapit) setup.ConditionCount = zapitConditionCount != null ? zapitConditionCount(opto.ZapitConfigPath) : ZapitConditions.Check(opto.ZapitConfigPath);
        setup.Seed = RngSeed.Resolve(task.RngSeed);
        setup.Audio = task.Modality != UclOpenCoenDomeNavigationTaskParametersModality.Visual;
        setup.Visual = task.Modality != UclOpenCoenDomeNavigationTaskParametersModality.Auditory;
        return setup;
    }
}

/// <summary>Draws every trial of an experiment from one random stream seeded with the task's rngSeed.</summary>
public class TrialDraw
{
    readonly CorridorSetup setup;
    readonly Random random;
    int drawn;

    public TrialDraw(CorridorSetup setup)
    {
        this.setup = setup;
        random = new Random(setup.Seed);
    }

    public Trial Next()
    {
        var task = setup.Task;
        var stage = setup.Stage;
        var t = new Trial();
        t.trialNumber = ++drawn;
        t.rngSeed = setup.Seed;
        t.trialType = task.Modality.ToString().ToLowerInvariant();
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
        t.timeLimitSeconds = stage.TimeLimitSeconds ?? double.NaN;
        t.trackEndEndsTrial = stage.TrackEndEndsTrial;
        t.frequencyDrift = stage.FrequencyDrift;
        var opto = task.Optogenetics;
        t.optogenetics = opto.UseZapit;
        // The lead applies in every experiment, so timing never reveals Zapit (Pip, 13 Nov 2026).
        var leadBeeps = (int)Math.Round(opto.LaserToStimulusSeconds / CorridorSetup.BeepSeconds);
        t.firstPipBeep = Math.Max(leadBeeps, 0);
        t.triggerBeep = t.firstPipBeep - leadBeeps;
        if (opto.UseZapit)
        {
            // The trigger line (and the masking light on it) rises only in an optogenetics experiment (Pip, 13 Nov 2026).
            t.triggerHoldSeconds = opto.TriggerHoldSeconds;
            // Every trial sends Zapit a condition; laser-off trials at 0 mW (Pip, decision 11).
            t.armZapit = setup.HardwareTriggered;
            ZapitTrial.Draw(random, t.trialNumber, setup.ConditionCount, opto.TrialsBeforeLaser, opto.LaserOnProbability, opto.LaserPowerMw.Value,
                out t.laserCondition, out t.laserOn, out t.laserPowerMw);
            t.laserMaxSeconds = opto.LaserMaxSeconds;
        }
        return t;
    }
}

public enum CorridorEventKind
{
    Arm,        // during the interval: send the trial's StartStimulus to a hardware-triggered Zapit
    Start,      // beep 0: the trial starts
    Trigger,    // optogenetics: laserToStimulusSeconds before the first pip, the trigger line up (a software-triggered Zapit's StartStimulus)
    Pip,        // a beep with the stimulus on: the tone pip and grating frame
    Outcome,    // the trial ended: its row, the reward or penalty
    LaserCap    // laserMaxSeconds after the trigger with no outcome yet: stop the laser
}

/// <summary>One thing the task asks the hardware to do, at a Harp time.</summary>
public class CorridorEvent
{
    public CorridorEventKind Kind;
    public double Seconds;
    public Trial Trial;
    public StimulusFrame Frame;   // Pip
    public TrialRow Row;          // Outcome
    public bool Rewarded;         // Outcome
    public bool PenaltySound;       // Outcome: the penalty sound plays
    public string RewardSide;     // Outcome: "left", the spout the task rewards

    public Tuple<string, double> Reward { get { return Tuple.Create(RewardSide, Trial.rewardSizeUl); } }
}

/// <summary>
/// The task's state from one trial to the next, driven by the Harp clock, the wheel, the left lick detector and
/// Zapit's replies; every wait is on the Harp clock. A trial (Pip, 13 Nov 2026): its interval from the last outcome
/// (after any timeout; trial 1 from the first clock tick), during which a hardware-triggered Zapit is armed (once the
/// last trial's trigger line has fallen); the trial start (beep 0) at the interval's end less the lead, max(laserToStimulusSeconds, 0),
/// or later until the stage's quiet waits hold; the first pip the lead after the trial start and the trigger laserToStimulusSeconds
/// before it (a reply not in by then stops the task); beeps every 50 ms until the outcome; then the penalty timeout.
/// </summary>
public class CorridorState
{
    enum Phase { Waiting, Interval, Running, Timeout }

    readonly CorridorSetup setup;
    readonly TrialDraw draw;
    Phase phase = Phase.Waiting;
    Trial trial;
    VirtualTrack track;
    QuietWait wait;
    bool wheelSeen, touched;   // touched: the lick detector's state now
    double now = double.NaN, wheel, itiStart = double.NaN, waitUntil, start, onset, quietWait, armAt, lastOutcome = double.NaN, laserOnset;
    int nextBeep, wrongLicks;
    bool capped, armSent, armed, triggered;
    StimulusFrame last;

    public CorridorState(CorridorSetup setup)
    {
        this.setup = setup;
        draw = new TrialDraw(setup);
    }

    public Trial Trial { get { return trial; } }

    public void Wheel(double cm)
    {
        wheel = cm;
        wheelSeen = true;
    }

    /// <summary>The wheel moved: the quiet wait in the interval; the track's position, and the trial's end at the end of the track, while it runs.</summary>
    public IEnumerable<CorridorEvent> OnWheel(double cm)
    {
        Wheel(cm);
        if (phase == Phase.Interval && !double.IsNaN(now)) wait.Wheel(cm, now);
        if (phase != Phase.Running) yield break;
        track.Update(cm);
        if (trial.trackEndEndsTrial && track.AtEnd) yield return End("EndOfTrack");
    }

    public IEnumerable<CorridorEvent> OnLick(bool lick)
    {
        if (lick == touched) yield break;
        touched = lick;
        if (phase == Phase.Interval && !double.IsNaN(now)) wait.Lick(touched, now);
        if (!lick) yield break;
        if (phase != Phase.Running || last == null || !trial.lickToReward) yield break;  // licks count from the first pip
        if (OnTarget(last.Row.toneFrequencyHz)) { yield return End("Lick"); yield break; }
        wrongLicks++;
        if (trial.wrongLickLimit > 0 && wrongLicks >= trial.wrongLickLimit) yield return End("WrongLickLimit");
    }

    /// <summary>Zapit's reply once the trial's StartStimulus is sent: it must have armed the trial's condition as asked
    /// (OpenRigs.Devices ZapitTrial.CheckArmed, the dome tasks' one check).</summary>
    public IEnumerable<CorridorEvent> OnZapitReply(ZapitReply reply)
    {
        if (trial == null || !armSent || armed || reply.Command != ZapitCommandType.StartStimulus) yield break;
        ZapitTrial.CheckArmed(reply, trial.trialNumber, trial.laserCondition, trial.laserOn);
        armed = true;
    }

    public IEnumerable<CorridorEvent> OnClock(double seconds)
    {
        if (!double.IsNaN(now) && seconds < now) throw new InvalidOperationException(string.Format("The Harp clock went backwards, from {0} s to {1} s.", now, seconds));
        now = seconds;
        var events = new List<CorridorEvent>();
        if (phase == Phase.Waiting) { itiStart = now; NextTrial(); }
        if (phase == Phase.Timeout && now >= waitUntil - 1e-9) { itiStart = waitUntil; NextTrial(); }
        if (trial != null && trial.armZapit && !armSent && phase != Phase.Timeout && now >= armAt - 1e-9)
        {
            armSent = true;
            events.Add(new CorridorEvent { Kind = CorridorEventKind.Arm, Seconds = now, Trial = trial });
        }
        if (phase == Phase.Interval && now >= wait.TrialStartTime - 1e-9) events.AddRange(Begin());
        else if (phase == Phase.Running) events.AddRange(Run());
        return events;
    }

    void NextTrial()
    {
        trial = draw.Next();
        phase = Phase.Interval;
        var lead = trial.firstPipBeep * CorridorSetup.BeepSeconds;
        wait = new QuietWait(itiStart, itiStart + trial.itiSeconds - lead, trial.lickQuietSeconds, trial.wheelQuietSeconds, trial.wheelQuietCm, wheelSeen ? wheel : double.NaN, touched);
        // Armed once the last trial's trigger line has fallen (its ramp-down done), at the interval's start at the soonest.
        armAt = double.IsNaN(lastOutcome) ? itiStart : Math.Max(itiStart, lastOutcome + trial.triggerHoldSeconds);
        armSent = false;
        armed = false;
    }

    IEnumerable<CorridorEvent> Begin()
    {
        phase = Phase.Running;
        start = wait.TrialStartTime;
        quietWait = wait.TrialStartTime - wait.Earliest;
        onset = double.NaN;
        nextBeep = 0;
        wrongLicks = 0;
        capped = false;
        triggered = false;
        last = null;
        track = new VirtualTrack(trial.trackLengthCm, wheel);
        track.Update(wheel);
        return new List<CorridorEvent> { new CorridorEvent { Kind = CorridorEventKind.Start, Seconds = start, Trial = trial } }.Concat(Run());
    }

    List<CorridorEvent> Run()
    {
        var events = new List<CorridorEvent>();
        // Only the latest beep due: after a gap in the Harp clock the missed pips are not played at once.
        var due = (int)Math.Floor((now - start + 1e-9) / CorridorSetup.BeepSeconds);
        if (phase == Phase.Running && trial.optogenetics && !triggered && due >= trial.triggerBeep)
        {
            if (trial.armZapit && !armed)
            {
                throw ZapitTrial.NotArmedByTrigger(trial.trialNumber, now);
            }
            triggered = true;
            if (!trial.armZapit) armSent = true;  // a software-triggered Zapit is sent its StartStimulus with the Trigger event
            laserOnset = now;
            events.Add(new CorridorEvent { Kind = CorridorEventKind.Trigger, Seconds = now, Trial = trial });
        }
        if (phase == Phase.Running && due >= nextBeep && due >= trial.firstPipBeep)
        {
            nextBeep = due + 1;
            last = Frame(due);
            if (double.IsNaN(onset)) onset = now;
            events.Add(new CorridorEvent { Kind = CorridorEventKind.Pip, Seconds = now, Trial = trial, Frame = last });
            if (!trial.lickToReward && last.Row.toneFrequencyHz >= trial.rewardFloorHz) events.Add(End("FreqReached"));
        }
        if (phase == Phase.Running && !double.IsNaN(trial.timeLimitSeconds) && now >= start + trial.firstPipBeep * CorridorSetup.BeepSeconds + trial.timeLimitSeconds - 1e-9)
        {
            events.Add(End("Timeout"));
        }
        if (phase == Phase.Running && triggered && !capped && now >= laserOnset + trial.laserMaxSeconds - 1e-9)
        {
            capped = true;
            events.Add(new CorridorEvent { Kind = CorridorEventKind.LaserCap, Seconds = now, Trial = trial });
        }
        return events;
    }

    StimulusFrame Frame(int beep)
    {
        var sweep = setup.Sweep;
        var step = Math.Max(1, setup.Task.PositionStepCm);
        var position = Math.Round(track.Position / step) * step;
        var fraction = trial.trackLengthCm > 0 ? position / trial.trackLengthCm : 0;
        var tone = sweep.FrequencyAt(fraction);
        var grating = CorridorSetup.GratingStartCpd * Math.Pow(CorridorSetup.GratingEndCpd / CorridorSetup.GratingStartCpd, Math.Max(0, Math.Min(1, fraction)));
        if (trial.frequencyDrift)
        {
            var drift = (double)beep / CorridorSetup.DriftBeeps;
            tone += sweep.StartHz * Math.Pow(sweep.EndHz / sweep.StartHz, drift) - sweep.StartHz;
            grating += CorridorSetup.GratingStartCpd * Math.Pow(CorridorSetup.GratingEndCpd / CorridorSetup.GratingStartCpd, drift) - CorridorSetup.GratingStartCpd;
        }
        var active = trial.truncationFraction >= 1 || Math.Round(sweep.FractionOf(tone), 3) < trial.truncationFraction;
        return new StimulusFrame
        {
            Row = new StimulusStateRow { trialNumber = trial.trialNumber, positionCm = position, toneFrequencyHz = tone, stimulusActive = active, visualSpatialFrequencyCpd = grating },
            Bin = sweep.BinOf(tone),
            ToneGain = active && setup.Audio ? 1 : 0,
            GratingOpacity = active && setup.Visual ? 1 : 0
        };
    }

    bool OnTarget(double hz)
    {
        return hz >= trial.rewardFloorHz && hz <= trial.rewardCeilingHz;
    }

    /// <summary>The outcome (Tom Childs' ProcessTrialEnd): a trial that ends on a lick in the zone or the zone reached
    /// is rewarded; one that runs out of time or track in the zone is penalised, as is one ending outside it on a stage
    /// with penaliseWrongZone. Reaching the zone is the floor alone, as in Tom's FreqAboveThreshold: a drifting stage's
    /// tone can pass the ceiling (and the sweep's end) on the beep it crosses the floor, and is rewarded.</summary>
    CorridorEvent End(string reason)
    {
        var tone = last != null ? last.Row.toneFrequencyHz : 0;
        var onTarget = reason == "FreqReached" || OnTarget(tone);
        var timedOut = reason == "Timeout" || reason == "EndOfTrack";
        var rewarded = onTarget && (reason == "Lick" || reason == "FreqReached");
        var penalised = onTarget ? timedOut : trial.penaliseWrongZone;
        var timeout = penalised ? setup.Task.Penalty.TimeoutSeconds : 0;
        var row = new TrialRow
        {
            trialNumber = trial.trialNumber,
            trialType = trial.trialType,
            outcome = rewarded ? "Correct" : timedOut || !trial.lickToReward ? "NoResponse" : "Incorrect",
            itiStartTime = itiStart,
            itiSeconds = trial.itiSeconds,
            quietWaitSeconds = quietWait,
            trialStartTime = start,
            stimulusOnsetTime = onset,
            outcomeTime = now,
            rewardSizeUl = trial.rewardSizeUl,
            penaltySound = penalised && setup.Task.Penalty.Sound.Enabled,
            penaltyTimeoutSeconds = timeout,
            laserCondition = trial.laserCondition,
            laserPowerMw = triggered ? trial.laserPowerMw : 0,  // a trial ended before its trigger had no laser
            laserTriggerTime = triggered ? laserOnset : double.NaN,
            endReason = reason == "Lick" ? (rewarded ? "CorrectLick" : "WrongLickLimit") : reason,
            trackLengthCm = trial.trackLengthCm,
            endPositionCm = track.Position,
            toneEndFrequencyHz = tone,
            visualEndSpatialFrequencyCpd = last != null ? last.Row.visualSpatialFrequencyCpd : 0,
            truncationFraction = trial.truncationFraction
        };
        var outcome = new CorridorEvent { Kind = CorridorEventKind.Outcome, Seconds = now, Trial = trial, Row = row, Rewarded = rewarded, PenaltySound = row.penaltySound, RewardSide = "left" };
        phase = Phase.Timeout;
        lastOutcome = now;
        waitUntil = now + timeout;
        if (timeout <= 0)
        {
            itiStart = now;
            NextTrial();
        }
        return outcome;
    }
}

[Combinator]
[Description("Checks the task at Go and makes what the trials are drawn from. Input: (task file, TrainingStage, rig.yml, sweep). Stops the workflow on a stage past the table, a reward zone outside the sweep or the optogenetics rules. A real mouse on a rig without hardware triggering is lib-open-rigs' ConnectZapit's refusal.")]
[WorkflowElementCategory(ElementCategory.Transform)]
public class CheckTaskAtGo
{
    public IObservable<CorridorSetup> Process(IObservable<Tuple<UclOpenCoenDomeNavigationTaskLogic, int, LabRig, Sweep>> source)
    {
        return source.Select(x => CorridorSetup.Create(x.Item1.TaskParameters, x.Item2, x.Item4, x.Item3.Zapit.HardwareTriggered, null));
    }
}

[Combinator]
[Description("Runs the trials: (setup, Harp clock in seconds, wheel distance in cm, left lick state, Zapit replies) in, the events the hardware acts on out (CorridorEventKind). Every wait is on the Harp clock.")]
[WorkflowElementCategory(ElementCategory.Combinator)]
public class CorridorTask
{
    public IObservable<CorridorEvent> Process(IObservable<CorridorSetup> setup, IObservable<double> clock, IObservable<double> wheelCm, IObservable<bool> lick, IObservable<ZapitReply> zapitReplies)
    {
        return setup.Take(1).SelectMany(s => Observable.Create<CorridorEvent>(observer =>
        {
            var state = new CorridorState(s);
            var gate = new object();
            Action<Func<IEnumerable<CorridorEvent>>> run = step =>
            {
                lock (gate)
                {
                    List<CorridorEvent> events;
                    try { events = step().ToList(); }
                    catch (Exception error) { observer.OnError(error); return; }
                    foreach (var e in events) observer.OnNext(e);
                }
            };
            return new CompositeDisposable(
                wheelCm.Subscribe(cm => run(() => state.OnWheel(cm)), observer.OnError),
                lick.Subscribe(l => run(() => state.OnLick(l)), observer.OnError),
                zapitReplies.Subscribe(r => run(() => state.OnZapitReply(r)), observer.OnError),
                clock.Subscribe(t => run(() => state.OnClock(t)), observer.OnError));
        }));
    }
}

[Combinator]
[Description("The events of one kind.")]
[WorkflowElementCategory(ElementCategory.Condition)]
public class CorridorEvents
{
    public CorridorEventKind Kind { get; set; }

    public IObservable<CorridorEvent> Process(IObservable<CorridorEvent> source)
    {
        var kind = Kind;
        return source.Where(e => e.Kind == kind);
    }
}

[Combinator]
[Description("A Zapit StopStimulus for each input.")]
[WorkflowElementCategory(ElementCategory.Transform)]
public class StopZapit
{
    public IObservable<ZapitCommand> Process<TSource>(IObservable<TSource> source)
    {
        return source.Select(_ => ZapitCommand.Stop());
    }
}
