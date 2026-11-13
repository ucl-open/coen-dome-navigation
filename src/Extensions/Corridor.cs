using Bonsai;
using Bonsai.Harp;
using Newtonsoft.Json.Linq;
using OpenCV.Net;
using OpenRigs.Devices.Zapit;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using UclOpenCoenDomeNavigation;
using UclOpenCoenDomeNavigationTask;

// The corridor task in tested C# (plan v11, W3): each trial's draw, its interval, its run along the virtual track on
// the Harp clock and its row. main.bonsai drives the hardware from the events CorridorTask gives; tests\Extensions.Tests
// runs the same classes on stand-in clocks.

/// <summary>The sweep the committed tone files were made for (src\waveforms\sweep.json, ucl_open_coen_dome_navigation.waveforms).</summary>
public class Sweep
{
    public double StartHz;
    public double EndHz;
    public int Steps;
    public double PipPeak;

    public static Sweep Load(string path)
    {
        var json = JObject.Parse(File.ReadAllText(path));
        var sweep = new Sweep
        {
            StartHz = (double)json["sweepStartHz"],
            EndHz = (double)json["sweepEndHz"],
            Steps = (int)json["sweepSteps"],
            PipPeak = (double)json["pipPeak"]
        };
        if (!(sweep.StartHz > 0 && sweep.EndHz > sweep.StartHz && sweep.Steps > 1))
        {
            throw new InvalidOperationException(path + " holds no usable sweep: it needs 0 < sweepStartHz < sweepEndHz and sweepSteps above 1. Run `uv run generate-waveforms`.");
        }
        return sweep;
    }

    /// <summary>The frequency a fraction (0 to 1, clamped) of the way along the sweep, on a log scale.</summary>
    public double FrequencyAt(double fraction)
    {
        return StartHz * Math.Pow(EndHz / StartHz, Math.Max(0, Math.Min(1, fraction)));
    }

    /// <summary>How far along the sweep a frequency is, on a log scale.</summary>
    public double FractionOf(double hz)
    {
        return hz <= 0 ? 0 : Math.Log(hz / StartHz) / Math.Log(EndHz / StartHz);
    }

    /// <summary>The tone file's row (0 to Steps - 1) nearest a frequency.</summary>
    public int BinOf(double hz)
    {
        return (int)Math.Max(0, Math.Min(Steps - 1, Math.Round(FractionOf(hz) * (Steps - 1))));
    }
}

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
public class CorridorTrial
{
    public int trialNumber;
    public string trialType;            // the task's modality in words
    public double itiSeconds;           // the interval before this trial, from the last outcome (after any timeout); NaN on the first
    public double quietSeconds;         // the stage's wait without licks after the interval; 0: none
    public double trackLengthCm;
    public double truncationFraction;   // 1: not truncated
    public double rewardFloorHz;
    public double rewardCeilingHz;
    public double rewardSizeUl;
    public bool lickToReward;
    public int wrongLickLimit;
    public bool punishWrongZone;
    public double timeLimitSeconds;     // from the first pip; NaN: none
    public bool trackEndEndsTrial;
    public bool frequencyDrift;
    public int firstPipBeep;            // 1, or laserToStimulusSeconds in beeps in an optogenetics experiment
    public bool optogenetics;
    public bool armZapit;               // hardware-triggered Zapit: armed after the interval, before the trial starts
    public int laserCondition;          // 0 without optogenetics
    public bool laserOn;
    public double laserPowerMw;         // 0 with the laser off
    public double laserMaxSeconds;
    public double triggerHoldSeconds;   // the trigger line stays high this long after the outcome, on every trial
    public int rngSeed;                 // the experiment's seed, for the RngSeed event
}

