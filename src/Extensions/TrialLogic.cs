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
    public string trialType;            // its condition's label: names it, drives nothing
    public int trialCondition;          // the 0-based index of its condition in the task's trialConditions
    public double visualContrast;       // the grating's contrast, drawn from its condition's contrasts; 0: no grating
    public double auditoryAmplitude;    // the pips' amplitude, its condition's auditoryStimulus.amplitude; 0: no tone
    public int speakerChannel;          // the one speaker's mixer channel; AllSpeakersChannel on every speaker (initialAzimuthsDeg "all"), -1 with no tone
    public double auditoryAzimuthDeg;   // that speaker's position (rig.yml); NaN on every speaker or with no tone
    public double auditoryElevationDeg;
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
    public double rewardOfferedUl;     // its condition's reward.rewardSizeUl: what a reward on this trial gives
    public bool autoReward;             // drawn with its condition's autoRewardProbability: rewarded on reaching the floor, licks ignored
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

    /// <summary>speakerChannel on a trial whose pips play on every rig.yml speaker (auditoryStimulus.initialAzimuthsDeg "all"),
    /// as coen-dome-decision-making's.</summary>
    public const int AllSpeakersChannel = -2;
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

    public TrialRules(UclOpenCoenDomeNavigationTaskParameters task, Sweep sweep, bool hardwareTriggered, Func<string, int> zapitConditionCount, EqualisedSpeakerArray speakers = null)
    {
        this.task = task;
        this.sweep = sweep;
        draw = new TrialDraw(task, sweep, zapitConditionCount, speakers);
        var opto = task.Optogenetics;
        // Every StartStimulus continuous: the laser on from the trigger until the outcome or LaserCap, the trigger line
        // (and the masking light on it) held triggerHoldSeconds past the outcome, the next trial armed once it falls.
        laser = opto.Enabled ? LaserTiming.UntilOutcome(hardwareTriggered, opto.TriggerHoldSeconds, opto.LaserMaxSeconds) : null;
    }

    /// <summary>The rules for a task file with the sweep, Zapit's hardware triggering and rig.yml's speakerArray (null: none,
    /// so only initialAzimuthsDeg "all"); throws naming every problem (TrialDraw.CheckTask).</summary>
    public static TrialRules Create(UclOpenCoenDomeNavigationTaskParameters task, Sweep sweep, bool hardwareTriggered, Func<string, int> zapitConditionCount, EqualisedSpeakerArray speakers = null)
    {
        return new TrialRules(task, sweep, hardwareTriggered, zapitConditionCount, speakers);
    }

    /// <summary>The stage as code-version (S03A-k7f2) from the task file's curriculum, which the check has found well formed.</summary>
    public string CurriculumStage { get { return task.Curriculum.Code + "-" + task.Curriculum.Version; } }

    /// <summary>The loudest tone the task's conditions can draw (TrialDraw.Loudest), for the pips' peak check (LayOutPips).</summary>
    public LoudestSound Loudest { get { return draw.Loudest(); } }

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
        // The tone switched off plays at amplitude 0, logged so in each pip's row.
        var auditoryOff = off && trial.auditoryOff;
        var visualOn = !(off && trial.visualOff);
        var contrast = visualOn ? trial.visualContrast : 0;
        if (auditoryOff && double.IsNaN(auditoryOffPositionCm)) auditoryOffPositionCm = position;
        if (!visualOn && double.IsNaN(visualOffPositionCm)) visualOffPositionCm = position;
        var gain = auditoryOff ? 0 : trial.auditoryAmplitude;
        return new StimulusFrame
        {
            Row = new StimulusStateRow { trialNumber = trial.trialNumber, positionCm = position, auditoryFrequencyHz = tone, auditoryAmplitude = gain, visualSpatialFrequencyCpd = grating, visualContrast = contrast },
            Bin = sweep.BinOf(tone),
            ToneGain = gain,
            SpeakerChannel = trial.speakerChannel,
            GratingOpacity = contrast > 0 ? 1 : 0,
            GratingContrast = trial.visualContrast
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
            autoRewarded = trial.autoReward && rewarded,
            visualContrast = trial.visualContrast,
            auditoryAmplitude = trial.auditoryAmplitude,
            auditoryAzimuthDeg = trial.auditoryAzimuthDeg,
            auditoryElevationDeg = trial.auditoryElevationDeg
        }, trial, times, trial.rewardOfferedUl, rewarded, penalised, task.Penalty.TimeoutSeconds, task.Penalty.Sound.Enabled);
        return new TrialOutcome(row);
    }
}

