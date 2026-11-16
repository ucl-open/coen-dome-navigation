using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenRigs.Devices;
using OpenRigs.Devices.Rigs;
using OpenRigs.Devices.Zapit;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Runtime.CompilerServices;
using UclOpenCoenDomeNavigationTask;
using YamlDotNet.Serialization;
using TrialEventOf = OpenRigs.Devices.TrialEvent<Trial, StimulusFrame, TrialOutcome>;

static class Fixture
{
    public static string Repo([CallerFilePath] string here = "")
    {
        return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here), "..", ".."));
    }

    public static UclOpenCoenDomeNavigationTaskParameters Task()
    {
        var text = File.ReadAllText(Path.Combine(Repo(), "config", "task-abstract-av-corridor.yml"));
        var task = new DeserializerBuilder().Build().Deserialize<UclOpenCoenDomeNavigationTaskLogic>(text).TaskParameters;
        task.RngSeed = 1;
        return task;
    }

    public static Sweep Sweep()
    {
        return global::Sweep.Load(Path.Combine(Repo(), "src", "waveforms", "sweep.json"));
    }

    public static TrialRules Setup(UclOpenCoenDomeNavigationTaskParameters task, int stage, bool hardware = true)
    {
        return TrialRules.Create(task, stage, Sweep(), hardware, path => 4);
    }

    public static void Opto(UclOpenCoenDomeNavigationTaskParameters task)
    {
        task.Optogenetics.UseZapit = true;
        task.Optogenetics.ZapitConfigPath = "conditions.yml";
        task.Optogenetics.LaserPowerMw = 2.0;
    }
}

/// <summary>Runs lib-open-rigs' TrialLoop with the task's rules on a stand-in Harp clock, 1 ms a tick, the wheel,
/// the left spout and Zapit's replies put in by the test (as RunTrials feeds it: no right spout).</summary>
class Rig
{
    public readonly TrialLoop<Trial, StimulusFrame, TrialOutcome> Loop;
    public readonly List<TrialEventOf> Events = new List<TrialEventOf>();
    public double Now;

    public Rig(TrialRules rules)
    {
        Loop = new TrialLoop<Trial, StimulusFrame, TrialOutcome>(rules);
    }

    public void Until(double seconds, Func<bool> stop = null)
    {
        while (Now < seconds - 1e-9 && (stop == null || !stop()))
        {
            Now = Math.Round(Now + 0.001, 6);
            Events.AddRange(Loop.OnClock(Now));
        }
    }

    public TrialEventOf RunTo(TrialEventKind kind, double limit = 60)
    {
        var seen = Events.Count;
        Until(Now + limit, () => Events.Skip(seen).Any(e => e.Kind == kind));
        return Events.Skip(seen).First(e => e.Kind == kind);
    }

    public void Wheel(double cm) { Events.AddRange(Loop.OnWheel(cm)); }
    public void Touch(bool touched) { Events.AddRange(Loop.OnLick(0, touched)); }
    public void Lick() { Touch(true); Touch(false); }
}

[TestClass]
public class VirtualTrackTests
{
    [TestMethod]
    public void RunningBackwardsCannotGoBehindTheStartAndTheEndCaps()
    {
        var track = new VirtualTrack(100, 50);
        Assert.AreEqual(10, track.Update(60), 1e-9);
        Assert.AreEqual(0, track.Update(40), 1e-9);    // 10 cm behind the start: held at 0
        Assert.AreEqual(5, track.Update(45), 1e-9);    // forward from where it turned
        Assert.AreEqual(100, track.Update(400), 1e-9);
        Assert.IsTrue(track.AtEnd);
    }
}

[TestClass]
public class TrialDrawTests
{
    [TestMethod]
    public void AStagePastTheTableIsRefused()
    {
        var error = Assert.ThrowsException<InvalidOperationException>(() => Fixture.Setup(Fixture.Task(), 6));
        StringAssert.Contains(error.Message, "rows 0 to 5");
    }

    [TestMethod]
    public void ARigWithoutAZapitBlockMakesTheRulesWhenTheLaserIsOff()
    {
        var logic = new UclOpenCoenDomeNavigationTaskLogic { TaskParameters = Fixture.Task() };
        var rig = new LabRig();   // rig.yml with no zapit block: Zapit is null
        Assert.IsNull(rig.Zapit);
        var rules = new CheckTaskAtGo().Process(Observable.Return(Tuple.Create(logic, 0, rig, Fixture.Sweep()))).Wait();
        Assert.IsNull(rules.Laser);
    }

    [TestMethod]
    public void EmptyTrackLengthsAreRefused()
    {
        var task = Fixture.Task();
        task.TrialStructure.TrackLengthsCm.Clear();
        StringAssert.Contains(Assert.ThrowsException<InvalidOperationException>(() => Fixture.Setup(task, 0)).Message, "trackLengthsCm");
    }

    [TestMethod]
    public void TheSameSeedDrawsTheSameTrials()
    {
        var a = new TrialDraw(Fixture.Setup(Fixture.Task(), 5));
        var b = new TrialDraw(Fixture.Setup(Fixture.Task(), 5));
        for (int i = 0; i < 50; i++)
        {
            var x = a.Next();
            var y = b.Next();
            Assert.AreEqual(x.trackLengthCm, y.trackLengthCm);
            Assert.AreEqual(x.truncationFraction, y.truncationFraction);
            Assert.AreEqual(x.itiSeconds, y.itiSeconds);
            Assert.AreEqual(x.trialType, y.trialType);
        }
    }

    static TrialTypeWeights Weights(int visual, int auditory, int coherent, int conflict = 0, int neutral = 0)
    {
        return new TrialTypeWeights { Visual = visual, Auditory = auditory, AudiovisualCoherent = coherent, AudiovisualConflict = conflict, Neutral = neutral };
    }