/// <summary>Everything fixed for the experiment that the trials are drawn from and run on.</summary>
public class CorridorSetup
{
    public const double BeepSeconds = 0.05;       // task.py BEEP_INTERVAL_SEC
    public const double LaserMaxMarginSec = 0.25; // task.py LASER_MAX_MARGIN_SEC
    public const int DriftBeeps = 500;            // a drift stage's frequency reaches the sweep's end on its own after this many beeps (25 s)
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
    public static CorridorSetup Create(UclOpenCoenDomeNavigationTaskParameters task, int stage, Sweep sweep, bool hardwareTriggered, string subjectId, Func<string, int> zapitConditionCount)
    {
        if (stage < 0 || stage >= task.TrainingStages.Count)
        {
            throw new InvalidOperationException(string.Format("TrainingStage is {0}, but the task file's trainingStages has rows 0 to {1}.", stage, task.TrainingStages.Count - 1));
        }
        var setup = new CorridorSetup { Task = task, TrainingStage = stage, Stage = task.TrainingStages[stage], Sweep = sweep, HardwareTriggered = hardwareTriggered };
        var row = setup.Stage;
        var problems = new List<string>();
        if (task.TrialStructure.TrackLengthsCm == null || task.TrialStructure.TrackLengthsCm.Count == 0 || task.TrialStructure.TrackLengthsCm.Any(l => !(l > 0))) problems.Add("trialStructure.trackLengthsCm needs at least one length above 0");
        if (task.TrialStructure.ItiMaxSeconds < task.TrialStructure.ItiMinSeconds) problems.Add("trialStructure.itiMaxSeconds is below itiMinSeconds");
        if (task.Optogenetics.TriggerHoldSeconds >= task.TrialStructure.ItiMinSeconds) problems.Add(string.Format("optogenetics.triggerHoldSeconds ({0} s) must be below trialStructure.itiMinSeconds ({1} s): the trigger line, held that long after every outcome, must fall before the next trial raises it", task.Optogenetics.TriggerHoldSeconds, task.TrialStructure.ItiMinSeconds));
        if (row.TruncatedTrialFraction > 0 && (task.Truncation.SweepFractions == null || task.Truncation.SweepFractions.Count == 0)) problems.Add("stage '" + row.Label + "' truncates trials but truncation.sweepFractions is empty");
        if (!(row.RewardFloorHz > sweep.StartHz && row.RewardFloorHz < row.RewardCeilingHz && row.RewardCeilingHz <= sweep.EndHz)) problems.Add(string.Format("stage '{0}''s reward zone ({1} to {2} Hz) is not inside the sweep ({3} to {4} Hz)", row.Label, row.RewardFloorHz, row.RewardCeilingHz, sweep.StartHz, sweep.EndHz));
        var opto = task.Optogenetics;
        if (opto.UseZapit)
        {
            if (!(opto.LaserPowerMw > 0)) problems.Add("optogenetics.laserPowerMw must be a power in mW above 0 with optogenetics on");
            var beeps = opto.LaserToStimulusSeconds / BeepSeconds;
            if (!(Math.Abs(beeps - Math.Round(beeps)) < 1e-6 && Math.Round(beeps) >= 1 && Math.Round(beeps) <= 4)) problems.Add("optogenetics.laserToStimulusSeconds must be a multiple of 0.05 s from 0.05 to 0.2");
            var limits = task.TrainingStages.Where(s => s.TimeLimitSeconds.HasValue).Select(s => s.TimeLimitSeconds.Value).ToList();
            if (limits.Count > 0 && opto.LaserMaxSeconds < limits.Max() + opto.LaserToStimulusSeconds + LaserMaxMarginSec - 1e-9)
            {
                problems.Add(string.Format("optogenetics.laserMaxSeconds must be at least {0} s (the longest timeLimitSeconds plus laserToStimulusSeconds plus {1} s)", limits.Max() + opto.LaserToStimulusSeconds + LaserMaxMarginSec, LaserMaxMarginSec));
            }
            if (!hardwareTriggered && (subjectId ?? "").Trim().ToLowerInvariant() != "test")
            {
                problems.Add("this rig's Zapit is not hardware-triggered (rig.yml zapit.hardwareTriggered), so only the test mouse may run optogenetics on it, not " + subjectId);
            }
        }
        if (problems.Count > 0) throw new InvalidOperationException("The task cannot be run: " + string.Join("; ", problems) + ".");
        if (opto.UseZapit) setup.ConditionCount = zapitConditionCount != null ? zapitConditionCount(opto.ZapitConfigPath) : ZapitConditions.Check(opto.ZapitConfigPath);
        setup.Seed = task.RngSeed.HasValue ? (int)task.RngSeed.Value : Guid.NewGuid().GetHashCode();
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

    public CorridorTrial Next()
    {
        var task = setup.Task;
        var stage = setup.Stage;
        var t = new CorridorTrial();
        t.trialNumber = ++drawn;
        t.rngSeed = setup.Seed;
        t.trialType = task.Modality.ToString().ToLowerInvariant();
        var iti = task.TrialStructure.ItiMinSeconds + (task.TrialStructure.ItiMaxSeconds - task.TrialStructure.ItiMinSeconds) * random.NextDouble();
        t.itiSeconds = t.trialNumber == 1 ? double.NaN : iti;  // drawn on the first too, so every later draw is as it was
        var lengths = task.TrialStructure.TrackLengthsCm;
        t.trackLengthCm = lengths[random.Next(lengths.Count)] * stage.TrackLengthScale;
        var u = random.NextDouble();
        var fractions = task.Truncation.SweepFractions;
        t.truncationFraction = stage.TruncatedTrialFraction > 0 && u < stage.TruncatedTrialFraction
            ? fractions[(int)Math.Floor(u / stage.TruncatedTrialFraction * fractions.Count)]
            : 1.0;
        t.quietSeconds = stage.QuietSeconds ?? 0;
        t.rewardFloorHz = stage.RewardFloorHz;
        t.rewardCeilingHz = stage.RewardCeilingHz;
        t.rewardSizeUl = stage.RewardSizeUl;
        t.lickToReward = stage.LickToReward;
        t.wrongLickLimit = stage.WrongLickLimit;
        t.punishWrongZone = stage.PunishWrongZone;
        t.timeLimitSeconds = stage.TimeLimitSeconds ?? double.NaN;
        t.trackEndEndsTrial = stage.TrackEndEndsTrial;
        t.frequencyDrift = stage.FrequencyDrift;
        var opto = task.Optogenetics;
        t.optogenetics = opto.UseZapit;
        t.firstPipBeep = 1;
        t.triggerHoldSeconds = opto.TriggerHoldSeconds;  // the trigger line (and the masking light on it) rises on every trial
        if (opto.UseZapit)
        {
            // Every trial sends Zapit a condition; laser-off trials at 0 mW (Pip, decision 11).
            t.firstPipBeep = (int)Math.Round(opto.LaserToStimulusSeconds / CorridorSetup.BeepSeconds);
            t.armZapit = setup.HardwareTriggered;
            t.laserCondition = random.Next(setup.ConditionCount) + 1;
            t.laserOn = t.trialNumber > opto.TrialsBeforeLaser && random.NextDouble() < opto.LaserOnProbability;
            t.laserPowerMw = t.laserOn ? opto.LaserPowerMw.Value : 0;
            t.laserMaxSeconds = opto.LaserMaxSeconds;
        }
        return t;
    }
}

public enum CorridorEventKind
{
    Arm,        // send the trial's StartStimulus to a hardware-triggered Zapit; the trial waits for its reply
    Start,      // beep 0: the trial starts (trigger line up, a software-triggered Zapit's StartStimulus)
    Pip,        // a beep with the stimulus on: the tone pip and grating frame
    Outcome,    // the trial ended: its row, the reward or punishment
    LaserCap    // laserMaxSeconds after the start with no outcome yet: stop the laser
}

/// <summary>One thing the task asks the hardware to do, at a Harp time.</summary>
public class CorridorEvent
{
    public CorridorEventKind Kind;
    public double Seconds;
    public CorridorTrial Trial;
    public StimulusFrame Frame;   // Pip
    public TrialRow Row;          // Outcome
    public bool Rewarded;         // Outcome
    public bool ErrorSound;       // Outcome: the punishment's sound plays
    public string RewardSide;     // Outcome: "left", the spout the task rewards