/// <summary>One of the task's trialConditions as built at Go (TrialDraw.BuildConditions).</summary>
public class TrialConditionDraw
{
    /// <summary>The condition's label, its trials' trialType.</summary>
    public string Label;
    /// <summary>Its relative draw weight.</summary>
    public int Weight;
    /// <summary>Where it is, for a message: trialConditions[index] (label).</summary>
    public string Where;
    /// <summary>What its trials draw, one per combination of its set's and pairs' picks, picked uniformly per trial.</summary>
    public List<TrialConditionCombination> Combinations;
}

/// <summary>One combination of a condition's picks: the condition fields' values over the task's.</summary>
public class TrialConditionCombination
{
    /// <summary>The grating contrasts, one drawn per trial (0: no grating).</summary>
    public List<double> Contrasts;
    /// <summary>The pips' amplitude (0: no tone).</summary>
    public double Amplitude;
    /// <summary>Where the pips play, one drawn per trial: a number (rig.yml's speaker nearest it) or "all" (every speaker).</summary>
    public List<object> InitialAzimuths;
    /// <summary>A reward's size, ul.</summary>
    public double RewardSizeUl;
    /// <summary>The chance a trial is an auto-reward trial.</summary>
    public double AutoRewardProbability;
    /// <summary>The chance a trial past trialsBeforeLaser is laser-on, with optogenetics on.</summary>
    public double LaserOnProbability;
    /// <summary>The picks that make it, for a message ("" with no list cell).</summary>
    public string Picks;
}

/// <summary>The largest amplitude a task's conditions can draw, and where (a condition and its picks).</summary>
public class LoudestSound
{
    /// <summary>The amplitude: 1 the pips as stored.</summary>
    public double Amplitude;
    /// <summary>The condition that draws it, for a message.</summary>
    public string Where;
}

/// <summary>One of the rules linking settings (task.py RULES, the schema's x-rules): when the value at the path `when` is
/// `is` (a list: every entry is), each `then` path must be its value, each `oneOf` path one of its values, and the
/// `unused` paths are not read. Named as the schema's keys.</summary>
public class ConditionRule
{
    /// <summary>The dotted path the rule looks at.</summary>
    public string when;
    /// <summary>The value at `when` that brings the rule in.</summary>
    public object @is;
    /// <summary>Paths and the value each must then have.</summary>
    public Dictionary<string, object> then;
    /// <summary>Paths and the values each may then have.</summary>
    public Dictionary<string, string[]> oneOf;
    /// <summary>Paths then not read.</summary>
    public string[] unused;
}

/// <summary>Draws every trial of an experiment from one random stream seeded with the task's rngSeed.</summary>
public class TrialDraw
{
    /// <summary>The fields a condition may set: task.py's CONDITION_FIELDS, the schema's x-conditionFields (a test holds them
    /// equal). Only what Next alone reads per trial.</summary>
    public static readonly string[] ConditionFields =
    {
        "contrasts", "auditoryStimulus.amplitude", "auditoryStimulus.initialAzimuthsDeg", "optogenetics.laserOnProbability", "reward.rewardSizeUl", "autoRewardProbability"
    };

    /// <summary>An auditoryStimulus.initialAzimuthsDeg item that plays the pips on every rig.yml speaker (task.py ALL_SPEAKERS).</summary>
    public const string AllSpeakers = "all";

    /// <summary>The rules linking settings: task.py's RULES, the schema's x-rules (a test holds them equal), checked on the
    /// task and every combination of every condition. No corridor setting limits another: they say only what goes unread.</summary>
    public static readonly ConditionRule[] Rules =
    {
        new ConditionRule { when = "contrasts", @is = 0, unused = new[] { "visualStimulus.spatialFrequencyStartCpd", "visualStimulus.spatialFrequencyEndCpd" } },
        new ConditionRule { when = "auditoryStimulus.amplitude", @is = 0, unused = new[] { "auditoryStimulus.initialAzimuthsDeg" } },
        new ConditionRule { when = "autoRewardProbability", @is = 1, unused = new[] { "wrongLickLimit", "rewardCeilingHz" } },
        new ConditionRule { when = "stimulusOffProbability", @is = 0, unused = new[] { "stimulusOff.trackFractions", "stimulusOff.stimuli" } },
    };