    [TestMethod]
    public void EachTrialsTypeIsDrawnFromTheTasksWeights()
    {
        // The task file: auditory alone, no stage with its own weights (Pip, 16 Nov 2026), so mice run as before trialWeights
        var task = Fixture.Task();
        Assert.IsTrue(task.TrainingStages.All(s => s.TrialWeights == null));
        var file = new TrialDraw(Fixture.Setup(task, 3));
        for (int i = 0; i < 100; i++) Assert.AreEqual("auditory", file.Next().trialType);
        task.TrialWeights = Weights(0, 1, 3);   // a mouse's weights, edited on Setup
        var draw = new TrialDraw(Fixture.Setup(task, 3));
        var types = Enumerable.Range(0, 4000).Select(i => draw.Next().trialType).ToList();
        Assert.AreEqual(0, types.Count(t => t == "visual"));   // weight 0: never
        Assert.AreEqual(0.75, types.Count(t => t == "audiovisualCoherent") / 4000.0, 0.03);
        Assert.AreEqual(1000, types.Count(t => t == "auditory"), 120);
        var again = new TrialDraw(Fixture.Setup(task, 3));   // the same seed: the same types
        CollectionAssert.AreEqual(types, Enumerable.Range(0, 4000).Select(i => again.Next().trialType).ToList());
        task.RngSeed = 2;
        var other = new TrialDraw(Fixture.Setup(task, 3));
        CollectionAssert.AreNotEqual(types, Enumerable.Range(0, 4000).Select(i => other.Next().trialType).ToList());
    }

    [TestMethod]
    public void AStagesOwnWeightsReplaceTheTasksForThatStageAlone()
    {
        var task = Fixture.Task();
        task.TrialWeights = Weights(1, 0, 0);   // the mouse's: visual
        task.TrainingStages[2].TrialWeights = Weights(0, 0, 1);
        var staged = new TrialDraw(Fixture.Setup(task, 2));
        for (int i = 0; i < 100; i++) Assert.AreEqual("audiovisualCoherent", staged.Next().trialType);
        // A stage without its own keeps the mouse's edited weights, at any stage it moves to.
        foreach (var stage in new[] { 1, 3, 4 })
        {
            var draw = new TrialDraw(Fixture.Setup(task, stage));
            for (int i = 0; i < 100; i++) Assert.AreEqual("visual", draw.Next().trialType);
        }
    }

    [TestMethod]
    public void WeightsAllZeroNegativeOrForAnUndefinedTrialAreRefused()
    {
        var cases = new[]
        {
            Tuple.Create(Weights(0, 0, 0), "needs every weight 0 or more and one above 0"),
            Tuple.Create(Weights(-1, 2, 0), "needs every weight 0 or more and one above 0"),
            Tuple.Create(Weights(0, 1, 0, conflict: 1), "audiovisualConflict must be 0: that trial is not defined in this task yet"),
            Tuple.Create(Weights(0, 1, 0, neutral: 1), "neutral must be 0: that trial is not defined in this task yet"),
        };
        foreach (var c in cases)
        {
            var task = Fixture.Task();
            task.TrialWeights = c.Item1;
            StringAssert.Contains(Assert.ThrowsException<InvalidOperationException>(() => Fixture.Setup(task, 2)).Message, "trialWeights" + (c.Item2.StartsWith("needs") ? " " : ".") + c.Item2);
            task = Fixture.Task();
            task.TrainingStages[2].TrialWeights = c.Item1;
            StringAssert.Contains(Assert.ThrowsException<InvalidOperationException>(() => Fixture.Setup(task, 2)).Message, "stage '" + task.TrainingStages[2].Label + "''s trialWeights");
            Fixture.Setup(task, 1);   // another stage, with the task's weights, still runs
        }
    }

    [TestMethod]
    public void TheTaskFilesWeightsAreReadByTheirYamlNames()
    {
        var text = File.ReadAllText(Path.Combine(Fixture.Repo(), "config", "task-abstract-av-corridor.yml"))
            .Replace("trialWeights: { visual: 0, auditory: 1, audiovisualCoherent: 0,", "trialWeights: { visual: 2, auditory: 1, audiovisualCoherent: 3,");
        var task = new DeserializerBuilder().Build().Deserialize<UclOpenCoenDomeNavigationTaskLogic>(text).TaskParameters;
        CollectionAssert.AreEqual(new[] { 2, 1, 3 }, TrialDraw.Weights(task, task.TrainingStages[0]));
    }

    [TestMethod]
    public void EachTrialShowsWhatItsTypeSaysAndLogsIt()
    {
        foreach (var type in TrialDraw.TypeNames)
        {
            var task = Fixture.Task();
            task.TrialWeights = Weights(type == "visual" ? 1 : 0, type == "auditory" ? 1 : 0, type == "audiovisualCoherent" ? 1 : 0);
            var rig = new Rig(Fixture.Setup(task, 1));
            rig.RunTo(TrialEventKind.Start);
            var pip = rig.RunTo(TrialEventKind.Update).Update;
            Assert.AreEqual(type == "visual" ? 0.0 : 1.0, pip.ToneGain, type);
            Assert.AreEqual(type == "auditory" ? 0.0 : 1.0, pip.GratingOpacity, type);
            rig.Wheel(1000);
            Assert.AreEqual(type, rig.RunTo(TrialEventKind.Outcome).Outcome.Row.trialType);
        }
    }

    [TestMethod]
    public void TheIntervalIsDrawnBeforeEveryTrialTheFirstToo()
    {
        var draw = new TrialDraw(Fixture.Setup(Fixture.Task(), 0));
        for (int i = 0; i < 100; i++)
        {
            var iti = draw.Next().itiSeconds;
            Assert.IsTrue(iti >= 2.0 && iti <= 3.0, iti.ToString());
        }
    }

    [TestMethod]
    public void TracksAreScaledByTheStageAndTruncationFollowsItsFraction()
    {
        var draw = new TrialDraw(Fixture.Setup(Fixture.Task(), 0));
        var lengths = Enumerable.Range(0, 200).Select(_ => draw.Next()).Select(t => t.trackLengthCm).Distinct().OrderBy(l => l).ToList();
        CollectionAssert.AreEqual(new[] { 10.0, 12.5, 15.0 }, lengths);
        draw = new TrialDraw(Fixture.Setup(Fixture.Task(), 5));
        var trials = Enumerable.Range(0, 2000).Select(_ => draw.Next()).ToList();
        var truncated = trials.Where(t => t.truncationFraction < 1).ToList();
        Assert.AreEqual(0.16, truncated.Count / 2000.0, 0.03);
        CollectionAssert.AreEquivalent(new[] { 0.4, 0.6 }, truncated.Select(t => t.truncationFraction).Distinct().ToList());
    }