    public Tuple<string, double> Reward { get { return Tuple.Create(RewardSide, Trial.rewardSizeUl); } }
}

/// <summary>A beep's stimulus: its BonsaiData_StimulusState row and what the mixer and grating play.</summary>
public class StimulusFrame
{
    public StimulusStateRow Row;
    public int Bin;              // the tone file's row
    public double ToneGain;      // 1, or 0 when truncated or not auditory
    public double GratingOpacity; // 1, or 0 when truncated or not visual
}

/// <summary>A BonsaiData_StimulusState row, one per pip while a trial runs.</summary>
public class StimulusStateRow
{
    public int trialNumber;
    public double positionCm;
    public double toneFrequencyHz;
    public int stimulusActive;
    public double visualSpatialFrequencyCpd;
}

/// <summary>A BonsaiData_Trial row; fields in schemas\trial-columns.yml's order, after Seconds.</summary>
public class TrialRow
{
    public int trialNumber;
    public string trialType;
    public string outcome;
    public string endReason;
    public bool punishSound;
    public double punishTimeoutSeconds;
    public double trackLengthCm;
    public double endPositionCm;
    public double toneEndFrequencyHz;
    public double visualEndSpatialFrequencyCpd;
    public double truncationFraction;
    public double itiStartTime;
    public double itiSeconds;
    public double trialStartTime;
    public double stimulusOnsetTime;
    public double rewardSizeUl;
    public double laserPowerMw;
    public int laserCondition;
}

/// <summary>
/// The task's state from one trial to the next, driven by the Harp clock, the wheel, the left lick detector and
/// Zapit's replies; every wait is on the Harp clock. A trial: its interval (itiSeconds from the last outcome, after any
/// timeout), the quiet wait, Zapit's arming, then beeps every 50 ms from beep 0 with pips from firstPipBeep, until the
/// outcome; then the punishment's timeout, and the next trial's interval.
/// </summary>
public class CorridorState
{
    enum Phase { Waiting, Interval, Quiet, Arming, Running, Timeout }