    /// <summary>The most combinations one condition may expand into (task.py MAX_COMBINATIONS).</summary>
    public const int MaxCombinations = 64;
    /// <summary>The most a condition's weight may be (task.py's TrialCondition.weight le), so the weights' sum stays an int.</summary>
    public const int MaxWeight = 1000000;

    /// <summary>An enum value as the task file writes it (its EnumMember name: audiovisualCoherent).</summary>
    public static string Name(Enum value)
    {
        var field = value.GetType().GetField(value.ToString());
        var member = field == null ? null : (System.Runtime.Serialization.EnumMemberAttribute)Attribute.GetCustomAttribute(field, typeof(System.Runtime.Serialization.EnumMemberAttribute));
        return member != null ? member.Value : value.ToString();
    }

    /// <summary>The task file's text read as Bonsai's DeserializeFromYaml reads it, except that a trial condition's set (typed
    /// object) keeps its YAML types, numbers and bools, where DeserializeFromYaml reads them as text; so Settings_Task logs them
    /// so (coen-dome-decision-making's ReadTask).</summary>
    public static UclOpenCoenDomeNavigationTaskLogic Read(string text)
    {
        var deserializer = new YamlDotNet.Serialization.DeserializerBuilder()
            .WithTypeConverter(new YamlDotNet.Serialization.Converters.DateTimeOffsetConverter())
            .WithAttemptingUnquotedStringTypeDeserialization()
            .Build();
        return deserializer.Deserialize<UclOpenCoenDomeNavigationTaskLogic>(new YamlDotNet.Core.MergingParser(new YamlDotNet.Core.Parser(new StringReader(text))));
    }