    [TestMethod]
    public void EveryOptogeneticsTrialHasAConditionAndLaserOffIsZeroMilliwatts()
    {
        var task = Fixture.Task();
        Fixture.Opto(task);
        var draw = new TrialDraw(Fixture.Setup(task, 4));
        var trials = Enumerable.Range(0, 500).Select(_ => draw.Next()).ToList();
        Assert.IsTrue(trials.All(t => t.laserCondition >= 1 && t.laserCondition <= 4));
        Assert.IsTrue(trials.Take(20).All(t => !t.laserOn));
        Assert.IsTrue(trials.Where(t => !t.laserOn).All(t => t.laserPowerMw == 0));
        Assert.IsTrue(trials.Where(t => t.laserOn).All(t => t.laserPowerMw == 2.0));
        Assert.AreEqual(0.4, trials.Skip(20).Count(t => t.laserOn) / 480.0, 0.07);
        var rules = Fixture.Setup(task, 4);
        Assert.IsTrue(trials.All(t => t.firstPipBeep == 2 && rules.Timing(t).Laser && rules.Timing(t).LaserToStimulusSeconds == 0.1));
        Assert.IsTrue(rules.Laser.HardwareTriggered);   // armed during the interval
    }

    [TestMethod]
    public void ASoftwareTriggeredRigIsNeverArmedAhead()
    {
        // Only the test mouse runs there: lib-open-rigs' ConnectZapit refuses a real one (RequireZapitHardwareTrigger).
        var task = Fixture.Task();
        Fixture.Opto(task);
        Assert.IsFalse(Fixture.Setup(task, 4, false).Laser.HardwareTriggered);
    }

    [TestMethod]
    public void ALeadTimeOutsideWholeBeepsUpTo200MsIsRefused()
    {
        foreach (var seconds in new[] { 0.07, -0.07, 0.25 })
        {
            var task = Fixture.Task();
            Fixture.Opto(task);
            task.Optogenetics.LaserToStimulusSeconds = seconds;
            StringAssert.Contains(Assert.ThrowsException<InvalidOperationException>(() => Fixture.Setup(task, 4)).Message, "laserToStimulusSeconds");
        }
        var ok = Fixture.Task();
        Fixture.Opto(ok);
        ok.Optogenetics.LaserToStimulusSeconds = 0.2;
        Assert.AreEqual(4, new TrialDraw(Fixture.Setup(ok, 4)).Next().firstPipBeep);
        ok.Optogenetics.LaserToStimulusSeconds = -0.5;   // after the first pip: the first pip at the trial start, the trigger 10 beeps later
        var late = new TrialDraw(Fixture.Setup(ok, 4)).Next();
        Assert.IsTrue(late.firstPipBeep == 0 && Fixture.Setup(ok, 4).Timing(late).LaserToStimulusSeconds == -0.5);
        ok.Optogenetics.LaserToStimulusSeconds = -12.05;  // past stage 4's 12 s limit
        StringAssert.Contains(Assert.ThrowsException<InvalidOperationException>(() => Fixture.Setup(ok, 4)).Message, "responseWindowSeconds");
        ok.Optogenetics.LaserToStimulusSeconds = -2.05;   // past the 2 s on a stage without one
        StringAssert.Contains(Assert.ThrowsException<InvalidOperationException>(() => Fixture.Setup(ok, 2)).Message, "without responseWindowSeconds");
        var tight = Fixture.Task();
        tight.TrialStructure.ItiMinSeconds = 0.35;
        tight.Optogenetics.LaserToStimulusSeconds = 0.2;
        StringAssert.Contains(Assert.ThrowsException<InvalidOperationException>(() => Fixture.Setup(tight, 1)).Message, "reply margin");
    }

    [TestMethod]
    public void TheQuietWaitsAreRefusedNegativeOrHalfSet()
    {
        var task = Fixture.Task();
        task.TrainingStages[1].WheelQuietSeconds = 1;
        StringAssert.Contains(Assert.ThrowsException<InvalidOperationException>(() => Fixture.Setup(task, 1)).Message, "are both above 0 (the wheel's quiet wait) or both 0 (off)");
        task.TrainingStages[1].WheelQuietCm = 2;
        Fixture.Setup(task, 1);
        task.TrainingStages[1].LickQuietSeconds = -1;
        StringAssert.Contains(Assert.ThrowsException<InvalidOperationException>(() => Fixture.Setup(task, 1)).Message, "lickQuietSeconds is -1, not from 0 to 10 s");
    }

    [TestMethod]
    public void AQuietWaitAboveItsMaximumIsRefusedAtGo()
    {
        var cases = new Dictionary<string, Action<TrainingStage>>
        {
            { "lickQuietSeconds is 10.5, not from 0 to 10 s", s => s.LickQuietSeconds = 10.5 },
            { "wheelQuietSeconds is 11, not from 0 to 10 s", s => { s.WheelQuietSeconds = 11; s.WheelQuietCm = 1; } },
            { "wheelQuietCm is 21, not from 0 to 20 cm", s => { s.WheelQuietSeconds = 1; s.WheelQuietCm = 21; } },
        };
        foreach (var c in cases)
        {
            var task = Fixture.Task();
            c.Value(task.TrainingStages[4]);
            StringAssert.Contains(Assert.ThrowsException<InvalidOperationException>(() => Fixture.Setup(task, 4), c.Key).Message, c.Key);
        }
        var atMax = Fixture.Task();
        atMax.TrainingStages[4].LickQuietSeconds = 10;
        atMax.TrainingStages[4].WheelQuietSeconds = 10;
        atMax.TrainingStages[4].WheelQuietCm = 20;
        Fixture.Setup(atMax, 4);
    }

