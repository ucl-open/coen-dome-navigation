using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenRigs.Devices.Zapit;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using UclOpenCoenDomeNavigationTask;
using YamlDotNet.Serialization;

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

    public static CorridorSetup Setup(UclOpenCoenDomeNavigationTaskParameters task, int stage, bool hardware = true, string subject = "TC001")
    {
        return CorridorSetup.Create(task, stage, Sweep(), hardware, subject, path => 4);
    }

    public static void Opto(UclOpenCoenDomeNavigationTaskParameters task)
    {
        task.Optogenetics.UseZapit = true;
        task.Optogenetics.ZapitConfigPath = "conditions.yml";
        task.Optogenetics.LaserPowerMw = 2.0;
    }
}

/// <summary>Runs CorridorState on a stand-in Harp clock, 1 ms a tick, the wheel and licks put in by the test.</summary>
class Rig
{
    public readonly CorridorState State;
    public readonly List<CorridorEvent> Events = new List<CorridorEvent>();
    public double Now;

    public Rig(CorridorSetup setup)
    {
        State = new CorridorState(setup);
    }

    public void Until(double seconds, Func<bool> stop = null)
    {
        while (Now < seconds - 1e-9 && (stop == null || !stop()))
        {
            Now = Math.Round(Now + 0.001, 6);
            Events.AddRange(State.OnClock(Now));
        }
    }

    public CorridorEvent RunTo(CorridorEventKind kind, double limit = 60)
    {
        var seen = Events.Count;
        Until(Now + limit, () => Events.Skip(seen).Any(e => e.Kind == kind));
        return Events.Skip(seen).First(e => e.Kind == kind);
    }

    public void Wheel(double cm) { Events.AddRange(State.OnWheel(cm)); }
    public void Lick() { Events.AddRange(State.OnLick(true)); Events.AddRange(State.OnLick(false)); }
}