    /// <summary>Each trial condition as drawn (task.py condition_combinations): its set's values over the task's, once for every
    /// combination: each pairs group's lists (the set's, else the task's) zipped, equally long; each other list cell one pick
    /// (a scalar's list one value, a list field's entry a one-entry list); all combined as a product, at most
    /// MaxCombinations. Adds a problem for each condition that cannot be built, and returns null then.</summary>
    public static List<TrialConditionDraw> BuildConditions(UclOpenCoenDomeNavigationTaskParameters task, List<string> problems)
    {
        var typed = task.TrialConditions ?? new List<TrialCondition>();
        if (typed.Count == 0) { problems.Add("trialConditions needs a condition"); return null; }
        if (typed.Sum(c => (long)c.Weight) > int.MaxValue)
        {
            problems.Add(string.Format("trialConditions' weights add to more than {0}, the most the draw can sum", int.MaxValue));
            return null;
        }
        var built = new List<TrialConditionDraw>();
        for (int i = 0; i < typed.Count; i++)
        {
            var c = typed[i];
            var where = string.Format("trialConditions[{0}] ({1})", i, Name(c.Label));
            var before = problems.Count;
            if (c.Weight < 0) problems.Add(where + ": its weight is below 0");
            if (c.Weight > MaxWeight) problems.Add(string.Format("{0}: its weight is above {1}", where, MaxWeight));
            var values = new Dictionary<string, object>();
            var set = c.Set as System.Collections.IDictionary;
            if (set != null) foreach (System.Collections.DictionaryEntry cell in set) values[Convert.ToString(cell.Key, System.Globalization.CultureInfo.InvariantCulture)] = cell.Value;
            else if (c.Set != null) problems.Add(where + ": its set is not a map");
            var pairs = c.Pairs ?? new List<List<string>>();
            var paired = pairs.SelectMany(g => g ?? new List<string>()).ToList();
            foreach (var path in values.Keys.Concat(paired))
            {
                if (!ConditionFields.Contains(path)) problems.Add(string.Format("{0} sets or pairs {1}, which is not a condition field (x-conditionFields)", where, path));
                else if (path.StartsWith("optogenetics.") && !task.Optogenetics.Enabled) problems.Add(string.Format("{0} sets {1} with optogenetics off", where, path));
            }
            if (paired.Distinct().Count() < paired.Count || pairs.Any(g => g == null || g.Count < 2)) problems.Add(where + ": each pairs group needs two paths or more, each path in one group");
            if (problems.Count > before) continue;
            // Each cell: its paths (a pairs group's, or one set path) and its picks, one value per path.
            var cells = new List<KeyValuePair<string[], List<object[]>>>();
            foreach (var group in pairs)
            {
                var lists = group.Select(path => Options(task, values, path, where, problems)).ToList();
                if (lists.Any(l => l == null)) continue;
                if (lists.Select(l => l.Count).Distinct().Count() > 1)
                {
                    problems.Add(string.Format("{0}: pairs [{1}] have lists of lengths {2}; paired lists must be equally long", where, string.Join(", ", group), string.Join(", ", lists.Select(l => l.Count))));
                    continue;
                }
                cells.Add(new KeyValuePair<string[], List<object[]>>(group.ToArray(), Enumerable.Range(0, lists[0].Count).Select(k => lists.Select(l => l[k]).ToArray()).ToList()));
            }
            foreach (var path in values.Keys.Where(p => !paired.Contains(p)))
            {
                var options = Options(task, values, path, where, problems);
                if (options != null) cells.Add(new KeyValuePair<string[], List<object[]>>(new[] { path }, options.Select(o => new[] { o }).ToList()));
            }
            foreach (var cell in cells.Where(cell => cell.Value.Count == 0)) problems.Add(string.Format("{0}: {1} is an empty list", where, string.Join(", ", cell.Key)));
            var count = cells.Aggregate(1L, (n, cell) => n * cell.Value.Count);
            if (count > MaxCombinations) problems.Add(string.Format("{0} expands into {1} combinations, more than {2}", where, count, MaxCombinations));
            if (problems.Count > before) continue;
            var draw = new TrialConditionDraw { Label = Name(c.Label), Weight = c.Weight, Where = where, Combinations = new List<TrialConditionCombination>() };
            for (int n = 0; n < count; n++)
            {
                var combination = Base(task);
                var named = new List<string>();
                int rest = n;
                foreach (var cell in cells)
                {
                    var picked = cell.Value[rest % cell.Value.Count];
                    rest /= cell.Value.Count;
                    for (int k = 0; k < cell.Key.Length; k++)
                    {
                        Put(combination, cell.Key[k], picked[k]);
                        if (cell.Value.Count > 1) named.Add(cell.Key[k] + " " + Show(picked[k]));
                    }
                }
                combination.Picks = string.Join(", ", named);
                draw.Combinations.Add(combination);
            }
            built.Add(draw);
        }
        if (problems.Count == 0 && built.Sum(c => (long)c.Weight) <= 0) problems.Add("trialConditions needs a condition with a weight above 0");
        return problems.Count == 0 ? built : null;
    }

    // The list fields: a cell picks one entry, a one-entry list.
    static bool IsList(string path) { return path == "contrasts" || path == "auditoryStimulus.initialAzimuthsDeg"; }

    /// <summary>The task's own values of the condition fields: a combination with no picks (initialAzimuthsDeg's items as
    /// numbers or "all", however the YAML reader typed them).</summary>
    public static TrialConditionCombination Base(UclOpenCoenDomeNavigationTaskParameters task)
    {
        var azimuths = task.AuditoryStimulus.InitialAzimuthsDeg ?? new List<object>();
        return new TrialConditionCombination
        {
            Contrasts = task.Contrasts,
            Amplitude = task.AuditoryStimulus.Amplitude,
            InitialAzimuths = azimuths.Select(a => Azimuth(a) ?? a).ToList(),
            RewardSizeUl = task.Reward.RewardSizeUl,
            AutoRewardProbability = task.AutoRewardProbability,
            LaserOnProbability = task.Optogenetics.LaserOnProbability,
            Picks = ""
        };
    }