    [TestMethod]
    public void ANaNOrInfiniteTimeIsRefusedAtGo()
    {
        // YAML's .nan or .inf: an interval, quiet wait or timeout of NaN or infinity would hold the task for ever, with no error.
        var cases = new Dictionary<string, Action<UclOpenCoenDomeNavigationTaskParameters>>
        {
            { "itiMaxSeconds", t => t.TrialStructure.ItiMaxSeconds = double.PositiveInfinity },
            { "itiMinSeconds", t => t.TrialStructure.ItiMinSeconds = double.NaN },
            { "timeoutSeconds", t => t.Penalty.TimeoutSeconds = double.NaN },
            { "lickQuietSeconds", t => t.TrainingStages[4].LickQuietSeconds = double.NaN },
            { "wheelQuietSeconds", t => t.TrainingStages[4].WheelQuietSeconds = double.PositiveInfinity },
            { "trackLengthScale", t => t.TrainingStages[4].TrackLengthScale = 0 },
            { "responseWindowSeconds", t => t.TrainingStages[4].ResponseWindowSeconds = double.NaN },
            { "trackLengthsCm", t => t.TrialStructure.TrackLengthsCm[0] = double.PositiveInfinity },
            { "laserMaxSeconds", t => { Fixture.Opto(t); t.Optogenetics.LaserMaxSeconds = double.PositiveInfinity; } },
        };
        foreach (var c in cases)
        {
            var task = Fixture.Task();
            c.Value(task);
            StringAssert.Contains(Assert.ThrowsException<InvalidOperationException>(() => Fixture.Setup(task, 4), c.Key).Message, c.Key);
        }
    }

    [TestMethod]
    public void ZapitIsSentEachTrialsConditionPowerAndLaserState()
    {
        // lib-open-rigs' ZapitTrialCommand reads the trial through IZapitTrial; 0 s: continuous, Bonsai stops it.
        var off = new Trial { trialNumber = 3, laserCondition = 2, laserOn = false, laserPowerMw = 0 };
        var on = new Trial { trialNumber = 4, laserCondition = 1, laserOn = true, laserPowerMw = 2.0 };
        var sent = new List<ZapitCommand>();
        new ZapitTrialCommand { HardwareTriggered = true }.Process(new[] { off }.ToObservable(), Observable.Empty<Trial>()).Subscribe(sent.Add);
        new ZapitTrialCommand { HardwareTriggered = false }.Process(Observable.Empty<Trial>(), new[] { on }.ToObservable()).Subscribe(sent.Add);
        Assert.AreEqual(2, sent.Count);
        Assert.AreEqual(2, sent[0].ConditionNumber);
        Assert.IsFalse(sent[0].LaserOn);
        Assert.AreEqual(0f, sent[0].LaserPower);
        Assert.IsTrue(sent[0].HardwareTriggered);
        Assert.IsTrue(sent[1].LaserOn);
        Assert.AreEqual(2f, sent[1].LaserPower);
        Assert.IsFalse(sent[1].HardwareTriggered);
        Assert.AreEqual(0, sent[1].StimDuration);
    }

    [TestMethod]
    public void ALaserCapShorterThanTheLongestTrialIsRefused()
    {
        var task = Fixture.Task();
        Fixture.Opto(task);
        task.Optogenetics.LaserMaxSeconds = 12.0;
        StringAssert.Contains(Assert.ThrowsException<InvalidOperationException>(() => Fixture.Setup(task, 4)).Message, "laserMaxSeconds");
    }
}

/// <summary>The task's trials: TrialLoop driven tick by tick with the task's rules.</summary>
[TestClass]
public class TrialRulesTests
{
    [TestMethod]
    public void TheFirstTrialWaitsItsIntervalAndPipsEvery50MsFromTheLead()
    {
        var rig = new Rig(Fixture.Setup(Fixture.Task(), 1));
        rig.Now = 100;
        var start = rig.RunTo(TrialEventKind.Start);
        Assert.AreEqual(100.001 + start.Trial.itiSeconds - 0.1, start.Seconds, 1e-9);   // the trial start: the interval's end less the 0.1 s lead
        rig.Until(rig.Now + 1.0);
        var pips = rig.Events.Where(e => e.Kind == TrialEventKind.Update).Select(e => e.Seconds).ToList();
        Assert.AreEqual(100.001 + start.Trial.itiSeconds, pips[0], 0.0011);   // the first pip: exactly the interval after its start
        for (int i = 1; i < pips.Count; i++) Assert.AreEqual(0.05, pips[i] - pips[i - 1], 1e-6);
        Assert.IsTrue(rig.Events.Where(e => e.Kind == TrialEventKind.Update).All(e => e.Update.Row.toneFrequencyHz == 2000 && e.Update.Bin == 0));
    }

    [TestMethod]
    public void ReachingTheZoneOnStage1IsRewardedAndTheNextIntervalRunsFromTheOutcome()
    {
        var task = Fixture.Task();
        var rig = new Rig(Fixture.Setup(task, 1));
        rig.RunTo(TrialEventKind.Start);
        rig.Wheel(1000);   // the end of a short track: the sweep's end
        var outcome = rig.RunTo(TrialEventKind.Outcome);
        Assert.IsTrue(outcome.Outcome.Rewarded);
        Assert.AreEqual("Correct", outcome.Outcome.Row.outcome);
        Assert.AreEqual("FreqReached", outcome.Outcome.Row.endReason);
        Assert.AreEqual(Tuple.Create("left", 3.0), outcome.Outcome.Reward);   // the task file's rewardSizeUl (3 ul since 15 Nov 2026)
        var next = rig.RunTo(TrialEventKind.Start);
        Assert.AreEqual(outcome.Seconds + next.Trial.itiSeconds - 0.1, next.Seconds, 1e-9);
        rig.Until(rig.Now + 0.2);
        var firstPip = rig.Events.First(e => e.Kind == TrialEventKind.Update && e.Trial == next.Trial);
        Assert.AreEqual(outcome.Seconds + next.Trial.itiSeconds, firstPip.Seconds, 0.0011);
    }

    [TestMethod]
    public void Stage0DriftsIntoTheZoneWithoutRunning()
    {
        var rig = new Rig(Fixture.Setup(Fixture.Task(), 0));
        var outcome = rig.RunTo(TrialEventKind.Outcome);
        Assert.AreEqual("FreqReached", outcome.Outcome.Row.endReason);
        Assert.IsTrue(outcome.Outcome.Row.toneEndFrequencyHz >= 18000);
        Assert.IsTrue(outcome.Seconds - outcome.Outcome.Row.trialStartTime > 10 && outcome.Seconds - outcome.Outcome.Row.trialStartTime < 25);
        Assert.AreEqual(outcome.Seconds, outcome.Outcome.Row.outcomeTime, "outcomeTime is the row's own time, harpTime");
    }