    readonly CorridorSetup setup;
    readonly TrialDraw draw;
    Phase phase = Phase.Waiting;
    CorridorTrial trial;
    VirtualTrack track;
    double now = double.NaN, wheel, itiStart = double.NaN, waitUntil, lastLick, start, onset;
    int nextBeep, wrongLicks;
    bool capped;
    StimulusFrame last;

    public CorridorState(CorridorSetup setup)
    {
        this.setup = setup;
        draw = new TrialDraw(setup);
    }

    public CorridorTrial Trial { get { return trial; } }

    public void Wheel(double cm)
    {
        wheel = cm;
    }

    /// <summary>The wheel moved: the track's position, and the trial's end at the end of the track.</summary>
    public IEnumerable<CorridorEvent> OnWheel(double cm)
    {
        wheel = cm;
        if (phase != Phase.Running) yield break;
        track.Update(cm);
        if (trial.trackEndEndsTrial && track.AtEnd) yield return End("EndOfTrack");
    }

    public IEnumerable<CorridorEvent> OnLick(bool lick)
    {
        if (!lick) yield break;
        if (phase == Phase.Quiet) lastLick = now;
        if (phase != Phase.Running || last == null || !trial.lickToReward) yield break;  // licks count from the first pip
        if (OnTarget(last.Row.toneFrequencyHz)) { yield return End("Lick"); yield break; }
        wrongLicks++;
        if (trial.wrongLickLimit > 0 && wrongLicks >= trial.wrongLickLimit) yield return End("WrongLickLimit");
    }

    /// <summary>Zapit's reply while a trial waits to be armed: it must have armed the trial's condition as asked.</summary>
    public IEnumerable<CorridorEvent> OnZapitReply(ZapitReply reply)
    {
        if (phase != Phase.Arming || reply.Command != ZapitCommandType.StartStimulus) yield break;
        int laserOn = trial.laserOn ? 1 : 0;
        if (reply.IsError || reply.Value != trial.laserCondition || reply.LaserOn != laserOn)
        {
            throw new InvalidOperationException(string.Format(
                "Zapit answered trial {0}'s StartStimulus (condition {1}, laserOn {2}) with {3}. Is the conditions file the task names loaded, and is Zapit calibrated?",
                trial.trialNumber, trial.laserCondition, laserOn, reply));
        }
        foreach (var e in Begin()) yield return e;
    }

    public IEnumerable<CorridorEvent> OnClock(double seconds)
    {
        if (!double.IsNaN(now) && seconds < now) throw new InvalidOperationException(string.Format("The Harp clock went backwards, from {0} s to {1} s.", now, seconds));
        now = seconds;
        var events = new List<CorridorEvent>();
        if (phase == Phase.Waiting) { NextTrial(); }
        if (phase == Phase.Timeout && now >= waitUntil - 1e-9) { itiStart = now; NextTrial(); }
        if (phase == Phase.Interval && now >= waitUntil - 1e-9)
        {
            phase = Phase.Quiet;
            lastLick = now;
        }
        if (phase == Phase.Quiet && now >= lastLick + trial.quietSeconds - 1e-9)
        {
            if (trial.armZapit)
            {
                phase = Phase.Arming;
                events.Add(new CorridorEvent { Kind = CorridorEventKind.Arm, Seconds = now, Trial = trial });
            }
            else events.AddRange(Begin());
        }
        if (phase == Phase.Running) events.AddRange(Run());
        return events;
    }