    // A initialAzimuthsDeg item as the draw reads it: a number, or "all"; null when it is neither.
    static object Azimuth(object value)
    {
        double number;
        if (Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) == AllSpeakers) return AllSpeakers;
        return Number(value, out number) ? (object)number : null;
    }

    static object Get(TrialConditionCombination combination, string path)
    {
        switch (path)
        {
            case "contrasts": return combination.Contrasts.Cast<object>().ToList();
            case "auditoryStimulus.amplitude": return combination.Amplitude;
            case "auditoryStimulus.initialAzimuthsDeg": return combination.InitialAzimuths;
            case "reward.rewardSizeUl": return combination.RewardSizeUl;
            case "autoRewardProbability": return combination.AutoRewardProbability;
            case "optogenetics.laserOnProbability": return combination.LaserOnProbability;
            default: throw new ArgumentException(path + " is not a condition field.");
        }
    }

    static void Put(TrialConditionCombination combination, string path, object value)
    {
        switch (path)
        {
            case "contrasts": combination.Contrasts = ((List<object>)value).Cast<double>().ToList(); break;
            case "auditoryStimulus.amplitude": combination.Amplitude = (double)value; break;
            case "auditoryStimulus.initialAzimuthsDeg": combination.InitialAzimuths = (List<object>)value; break;
            case "reward.rewardSizeUl": combination.RewardSizeUl = (double)value; break;
            case "autoRewardProbability": combination.AutoRewardProbability = (double)value; break;
            case "optogenetics.laserOnProbability": combination.LaserOnProbability = (double)value; break;
            default: throw new ArgumentException(path + " is not a condition field.");
        }
    }

    // A cell's picks (task.py _options): a list field's entries, each a one-entry list; a scalar's list, else its one value.
    // The value is the set's, else the task's; null, with a problem, when a value is not a number (initialAzimuthsDeg: or
    // "all").
    static List<object> Options(UclOpenCoenDomeNavigationTaskParameters task, Dictionary<string, object> values, string path, string where, List<string> problems)
    {
        object value;
        var listField = IsList(path);
        var azimuths = path == "auditoryStimulus.initialAzimuthsDeg";
        if (!values.TryGetValue(path, out value)) value = Get(Base(task), path);
        var list = value as System.Collections.IList;
        if (listField && list == null) { problems.Add(string.Format("{0}: {1} must be a list", where, path)); return null; }
        var entries = list != null ? list.Cast<object>().ToList() : new List<object> { value };
        var picks = new List<object>();
        foreach (var entry in entries)
        {
            double number;
            var pick = azimuths ? Azimuth(entry) : Number(entry, out number) ? (object)number : null;
            if (pick == null) { problems.Add(string.Format("{0}: {1} value '{2}' is not a number{3}", where, path, entry, azimuths ? " or all" : "")); return null; }
            picks.Add(listField ? (object)new List<object> { pick } : pick);
        }
        return picks;
    }

    static bool Number(object value, out double number)
    {
        number = double.NaN;
        if (value == null || value is bool) return false;
        return double.TryParse(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out number);
    }

    static string Show(object value)
    {
        var list = value as List<object>;
        return list != null ? "[" + string.Join(", ", list.Select(v => Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture))) + "]" : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    // The value at a path the rules name, as the rules compare it: a number, or contrasts' list.
    static object Value(UclOpenCoenDomeNavigationTaskParameters task, TrialConditionCombination combination, string path)
    {
        switch (path)
        {
            case "contrasts": return combination != null ? combination.Contrasts : task.Contrasts;
            case "auditoryStimulus.amplitude": return combination != null ? combination.Amplitude : task.AuditoryStimulus.Amplitude;
            case "autoRewardProbability": return combination != null ? combination.AutoRewardProbability : task.AutoRewardProbability;
            case "stimulusOffProbability": return task.StimulusOffProbability;
            case "wrongLickLimit": return (double)task.WrongLickLimit;
            default: throw new ArgumentException("x-rules names " + path + ", which TrialDraw.Value does not read.");
        }
    }

    static bool Same(object value, object expected)
    {
        if (value is double) return expected is string ? false : (double)value == Convert.ToDouble(expected, System.Globalization.CultureInfo.InvariantCulture);
        return Equals(value, expected);
    }

    // A value is a rule's `is`: a list when every entry is.
    static bool Is(object value, object expected)
    {
        var list = value as List<double>;
        if (list != null) return list.Count > 0 && list.All(entry => Same(entry, expected));
        return Same(value, expected);
    }

    /// <summary>What breaks the rules in the task, or in one combination of a condition (task.py rule_problems).</summary>
    public static IEnumerable<string> RuleProblems(UclOpenCoenDomeNavigationTaskParameters task, TrialConditionCombination combination = null, IEnumerable<ConditionRule> rules = null)
    {
        foreach (var rule in rules ?? Rules)
        {
            if (!Is(Value(task, combination, rule.when), rule.@is)) continue;
            foreach (var then in rule.then ?? new Dictionary<string, object>())
            {
                if (!Same(Value(task, combination, then.Key), then.Value)) yield return string.Format("{0} {1} needs {2} {3} (x-rules)", rule.when, rule.@is, then.Key, then.Value);
            }
            foreach (var oneOf in rule.oneOf ?? new Dictionary<string, string[]>())
            {
                if (!oneOf.Value.Contains(Value(task, combination, oneOf.Key) as string)) yield return string.Format("{0} {1} needs {2} one of {3} (x-rules)", rule.when, rule.@is, oneOf.Key, string.Join(", ", oneOf.Value));
            }
        }
    }

    // What the condition fields' values must be (task.py's bounds), on the task or one combination; an azimuth needs rig.yml's
    // speakers.
    static IEnumerable<string> ConditionProblems(TrialConditionCombination c, EqualisedSpeakerArray speakers)
    {
        if (c.Contrasts == null || c.Contrasts.Count == 0 || c.Contrasts.Any(v => !(v >= 0) || double.IsInfinity(v))) yield return "contrasts must each be a finite number, 0 or more, at least one";
        if (!(c.Amplitude >= 0) || double.IsInfinity(c.Amplitude)) yield return "auditoryStimulus.amplitude must be a finite number, 0 or more";
        if (c.InitialAzimuths == null || c.InitialAzimuths.Count == 0 || c.InitialAzimuths.Any(a => !(a as string == AllSpeakers || a is double && (double)a >= -180 && (double)a <= 180)))
            yield return "auditoryStimulus.initialAzimuthsDeg must each be all or a number in [-180, 180], at least one";
        else if (c.Amplitude > 0 && c.InitialAzimuths.Any(a => a is double) && (speakers == null || speakers.Speakers == null || speakers.Speakers.Count == 0))
            yield return "auditoryStimulus.initialAzimuthsDeg names an azimuth but rig.yml has no speakerArray.speakers";
        if (!(c.RewardSizeUl > 0 && c.RewardSizeUl <= LoadRewardLine.DefaultMaxRewardUl)) yield return string.Format("reward.rewardSizeUl must be above 0 and at most {0} ul", LoadRewardLine.DefaultMaxRewardUl);
        foreach (var p in TaskBlocks.AutoRewardProblems(c.AutoRewardProbability)) yield return p;
        if (!(c.LaserOnProbability >= 0 && c.LaserOnProbability <= 1)) yield return "optogenetics.laserOnProbability must be in [0, 1]";
    }

    readonly UclOpenCoenDomeNavigationTaskParameters task;
    readonly EqualisedSpeakerArray speakers;
    readonly List<TrialConditionDraw> conditions;
    readonly Random random;
    readonly int conditionCount;
    int drawn;

    /// <summary>The seed the random stream started from: the task's rngSeed, or one drawn when it is null.</summary>
    public int Seed { get; private set; }

    public TrialDraw(UclOpenCoenDomeNavigationTaskParameters task, Sweep sweep, Func<string, int> zapitConditionCount, EqualisedSpeakerArray speakers = null)
    {
        this.task = task;
        this.speakers = speakers;
        conditions = CheckTask(task, sweep, speakers);
        var opto = task.Optogenetics;
        if (opto.Enabled) conditionCount = zapitConditionCount != null ? zapitConditionCount(opto.ZapitConditionsPath) : ZapitConditions.Check(opto.ZapitConditionsPath);
        Seed = RngSeed.Resolve(task.RngSeed);
        random = new Random(Seed);
    }

    /// <summary>What is wrong with a curriculum (null when present and well formed: an id of 0 or more, a code like S03A, a
    /// 4-character version, a path of one capital letter, a label and a sha256), else the reason.</summary>
    public static string CurriculumProblem(Curriculum curriculum)
    {
        if (curriculum == null) return "is empty";
        var bad = new List<string>();
        if (curriculum.Id < 0) bad.Add("id " + curriculum.Id + " is below 0");
        if (curriculum.Code == null || !System.Text.RegularExpressions.Regex.IsMatch(curriculum.Code, @"^S\d{2}[A-Z]$")) bad.Add("code '" + curriculum.Code + "' is not like S03A");
        if (curriculum.Version == null || !System.Text.RegularExpressions.Regex.IsMatch(curriculum.Version, "^[abcdefghjkmnpqrstuvwxyz23456789]{4}$")) bad.Add("version '" + curriculum.Version + "' is not 4 characters of abcdefghjkmnpqrstuvwxyz23456789");
        if (curriculum.Path == null || !System.Text.RegularExpressions.Regex.IsMatch(curriculum.Path, "^[A-Z]$")) bad.Add("path '" + curriculum.Path + "' is not one capital letter");
        if (string.IsNullOrEmpty(curriculum.Label)) bad.Add("label is empty");
        if (string.IsNullOrEmpty(curriculum.Sha256)) bad.Add("sha256 is empty");
        return bad.Count == 0 ? null : "is malformed (" + string.Join("; ", bad) + ")";
    }

    /// <summary>The conditions as built at Go, in trialConditions' order.</summary>
    public IList<TrialConditionDraw> Conditions { get { return conditions; } }

    /// <summary>Checks the task can be run with the sweep and gives its conditions as drawn (BuildConditions): its curriculum (the
    /// only record of the stage, filled by rig-control) is present and well formed, what pydantic would check (Bonsai reads the
    /// task file without it), on the task and every combination of every condition, and what only the sweep can say; throws
    /// naming every problem.</summary>
    public static List<TrialConditionDraw> CheckTask(UclOpenCoenDomeNavigationTaskParameters task, Sweep sweep, EqualisedSpeakerArray speakers = null)
    {
        var curriculumProblem = CurriculumProblem(task.Curriculum);
        if (curriculumProblem != null) throw new InvalidOperationException("The task file's curriculum " + curriculumProblem + ": rig-control fills it from the Mouse Curriculum at launch.");
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
        number("visualStimulus.spatialFrequencyStartCpd", task.VisualStimulus.SpatialFrequencyStartCpd, true);
        number("visualStimulus.spatialFrequencyEndCpd", task.VisualStimulus.SpatialFrequencyEndCpd, true);
        if (task.AuditoryStimulus == null || task.Reward == null) problems.Add("auditoryStimulus and reward are required");
        else
        {
            problems.AddRange(ConditionProblems(Base(task), speakers));
            problems.AddRange(RuleProblems(task));
        }
        var built = problems.Count == 0 ? BuildConditions(task, problems) : null;
        if (built != null)
        {
            // Every combination of every condition, each problem named by its condition.
            foreach (var condition in built)
            {
                foreach (var combination in condition.Combinations)
                {
                    var at = condition.Where + (combination.Picks.Length == 0 ? "" : " with " + combination.Picks) + ": ";
                    problems.AddRange(ConditionProblems(combination, speakers).Concat(RuleProblems(task, combination)).Select(p => at + p));
                }
            }
        }
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
        if (problems.Count > 0) throw new InvalidOperationException("The task cannot be run: " + string.Join("; ", problems.Distinct()) + ".");
        return built;
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
        t.wrongLickLimit = task.WrongLickLimit;
        t.responseWindowSeconds = task.ResponseWindowSeconds ?? double.NaN;
        t.wheelGain = task.WheelGain;
        t.autoProgressPerSecond = task.AutoProgressPerSecond;
        var opto = task.Optogenetics;
        t.optogenetics = opto.Enabled;
        // The lead applies in every experiment, so timing never reveals Zapit.
        t.firstPipStep = Math.Max((int)Math.Round(opto.LaserToStimulusSeconds / TrialRules.StepSeconds), 0);
        // The condition, then one of its combinations, whose values the rest is drawn from. Each list is drawn from only when
        // it holds a choice, so a condition with one of each draws as before it was added; the laser comes last, as it needs
        // the combination's laserOnProbability (before the trial-type split's follow-up it came before the condition).
        t.trialCondition = WeightedCondition();
        var condition = conditions[t.trialCondition];
        t.trialType = condition.Label;
        var combination = condition.Combinations.Count == 1 ? condition.Combinations[0] : condition.Combinations[random.Next(condition.Combinations.Count)];
        t.rewardOfferedUl = combination.RewardSizeUl;
        t.autoReward = random.NextDouble() < combination.AutoRewardProbability;   // so 1 is every trial and 0 none
        t.visualContrast = combination.Contrasts.Count == 1 ? combination.Contrasts[0] : combination.Contrasts[random.Next(combination.Contrasts.Count)];
        t.auditoryAmplitude = combination.Amplitude;
        // Where the pips play: every speaker ("all"), or rig.yml's speaker nearest the picked azimuth; nowhere at amplitude 0.
        var azimuths = combination.InitialAzimuths;
        var azimuth = azimuths.Count == 1 ? azimuths[0] : azimuths[random.Next(azimuths.Count)];
        t.speakerChannel = t.auditoryAmplitude > 0 ? Trial.AllSpeakersChannel : -1;
        t.auditoryAzimuthDeg = double.NaN;
        t.auditoryElevationDeg = double.NaN;
        if (t.auditoryAmplitude > 0 && azimuth is double)
        {
            var speaker = NearestSpeaker(speakers, (double)azimuth).Value;
            t.speakerChannel = speaker.Channel;
            t.auditoryAzimuthDeg = speaker.Position.Azimuth;
            t.auditoryElevationDeg = speaker.Position.Elevation;
        }
        if (opto.Enabled)
        {
            // Every trial sends Zapit a condition; laser-off trials at 0 mW (Pip, decision 11).
            ZapitTrial.Draw(random, t.trialNumber, conditionCount, opto.TrialsBeforeLaser, combination.LaserOnProbability, opto.LaserPowerMw.Value,
                out t.laserCondition, out t.laserOn, out t.laserPowerMw);
            t.laserMaxSeconds = opto.LaserMaxSeconds;
        }
        return t;
    }

    /// <summary>rig.yml's speaker nearest an azimuth at elevation 0 (the smallest angle between them; the first in rig.yml's
    /// order on a tie).</summary>
    public static KeyValuePair<string, Speaker> NearestSpeaker(EqualisedSpeakerArray speakers, double azimuthDeg)
    {
        if (speakers == null || speakers.Speakers == null || speakers.Speakers.Count == 0) throw new InvalidOperationException("rig.yml has no speakerArray.speakers to play the pips on.");
        const double rad = Math.PI / 180;
        return speakers.Speakers.OrderBy(s => Math.Acos(Math.Max(-1, Math.Min(1, Math.Cos(s.Value.Position.Elevation * rad) * Math.Cos((azimuthDeg - s.Value.Position.Azimuth) * rad))))).First();
    }

    /// <summary>The loudest tone the conditions can draw: the largest amplitude of any combination of a condition with a weight
    /// above 0, and where it is, for the pips' peak check at Go (LayOutPips).</summary>
    public LoudestSound Loudest()
    {
        var loudest = new LoudestSound { Amplitude = 0, Where = "no condition" };
        foreach (var condition in conditions.Where(c => c.Weight > 0))
        {
            foreach (var combination in condition.Combinations)
            {
                if (combination.Amplitude > loudest.Amplitude)
                {
                    loudest = new LoudestSound { Amplitude = combination.Amplitude, Where = condition.Where + (combination.Picks.Length == 0 ? "" : " with " + combination.Picks) };
                }
            }
        }
        return loudest;
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
[Description("Reads the task file's text into the task (TrialDraw.Read): as DeserializeFromYaml, but a trial condition's set values keep their YAML types (numbers, bools), so Settings_Task logs them as such.")]
[WorkflowElementCategory(ElementCategory.Transform)]
public class ReadTask
{
    public IObservable<UclOpenCoenDomeNavigationTaskLogic> Process(IObservable<string> source)
    {
        return source.Select(TrialDraw.Read);
    }
}

[Combinator]
[Description("Checks the task at Go and makes the rules the trials are drawn from and run by. Input: (task file, rig.yml, sweep); rig.yml's speakerArray places initialAzimuthsDeg's nearest speaker. Stops the workflow on a task whose curriculum is empty or malformed, a trial condition that cannot be built (its set and pairs over the task's values, every combination), a reward zone outside the sweep or the optogenetics rules. A real mouse on a rig without hardware triggering is lib-open-rigs' ConnectZapit's refusal.")]
[WorkflowElementCategory(ElementCategory.Transform)]
public class CheckTaskAtGo
{
    public IObservable<TrialRules> Process(IObservable<Tuple<UclOpenCoenDomeNavigationTaskLogic, LabRig, Sweep>> source)
    {
        return source.Select(x => TrialRules.Create(x.Item1.TaskParameters, x.Item3, x.Item2.Zapit != null && x.Item2.Zapit.HardwareTriggered, null, x.Item2.SpeakerArray));
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