    [TestMethod]
    public void ALickInTheZoneIsRewardedAndOutsideCountsTowardTheLimit()
    {
        var task = Fixture.Task();
        var rig = new Rig(Fixture.Setup(task, 4));  // zone 15248-21248 Hz, 5 wrong licks
        rig.RunTo(TrialEventKind.Start);
        rig.Until(rig.Now + 0.2);
        for (int i = 0; i < 4; i++) rig.Lick();
        Assert.IsFalse(rig.Events.Any(e => e.Kind == TrialEventKind.Outcome));
        rig.Lick();
        var outcome = rig.Events.Single(e => e.Kind == TrialEventKind.Outcome);
        Assert.AreEqual("Incorrect", outcome.Outcome.Row.outcome);
        Assert.AreEqual("WrongLickLimit", outcome.Outcome.Row.endReason);
        Assert.IsTrue(outcome.Outcome.Row.penaltySound);
        Assert.AreEqual(2.0, outcome.Outcome.Row.penaltyTimeoutSeconds);

        // the next trial waits the timeout and its interval; the quiet second, counted from the interval's start, adds nothing
        var next = rig.RunTo(TrialEventKind.Start);
        Assert.AreEqual(outcome.Seconds + 2.0 + next.Trial.itiSeconds - 0.1, next.Seconds, 1e-9);
        rig.Until(rig.Now + 0.2);
        var track = next.Trial.trackLengthCm;
        rig.Wheel(track * 0.85);  // inside the zone
        rig.Until(rig.Now + 0.06);
        rig.Lick();
        var rewarded = rig.Events.Last(e => e.Kind == TrialEventKind.Outcome);
        Assert.AreEqual("CorrectLick", rewarded.Outcome.Row.endReason);
        Assert.AreEqual(2.0, rewarded.Outcome.Row.itiStartTime - outcome.Seconds, 0.0011);
        Assert.AreEqual(next.Trial.itiSeconds, rewarded.Outcome.Row.itiSeconds);
    }

    [TestMethod]
    public void TheQuietWaitRestartsWithEachLick()
    {
        var quiet = new Rig(Fixture.Setup(Fixture.Task(), 4));
        var start = quiet.RunTo(TrialEventKind.Start);   // a quiet mouse: the start at the interval's end less the lead
        var iti = start.Trial.itiSeconds;
        Assert.AreEqual(0.001 + iti - 0.1, start.Seconds, 1e-9);
        var r = new Rig(Fixture.Setup(Fixture.Task(), 4));
        r.Until(Math.Round(0.001 + iti - 0.5, 3));
        r.Lick();                                           // licked late in the interval: the start a quiet second later
        r.Until(Math.Round(r.Now + 0.3, 3));
        r.Lick();                                           // and again
        var started = r.RunTo(TrialEventKind.Start);
        Assert.AreEqual(Math.Round(0.001 + iti - 0.5, 3) + 0.3 + 1.0, started.Seconds, 1e-9);
        rig_quiet_wait(r, started);
    }

    static void rig_quiet_wait(Rig r, TrialEventOf started)
    {
        r.Wheel(1000);   // the end of the track ends the trial
        var outcome = r.Events.Single(e => e.Kind == TrialEventKind.Outcome);
        Assert.AreEqual(started.Seconds - (started.Trial.itiSeconds - 0.1 + 0.001), outcome.Outcome.Row.quietWaitSeconds, 1e-9);
        Assert.AreEqual(started.Seconds, outcome.Outcome.Row.trialStartTime, 1e-12);
    }

    [TestMethod]
    public void TheWheelWaitCountsTheLastWheelQuietSeconds()
    {
        var task = Fixture.Task();
        task.TrainingStages[1].WheelQuietSeconds = 1.0;
        task.TrainingStages[1].WheelQuietCm = 1.0;
        var still = new Rig(Fixture.Setup(task, 1));
        still.Wheel(50);
        var start = still.RunTo(TrialEventKind.Start);
        var iti = start.Trial.itiSeconds;
        Assert.AreEqual(0.001 + iti - 0.1, start.Seconds, 1e-9);
        var rock = new Rig(Fixture.Setup(task, 1));
        rock.Wheel(50);
        rock.Until(Math.Round(iti - 0.5, 3));
        rock.Wheel(50.6);                                   // 0.6 cm forward: within 1 cm
        rock.Until(Math.Round(iti - 0.3, 3));
        rock.Wheel(50.0);                                   // and back: net 0, 1.2 cm within a second, so the start a second after the first swing
        var started = rock.RunTo(TrialEventKind.Start);
        Assert.AreEqual(Math.Round(iti - 0.5, 3) + 1.0, started.Seconds, 1e-9);

        // 0.3 cm every 0.4 s up to the start: past 1 cm in all, never more than 0.9 cm within a second, so no delay.
        var fidget = new Rig(Fixture.Setup(task, 1));
        fidget.Wheel(50);
        for (int i = 1; i <= 4; i++)
        {
            fidget.Until(Math.Round(iti - 1.7 + 0.4 * i, 3));
            fidget.Wheel(50 + 0.3 * i);
        }
        Assert.AreEqual(0.001 + iti - 0.1, fidget.RunTo(TrialEventKind.Start).Seconds, 1e-9);
    }

    [TestMethod]
    public void AHeldContactHoldsTheTrialStartUntilReleased()
    {
        var r = new Rig(Fixture.Setup(Fixture.Task(), 4));  // lickQuietSeconds 1
        var iti = new Rig(Fixture.Setup(Fixture.Task(), 4)).RunTo(TrialEventKind.Start).Trial.itiSeconds;
        r.Until(0.5);
        r.Touch(true);            // a paw on the spout, held past the interval's end
        r.Until(Math.Round(iti + 2, 3));
        Assert.IsFalse(r.Events.Any(e => e.Kind == TrialEventKind.Start), "no trial start while the spout is held");
        r.Touch(false);
        Assert.AreEqual(Math.Round(iti + 2, 3) + 1.0, r.RunTo(TrialEventKind.Start).Seconds, 1e-9);
    }