    void NextTrial()
    {
        trial = draw.Next();
        phase = Phase.Interval;
        waitUntil = double.IsNaN(trial.itiSeconds) ? now : itiStart + trial.itiSeconds;
    }

    IEnumerable<CorridorEvent> Begin()
    {
        phase = Phase.Running;
        start = now;
        onset = double.NaN;
        nextBeep = 0;
        wrongLicks = 0;
        capped = false;
        last = null;
        track = new VirtualTrack(trial.trackLengthCm, wheel);
        track.Update(wheel);
        return new List<CorridorEvent> { new CorridorEvent { Kind = CorridorEventKind.Start, Seconds = now, Trial = trial } }.Concat(Run());
    }

    List<CorridorEvent> Run()
    {
        var events = new List<CorridorEvent>();
        // Only the latest beep due: after a gap in the Harp clock the missed pips are not played at once.
        var due = (int)Math.Floor((now - start + 1e-9) / CorridorSetup.BeepSeconds);
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
        if (phase == Phase.Running && trial.optogenetics && !capped && now >= start + trial.laserMaxSeconds - 1e-9)
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
            Row = new StimulusStateRow { trialNumber = trial.trialNumber, positionCm = position, toneFrequencyHz = tone, stimulusActive = active ? 1 : 0, visualSpatialFrequencyCpd = grating },
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
    /// is rewarded; one that runs out of time or track in the zone is punished, as is one ending outside it on a stage
    /// with punishWrongZone. Reaching the zone is the floor alone, as in Tom's FreqAboveThreshold: a drifting stage's
    /// tone can pass the ceiling (and the sweep's end) on the beep it crosses the floor, and is rewarded.</summary>
    CorridorEvent End(string reason)
    {
        var tone = last != null ? last.Row.toneFrequencyHz : 0;
        var onTarget = reason == "FreqReached" || OnTarget(tone);
        var timedOut = reason == "Timeout" || reason == "EndOfTrack";
        var rewarded = onTarget && (reason == "Lick" || reason == "FreqReached");
        var punished = onTarget ? timedOut : trial.punishWrongZone;
        var timeout = punished ? setup.Task.Punishment.TimeoutSeconds : 0;
        var row = new TrialRow
        {
            trialNumber = trial.trialNumber,
            trialType = trial.trialType,
            outcome = rewarded ? "Correct" : timedOut || !trial.lickToReward ? "NoResponse" : "Incorrect",
            endReason = reason == "Lick" ? (rewarded ? "CorrectLick" : "WrongLickLimit") : reason,
            punishSound = punished && setup.Task.Punishment.Sound.Enabled,
            punishTimeoutSeconds = timeout,
            trackLengthCm = trial.trackLengthCm,
            endPositionCm = track.Position,
            toneEndFrequencyHz = tone,
            visualEndSpatialFrequencyCpd = last != null ? last.Row.visualSpatialFrequencyCpd : 0,
            truncationFraction = trial.truncationFraction,
            itiStartTime = trial.trialNumber == 1 ? double.NaN : itiStart,
            itiSeconds = trial.itiSeconds,
            trialStartTime = start,
            stimulusOnsetTime = onset,
            rewardSizeUl = trial.rewardSizeUl,
            laserPowerMw = trial.laserPowerMw,
            laserCondition = trial.laserCondition
        };
        var outcome = new CorridorEvent { Kind = CorridorEventKind.Outcome, Seconds = now, Trial = trial, Row = row, Rewarded = rewarded, ErrorSound = row.punishSound, RewardSide = "left" };
        phase = Phase.Timeout;
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
[Description("Checks the task at Go and makes what the trials are drawn from. Input: (task file, TrainingStage, rig.yml, SubjectId). Stops the workflow on a stage past the table, a reward zone outside the sweep, the optogenetics rules, or optogenetics on a software-triggered rig with a real mouse.")]
[WorkflowElementCategory(ElementCategory.Transform)]
public class CreateCorridorSetup
{
    public CreateCorridorSetup()
    {
        SweepFile = @"waveforms\sweep.json";
    }

    [Description("sweep.json, relative to the workflow's folder (src).")]
    public string SweepFile { get; set; }

    public IObservable<CorridorSetup> Process(IObservable<Tuple<UclOpenCoenDomeNavigationTaskLogic, int, DomeRig, string>> source)
    {
        return source.Select(x => CorridorSetup.Create(x.Item1.TaskParameters, x.Item2, Sweep.Load(SweepFile), x.Item3.Zapit.HardwareTriggered, x.Item4, null));
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
[Description("An Outcome event's trial row, stamped with the Harp time the outcome was known, for LogTrial.")]
[WorkflowElementCategory(ElementCategory.Transform)]
public class CreateTrialRow
{
    public IObservable<Timestamped<TrialRow>> Process(IObservable<CorridorEvent> source)
    {
        return source.Where(e => e.Kind == CorridorEventKind.Outcome).Select(e => Timestamped.Create(e.Row, e.Seconds));
    }
}

[Combinator]
[Description("A Pip event's BonsaiData_StimulusState row, stamped with its Harp time.")]
[WorkflowElementCategory(ElementCategory.Transform)]
public class CreateStimulusRow
{
    public IObservable<Timestamped<StimulusStateRow>> Process(IObservable<CorridorEvent> source)
    {
        return source.Where(e => e.Kind == CorridorEventKind.Pip).Select(e => Timestamped.Create(e.Frame.Row, e.Seconds));
    }
}

[Combinator]
[Description("A trial's Zapit StartStimulus: its condition, the laser on or off (galvos and masking light either way, 0 mW off), continuous: Bonsai stops it at the outcome or the cap.")]
[WorkflowElementCategory(ElementCategory.Transform)]
public class ZapitTrialCommand
{
    [Description("Whether the Zapit DAQ waits for the ZapitTrigger line (rig.yml's zapit.hardwareTriggered).")]
    public bool HardwareTriggered { get; set; }

    public IObservable<ZapitCommand> Process(IObservable<CorridorEvent> source)
    {
        var hardware = HardwareTriggered;
        return source.Select(e => Build(e.Trial, hardware));
    }

    public static ZapitCommand Build(CorridorTrial trial, bool hardwareTriggered)
    {
        if (trial.laserCondition < 1) throw new InvalidOperationException("Trial " + trial.trialNumber + " has no Zapit condition.");
        return new ZapitCommand
        {
            Command = ZapitCommandType.StartStimulus,
            ConditionNumber = trial.laserCondition,
            LaserOn = trial.laserOn,
            HardwareTriggered = hardwareTriggered,
            Logging = true,
            StimDuration = 0,
            LaserPower = (float)trial.laserPowerMw,
            StartDelay = 0
        };
    }
}

[Combinator]
[Description("A Behavior board write that sets (Set true) or clears the input line, such as ZapitTriggerLine.")]
[WorkflowElementCategory(ElementCategory.Transform)]
public class LineMessage
{
    [Description("True sets the line high, false clears it.")]
    public bool Set { get; set; }

    public IObservable<HarpMessage> Process(IObservable<Harp.Behavior.DigitalOutputs> source)
    {
        var set = Set;
        return source.Select(line => set
            ? Harp.Behavior.OutputSet.FromPayload(MessageType.Write, line)
            : Harp.Behavior.OutputClear.FromPayload(MessageType.Write, line));
    }
}

[Combinator]
[Description("A pip's buffer for the mixer: the tone file's row for the pip's bin (input: the Pip event and the tone rows), silent when the pip's ToneGain is 0.")]
[WorkflowElementCategory(ElementCategory.Transform)]
public class PipBuffer
{
    public IObservable<Mat> Process(IObservable<Tuple<CorridorEvent, IList<Mat>>> source)
    {
        return source.Select(x =>
        {
            var tone = x.Item2[x.Item1.Frame.Bin];
            if (x.Item1.Frame.ToneGain > 0) return tone;
            var silent = new Mat(tone.Size, tone.Depth, tone.Channels);
            silent.SetZero();
            return silent;
        });
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