[TestClass]
public class SweepTests
{
    [TestMethod]
    public void TheSweepIsTheToneFiles()
    {
        var sweep = Fixture.Sweep();
        Assert.AreEqual(2000, sweep.StartHz);
        Assert.AreEqual(25000, sweep.EndHz);
        Assert.AreEqual(100, sweep.Steps);
        Assert.AreEqual(0.0025, sweep.PipPeak, 1e-12);
        Assert.AreEqual(2000, sweep.FrequencyAt(-1), 1e-9);
        Assert.AreEqual(25000, sweep.FrequencyAt(2), 1e-6);
        Assert.AreEqual(0, sweep.BinOf(2000));
        Assert.AreEqual(99, sweep.BinOf(25000));
        Assert.AreEqual(99, sweep.BinOf(40000));
    }
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
        }
    }

    [TestMethod]
    public void TheIntervalIsDrawnBeforeEachTrialButTheFirst()
    {
        var draw = new TrialDraw(Fixture.Setup(Fixture.Task(), 0));
        Assert.IsTrue(double.IsNaN(draw.Next().itiSeconds));
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
        Assert.IsTrue(trials.All(t => t.firstPipBeep == 2 && t.armZapit));
    }

    [TestMethod]
    public void OptogeneticsOnASoftwareTriggeredRigIsForTheTestMouseOnly()
    {
        var task = Fixture.Task();
        Fixture.Opto(task);
        StringAssert.Contains(Assert.ThrowsException<InvalidOperationException>(() => Fixture.Setup(task, 4, false, "TC001")).Message, "test mouse");
        Assert.IsFalse(new TrialDraw(Fixture.Setup(task, 4, false, "test")).Next().armZapit);
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

[TestClass]
public class CorridorStateTests
{
    [TestMethod]
    public void TheFirstTrialStartsAtOnceAndPipsEvery50MsOnTheHarpClock()
    {
        var rig = new Rig(Fixture.Setup(Fixture.Task(), 1));
        rig.Now = 100;
        var start = rig.RunTo(CorridorEventKind.Start);
        Assert.AreEqual(100.001, start.Seconds, 1e-9);
        rig.Until(101.0);
        var pips = rig.Events.Where(e => e.Kind == CorridorEventKind.Pip).Select(e => e.Seconds).ToList();
        Assert.AreEqual(100.051, pips[0], 1e-9);   // beep 1
        for (int i = 1; i < pips.Count; i++) Assert.AreEqual(0.05, pips[i] - pips[i - 1], 1e-6);
        Assert.IsTrue(rig.Events.Where(e => e.Kind == CorridorEventKind.Pip).All(e => e.Frame.Row.toneFrequencyHz == 2000 && e.Frame.Bin == 0));
    }

    [TestMethod]
    public void ReachingTheZoneOnStage1IsRewardedAndTheNextIntervalRunsFromTheOutcome()
    {
        var task = Fixture.Task();
        var rig = new Rig(Fixture.Setup(task, 1));
        rig.RunTo(CorridorEventKind.Start);
        rig.Wheel(1000);   // the end of a short track: the sweep's end
        var outcome = rig.RunTo(CorridorEventKind.Outcome);
        Assert.IsTrue(outcome.Rewarded);
        Assert.AreEqual("Correct", outcome.Row.outcome);
        Assert.AreEqual("FreqReached", outcome.Row.endReason);
        Assert.AreEqual(Tuple.Create("left", 2.0), outcome.Reward);
        var next = rig.RunTo(CorridorEventKind.Start);
        Assert.AreEqual(outcome.Seconds + next.Trial.itiSeconds, next.Seconds, 0.0011);
    }

    [TestMethod]
    public void Stage0DriftsIntoTheZoneWithoutRunning()
    {
        var rig = new Rig(Fixture.Setup(Fixture.Task(), 0));
        var outcome = rig.RunTo(CorridorEventKind.Outcome);
        Assert.AreEqual("FreqReached", outcome.Row.endReason);
        Assert.IsTrue(outcome.Row.toneEndFrequencyHz >= 18000);
        Assert.IsTrue(outcome.Seconds - outcome.Row.trialStartTime > 10 && outcome.Seconds - outcome.Row.trialStartTime < 25);
    }

    [TestMethod]
    public void ALickInTheZoneIsRewardedAndOutsideCountsTowardTheLimit()
    {
        var task = Fixture.Task();
        var rig = new Rig(Fixture.Setup(task, 4));  // zone 15248-21248 Hz, 5 wrong licks
        rig.RunTo(CorridorEventKind.Start);
        rig.Until(rig.Now + 0.2);
        for (int i = 0; i < 4; i++) rig.Lick();
        Assert.IsFalse(rig.Events.Any(e => e.Kind == CorridorEventKind.Outcome));
        rig.Lick();
        var outcome = rig.Events.Single(e => e.Kind == CorridorEventKind.Outcome);
        Assert.AreEqual("Incorrect", outcome.Row.outcome);
        Assert.AreEqual("WrongLickLimit", outcome.Row.endReason);
        Assert.IsTrue(outcome.Row.punishSound);
        Assert.AreEqual(2.0, outcome.Row.punishTimeoutSeconds);

        // the next trial waits the timeout, its interval and a second without licks
        var next = rig.RunTo(CorridorEventKind.Start);
        Assert.IsTrue(next.Seconds >= outcome.Seconds + 2.0 + next.Trial.itiSeconds + 1.0 - 0.002, next.Seconds.ToString());
        rig.Until(rig.Now + 0.2);
        var track = next.Trial.trackLengthCm;
        rig.Wheel(track * 0.85);  // inside the zone
        rig.Until(rig.Now + 0.06);
        rig.Lick();
        var rewarded = rig.Events.Last(e => e.Kind == CorridorEventKind.Outcome);
        Assert.AreEqual("CorrectLick", rewarded.Row.endReason);
        Assert.AreEqual(2.0, rewarded.Row.itiStartTime - outcome.Seconds, 0.0011);
        Assert.AreEqual(next.Trial.itiSeconds, rewarded.Row.itiSeconds);
    }

    [TestMethod]
    public void TheQuietWaitRestartsWithEachLick()
    {
        var rig = new Rig(Fixture.Setup(Fixture.Task(), 4));
        rig.RunTo(CorridorEventKind.Start);  // the first trial has no interval, but a quiet second
        Assert.AreEqual(1.001, rig.Now, 1e-9);
        var r = new Rig(Fixture.Setup(Fixture.Task(), 4));
        r.Until(0.5);
        r.Lick();
        r.RunTo(CorridorEventKind.Start);
        Assert.AreEqual(1.5, r.Now, 1e-9);
    }

    [TestMethod]
    public void ATimeOutInTheZoneIsPunished()
    {
        var rig = new Rig(Fixture.Setup(Fixture.Task(), 4));
        var start = rig.RunTo(CorridorEventKind.Start);
        rig.Wheel(start.Trial.trackLengthCm * 0.85);
        var outcome = rig.RunTo(CorridorEventKind.Outcome);
        Assert.AreEqual("Timeout", outcome.Row.endReason);
        Assert.AreEqual("NoResponse", outcome.Row.outcome);
        Assert.IsTrue(outcome.Row.punishSound);
        Assert.AreEqual(start.Seconds + 0.05 + 12.0, outcome.Seconds, 0.0011);
    }

    [TestMethod]
    public void TheEndOfTheTrackEndsTheTrial()
    {
        var rig = new Rig(Fixture.Setup(Fixture.Task(), 2));
        var start = rig.RunTo(CorridorEventKind.Start);
        rig.Until(rig.Now + 0.1);
        rig.Wheel(start.Trial.trackLengthCm + 5);
        var outcome = rig.Events.Single(e => e.Kind == CorridorEventKind.Outcome);
        Assert.AreEqual("EndOfTrack", outcome.Row.endReason);
        Assert.AreEqual(start.Trial.trackLengthCm, outcome.Row.endPositionCm, 1e-9);
    }

    [TestMethod]
    public void ATruncatedTrialSilencesThePipsPastItsFraction()
    {
        var setup = Fixture.Setup(Fixture.Task(), 3);
        var rig = new Rig(setup);
        var start = rig.RunTo(CorridorEventKind.Start);
        start.Trial.truncationFraction = 0.5;
        rig.Wheel(start.Trial.trackLengthCm * 0.3);
        rig.Until(rig.Now + 0.1);
        Assert.AreEqual(1.0, rig.Events.Last(e => e.Kind == CorridorEventKind.Pip).Frame.ToneGain);
        rig.Wheel(start.Trial.trackLengthCm * 0.7);
        rig.Until(rig.Now + 0.1);
        var pip = rig.Events.Last(e => e.Kind == CorridorEventKind.Pip).Frame;
        Assert.AreEqual(0.0, pip.ToneGain);
        Assert.AreEqual(0, pip.Row.stimulusActive);
    }

    [TestMethod]
    public void AHardwareTriggeredTrialStartsOnceZapitArmedItsCondition()
    {
        var task = Fixture.Task();
        Fixture.Opto(task);
        var rig = new Rig(Fixture.Setup(task, 4));
        var arm = rig.RunTo(CorridorEventKind.Arm);
        rig.Until(rig.Now + 0.5);
        Assert.IsFalse(rig.Events.Any(e => e.Kind == CorridorEventKind.Start));
        var reply = new ZapitReply { Command = ZapitCommandType.StartStimulus, Value = arm.Trial.laserCondition, LaserOn = arm.Trial.laserOn ? 1 : 0 };
        rig.Events.AddRange(rig.State.OnZapitReply(reply));
        var start = rig.Events.Single(e => e.Kind == CorridorEventKind.Start);
        rig.Until(rig.Now + 0.2);
        Assert.AreEqual(start.Seconds + 0.1, rig.Events.First(e => e.Kind == CorridorEventKind.Pip).Seconds, 1e-9);  // laserToStimulusSeconds
        var wrong = new ZapitReply { Command = ZapitCommandType.StartStimulus, Value = 99 };
        rig.RunTo(CorridorEventKind.Arm);
        Assert.ThrowsException<InvalidOperationException>(() => rig.State.OnZapitReply(wrong).ToList());
    }

    [TestMethod]
    public void TheLaserIsCappedOnTheHarpClockOnAStageWithoutATimeLimit()
    {
        var task = Fixture.Task();
        Fixture.Opto(task);
        var rig = new Rig(Fixture.Setup(task, 2, false, "test"));
        var start = rig.RunTo(CorridorEventKind.Start);
        var cap = rig.RunTo(CorridorEventKind.LaserCap);
        Assert.AreEqual(start.Seconds + 12.5, cap.Seconds, 0.0011);
    }

    [TestMethod]
    public void AClockGoingBackwardsStopsTheTask()
    {
        var state = new CorridorState(Fixture.Setup(Fixture.Task(), 0));
        state.OnClock(10).ToList();
        Assert.ThrowsException<InvalidOperationException>(() => state.OnClock(9).ToList());
    }
}