    [TestMethod]
    public void ZapitAddsNoTimeAndTheTriggerIsSignedFromTheFirstPip()
    {
        var plain = new Rig(Fixture.Setup(Fixture.Task(), 1)).RunTo(TrialEventKind.Start);
        foreach (var lead in new[] { 0.1, 0.0, -0.5 })
        {
            var task = Fixture.Task();
            Fixture.Opto(task);
            task.Optogenetics.LaserToStimulusSeconds = lead;
            var rig = new Rig(Fixture.Setup(task, 1));
            var arm = rig.RunTo(TrialEventKind.Arm);
            Assert.AreEqual(0.001, arm.Seconds, 1e-9);       // armed at the interval's start
            rig.Loop.OnZapitReply(new ZapitReply { Command = ZapitCommandType.StartStimulus, Value = arm.Trial.laserCondition, LaserOn = arm.Trial.laserOn ? 1 : 0 });
            var start = rig.RunTo(TrialEventKind.Start);
            Assert.AreEqual(0.001 + start.Trial.itiSeconds - Math.Max(lead, 0), start.Seconds, 1e-9);
            rig.Until(rig.Now + 0.7);
            var pip = rig.Events.First(e => e.Kind == TrialEventKind.Update).Seconds;
            var trigger = rig.Events.Single(e => e.Kind == TrialEventKind.Trigger).Seconds;
            Assert.AreEqual(0.001 + start.Trial.itiSeconds, pip, 0.0011, "the first pip: the interval after its start, Zapit or not");
            Assert.AreEqual(pip - lead, trigger, 0.0011, "the trigger: the first pip less laserToStimulusSeconds");
            if (lead == 0.1) Assert.AreEqual(plain.Seconds, start.Seconds, 1e-9, "the same start as without optogenetics");
        }
    }

    [TestMethod]
    public void ATrialEndedBeforeItsTriggerHasNoLaserAndLogsZeroPower()
    {
        var task = Fixture.Task();
        Fixture.Opto(task);
        task.Optogenetics.LaserToStimulusSeconds = -0.5;   // the trigger half a second after the first pip
        task.Optogenetics.TrialsBeforeLaser = 0;
        task.Optogenetics.LaserOnProbability = 1;
        var rig = new Rig(Fixture.Setup(task, 4));
        var arm = rig.RunTo(TrialEventKind.Arm);
        rig.Loop.OnZapitReply(new ZapitReply { Command = ZapitCommandType.StartStimulus, Value = arm.Trial.laserCondition, LaserOn = 1 });
        rig.RunTo(TrialEventKind.Start);
        rig.Until(rig.Now + 0.1);
        rig.Wheel(1000);                                    // the end of the track ends the trial before the trigger
        var outcome = rig.Events.Single(e => e.Kind == TrialEventKind.Outcome);
        Assert.IsTrue(arm.Trial.laserOn);
        Assert.AreEqual(0, outcome.Outcome.Row.laserPowerMw, "no laser came, so none is logged");
        Assert.IsFalse(rig.Events.Any(e => e.Kind == TrialEventKind.Trigger));
        Assert.IsTrue(double.IsNaN(outcome.Outcome.Row.laserTriggerTime), "answered before the trigger: laserTriggerTime NaN");
    }

    // laserTriggerTime (the shared block's 16th column) is the trigger's Harp time when it came.
    [TestMethod]
    public void LaserTriggerTimeIsTheTriggersTimeLaserOnOrOff()
    {
        foreach (var probability in new[] { 1.0, 0.0 })
        {
            var task = Fixture.Task();
            Fixture.Opto(task);
            task.Optogenetics.TrialsBeforeLaser = 0;
            task.Optogenetics.LaserOnProbability = probability;
            var rig = new Rig(Fixture.Setup(task, 4));
            var arm = rig.RunTo(TrialEventKind.Arm);
            rig.Loop.OnZapitReply(new ZapitReply { Command = ZapitCommandType.StartStimulus, Value = arm.Trial.laserCondition, LaserOn = arm.Trial.laserOn ? 1 : 0 });
            var trigger = rig.RunTo(TrialEventKind.Trigger);
            rig.Until(rig.Now + 0.2);
            rig.Wheel(1000);                                // the end of the track ends the trial after the trigger
            var outcome = rig.Events.Single(e => e.Kind == TrialEventKind.Outcome);
            Assert.AreEqual(trigger.Seconds, outcome.Outcome.Row.laserTriggerTime, 1e-12);
            Assert.AreEqual(probability > 0 ? 2.0 : 0.0, outcome.Outcome.Row.laserPowerMw, "laser off: a trigger time, 0 mW");
        }
    }

    [TestMethod]
    public void LaserTriggerTimeIsNaNWithoutOptogenetics()
    {
        var rig = new Rig(Fixture.Setup(Fixture.Task(), 1));
        rig.RunTo(TrialEventKind.Start);
        rig.Wheel(1000);
        var outcome = rig.RunTo(TrialEventKind.Outcome);
        Assert.IsTrue(double.IsNaN(outcome.Outcome.Row.laserTriggerTime));
        Assert.AreEqual(0, outcome.Outcome.Row.laserCondition);
    }

    [TestMethod]
    public void TheTrialRowsFieldsAreTheTrialColumnsInOrder()
    {
        // schemas\trial-columns.yml's names, harpTime aside (the row's timestamp), are TrialRow's fields in order:
        // LogTrial writes the fields in declaration order.
        var yaml = new DeserializerBuilder().Build().Deserialize<Dictionary<string, object>>(
            File.ReadAllText(Path.Combine(Fixture.Repo(), "schemas", "trial-columns.yml")));
        var fields = typeof(TrialRow).GetFields().Select(f => f.Name).ToList();
        CollectionAssert.AreEqual(yaml.Keys.Skip(1).ToList(), fields);
        var laser = fields.IndexOf("laserTriggerTime");
        Assert.AreEqual(fields.IndexOf("laserPowerMw") + 1, laser);
        Assert.AreEqual(typeof(double), typeof(TrialRow).GetField("laserTriggerTime").FieldType);
    }

    [TestMethod]
    public void AResponseWindowEndingInTheZoneIsPenalised()
    {
        var rig = new Rig(Fixture.Setup(Fixture.Task(), 4));
        var start = rig.RunTo(TrialEventKind.Start);
        rig.Wheel(start.Trial.trackLengthCm * 0.85);
        var outcome = rig.RunTo(TrialEventKind.Outcome);
        Assert.AreEqual("ResponseWindowEnd", outcome.Outcome.Row.endReason);
        Assert.AreEqual("NoResponse", outcome.Outcome.Row.outcome);
        Assert.IsTrue(outcome.Outcome.Row.penaltySound);
        Assert.AreEqual(start.Seconds + 0.1 + 12.0, outcome.Seconds, 0.0011);
    }

    [TestMethod]
    public void TheEndOfTheTrackEndsTheTrial()
    {
        var rig = new Rig(Fixture.Setup(Fixture.Task(), 2));
        var start = rig.RunTo(TrialEventKind.Start);
        rig.Until(rig.Now + 0.1);
        rig.Wheel(start.Trial.trackLengthCm + 5);
        var outcome = rig.Events.Single(e => e.Kind == TrialEventKind.Outcome);
        Assert.AreEqual("EndOfTrack", outcome.Outcome.Row.endReason);
        Assert.AreEqual(start.Trial.trackLengthCm, outcome.Outcome.Row.endPositionCm, 1e-9);
    }

    [TestMethod]
    public void ATruncatedTrialSilencesThePipsPastItsFraction()
    {
        var setup = Fixture.Setup(Fixture.Task(), 3);
        var rig = new Rig(setup);
        var start = rig.RunTo(TrialEventKind.Start);
        start.Trial.truncationFraction = 0.5;
        rig.Wheel(start.Trial.trackLengthCm * 0.3);
        rig.Until(rig.Now + 0.1);
        var before = rig.Events.Last(e => e.Kind == TrialEventKind.Update).Update;
        Assert.AreEqual(1.0, before.ToneGain);
        Assert.IsTrue(before.Row.stimulusActive);
        rig.Wheel(start.Trial.trackLengthCm * 0.7);
        rig.Until(rig.Now + 0.1);
        var pip = rig.Events.Last(e => e.Kind == TrialEventKind.Update).Update;
        Assert.AreEqual(0.0, pip.ToneGain);
        Assert.IsFalse(pip.Row.stimulusActive);
        // A bool, so BonsaiData_StimulusState says True or False, as the lab's other bool columns.
        Assert.AreEqual(typeof(bool), typeof(StimulusStateRow).GetField("stimulusActive").FieldType);
    }

    [TestMethod]
    public void AZapitReplyNotInByTheTriggerStopsTheTask()
    {
        var task = Fixture.Task();
        Fixture.Opto(task);
        var rig = new Rig(Fixture.Setup(task, 4));
        rig.RunTo(TrialEventKind.Arm);
        var error = Assert.ThrowsException<InvalidOperationException>(() => rig.RunTo(TrialEventKind.Trigger));
        StringAssert.Contains(error.Message, "had not answered trial 1's StartStimulus by its trigger");
        var wrong = new Rig(Fixture.Setup(task, 4));
        wrong.RunTo(TrialEventKind.Arm);
        Assert.ThrowsException<InvalidOperationException>(() => wrong.Loop.OnZapitReply(new ZapitReply { Command = ZapitCommandType.StartStimulus, Value = 99 }));
    }

    // The armed reply is checked by OpenRigs.Devices' ZapitTrial.CheckArmed, the dome tasks' one check:
    // its words, and a refusal for an error reply, another condition or another laser state.
    [TestMethod]
    public void TheArmedReplyIsZapitTrialsCheckArmed()
    {
        var task = Fixture.Task();
        Fixture.Opto(task);
        task.Optogenetics.TrialsBeforeLaser = 0;
        task.Optogenetics.LaserOnProbability = 1;
        Func<Rig> armed = () => { var r = new Rig(Fixture.Setup(task, 4)); r.RunTo(TrialEventKind.Arm); return r; };
        var rig = armed();
        var trial = rig.Loop.Trial;
        Assert.IsTrue(trial.laserOn);
        var replies = new[]
        {
            new ZapitReply { Command = ZapitCommandType.StartStimulus, Value = trial.laserCondition, LaserOn = 0 },
            new ZapitReply { Command = ZapitCommandType.StartStimulus, Value = trial.laserCondition + 1, LaserOn = 1 },
            new ZapitReply { Command = ZapitCommandType.StartStimulus, Value = trial.laserCondition, LaserOn = 1, IsError = true },
        };
        foreach (var reply in replies)
        {
            var mine = Assert.ThrowsException<InvalidOperationException>(() => armed().Loop.OnZapitReply(reply)).Message;
            var lib = Assert.ThrowsException<InvalidOperationException>(() => ZapitTrial.CheckArmed(reply, trial.trialNumber, trial.laserCondition, true)).Message;
            Assert.AreEqual(lib, mine);
            StringAssert.Contains(mine, "is the DAQ working?");
        }
        rig.Loop.OnZapitReply(new ZapitReply { Command = ZapitCommandType.StartStimulus, Value = trial.laserCondition, LaserOn = 1 });
        rig.RunTo(TrialEventKind.Trigger);   // armed: the trigger comes
    }

    // On a software-triggered rig the Trigger event sends the StartStimulus, so its reply is checked too.
    [TestMethod]
    public void ASoftwareTriggeredZapitsWrongReplyStopsTheTask()
    {
        var task = Fixture.Task();
        Fixture.Opto(task);
        var rig = new Rig(Fixture.Setup(task, 4, false));
        rig.RunTo(TrialEventKind.Trigger);
        Assert.IsFalse(rig.Events.Any(e => e.Kind == TrialEventKind.Arm));
        Assert.ThrowsException<InvalidOperationException>(() => rig.Loop.OnZapitReply(new ZapitReply { Command = ZapitCommandType.StartStimulus, Value = 99 }));
        var right = new Rig(Fixture.Setup(task, 4, false));
        var trigger = right.RunTo(TrialEventKind.Trigger);
        right.Loop.OnZapitReply(new ZapitReply { Command = ZapitCommandType.StartStimulus, Value = trigger.Trial.laserCondition, LaserOn = trigger.Trial.laserOn ? 1 : 0 });
    }

    [TestMethod]
    public void TheLaserIsCappedOnTheHarpClockOnAStageWithAnUnlimitedResponseWindow()
    {
        var task = Fixture.Task();
        Fixture.Opto(task);
        var rig = new Rig(Fixture.Setup(task, 2, false));
        var start = rig.RunTo(TrialEventKind.Start);
        var cap = rig.RunTo(TrialEventKind.LaserCap);
        Assert.AreEqual(start.Seconds + 12.5, cap.Seconds, 0.0011);   // from the trigger, at the trial start with the 0.1 s lead
    }

    [TestMethod]
    public void Stage0RunningAt40CmPerSecondPastTheCeilingIsStillRewarded()
    {
        // 6 s of drift, then 2 cm a beep: the beep that crosses the floor is above the ceiling (the sweep's end, 25000 Hz).
        var rig = new Rig(Fixture.Setup(Fixture.Task(), 0));
        rig.RunTo(TrialEventKind.Start);
        rig.Until(rig.Now + 6.0);
        double cm = 0;
        TrialEventOf outcome = null;
        for (int i = 0; i < 200 && outcome == null; i++)
        {
            cm += 2.0;
            rig.Wheel(cm);
            var seen = rig.Events.Count;
            rig.Until(rig.Now + 0.05);
            outcome = rig.Events.Skip(seen).FirstOrDefault(e => e.Kind == TrialEventKind.Outcome);
        }
        Assert.AreEqual("FreqReached", outcome.Outcome.Row.endReason);
        Assert.IsTrue(outcome.Outcome.Row.toneEndFrequencyHz > 25000, outcome.Outcome.Row.toneEndFrequencyHz.ToString());
        Assert.IsTrue(outcome.Outcome.Rewarded);
        Assert.AreEqual("Correct", outcome.Outcome.Row.outcome);
        Assert.IsFalse(outcome.Outcome.Row.penaltySound);
    }

    [TestMethod]
    public void AfterAGapInTheHarpClockOnlyTheLatestBeepPips()
    {
        var rig = new Rig(Fixture.Setup(Fixture.Task(), 3));
        var start = rig.RunTo(TrialEventKind.Start);
        rig.Until(rig.Now + 0.2);
        var seen = rig.Events.Count;
        rig.Now = Math.Round(rig.Now + 0.2, 6);
        rig.Events.AddRange(rig.Loop.OnClock(rig.Now));
        Assert.AreEqual(1, rig.Events.Skip(seen).Count(e => e.Kind == TrialEventKind.Update));
        rig.Until(rig.Now + 0.2);
        var pips = rig.Events.Where(e => e.Kind == TrialEventKind.Update).Select(e => e.Seconds).ToList();
        for (int i = 1; i < pips.Count; i++) Assert.IsTrue(pips[i] - pips[i - 1] > 0.049, pips[i].ToString());
        Assert.AreEqual(start.Seconds + 0.6, pips.Last(), 0.0011);   // back on the 50 ms grid
    }

    [TestMethod]
    public void TheTriggerLineIsHeldOnlyInAnOptogeneticsExperiment()
    {
        var off = Fixture.Setup(Fixture.Task(), 3);
        Assert.IsFalse(new TrialDraw(off).Next().optogenetics);
        Assert.IsNull(off.Laser);
        var plain = new Rig(off);
        var ended = plain.RunTo(TrialEventKind.Outcome);
        plain.Until(ended.Seconds + 2.0);
        Assert.IsFalse(plain.Events.Any(e => e.Kind == TrialEventKind.Trigger || e.Kind == TrialEventKind.TriggerOff || e.Kind == TrialEventKind.Arm));
        var task = Fixture.Task();
        Fixture.Opto(task);
        var on = Fixture.Setup(task, 3);
        Assert.IsTrue(new TrialDraw(on).Next().optogenetics);
        Assert.AreEqual(1.0, on.Laser.TriggerHoldSeconds);
    }

    // The line falls triggerHoldSeconds after the outcome, on the Harp clock (TrialLoop's TriggerOff, which replaced
    // TaskLogic's HarpDelay), and the next trial is armed then.
    [TestMethod]
    public void TheTriggerLineFallsTriggerHoldSecondsAfterTheOutcomeAndTheNextIsArmedThen()
    {
        var task = Fixture.Task();
        Fixture.Opto(task);
        task.Penalty.TimeoutSeconds = 0;   // the next interval from the outcome: armed once the line falls, not at its start
        var rig = new Rig(Fixture.Setup(task, 4));
        var arm = rig.RunTo(TrialEventKind.Arm);
        rig.Loop.OnZapitReply(new ZapitReply { Command = ZapitCommandType.StartStimulus, Value = arm.Trial.laserCondition, LaserOn = arm.Trial.laserOn ? 1 : 0 });
        rig.RunTo(TrialEventKind.Trigger);
        rig.Wheel(1000);   // the end of the track ends the trial
        var outcome = rig.Events.Single(e => e.Kind == TrialEventKind.Outcome);
        var off = rig.RunTo(TrialEventKind.TriggerOff);
        Assert.AreEqual(outcome.Seconds + 1.0, off.Seconds, 0.0011);
        var next = rig.Events.Single(e => e.Kind == TrialEventKind.Arm && e.Trial.trialNumber == 2);   // on the line's tick
        Assert.AreEqual(off.Seconds, next.Seconds, 1e-9);
    }

    [TestMethod]
    public void ATriggerHoldAsLongAsTheShortestIntervalIsRefused()
    {
        var task = Fixture.Task();
        task.Optogenetics.TriggerHoldSeconds = 2.0;
        Fixture.Setup(task, 0);   // without optogenetics the line stays low, so the hold is not read
        Fixture.Opto(task);
        StringAssert.Contains(Assert.ThrowsException<InvalidOperationException>(() => Fixture.Setup(task, 0)).Message, "triggerHoldSeconds");
    }
}
