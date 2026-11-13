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
        Assert.IsTrue(trials.All(t => t.firstPipBeep == 2 && t.triggerBeep == 0 && t.armZapit));
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
        ok.Optogenetics.LaserToStimulusSeconds = -0.5;   // after the first pip: the first pip at Go, the trigger 10 beeps later
        var late = new TrialDraw(Fixture.Setup(ok, 4)).Next();
        Assert.IsTrue(late.firstPipBeep == 0 && late.triggerBeep == 10);
        ok.Optogenetics.LaserToStimulusSeconds = -12.05;  // past stage 4's 12 s limit
        StringAssert.Contains(Assert.ThrowsException<InvalidOperationException>(() => Fixture.Setup(ok, 4)).Message, "timeLimitSeconds");
        ok.Optogenetics.LaserToStimulusSeconds = -2.05;   // past the 2 s on a stage without one
        StringAssert.Contains(Assert.ThrowsException<InvalidOperationException>(() => Fixture.Setup(ok, 2)).Message, "without timeLimitSeconds");
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
        StringAssert.Contains(Assert.ThrowsException<InvalidOperationException>(() => Fixture.Setup(task, 1)).Message, "both above 0");
        task.TrainingStages[1].WheelQuietCm = 2;
        Fixture.Setup(task, 1);
        task.TrainingStages[1].LickQuietSeconds = -1;
        StringAssert.Contains(Assert.ThrowsException<InvalidOperationException>(() => Fixture.Setup(task, 1)).Message, "negative");
    }

    [TestMethod]
    public void ZapitIsSentEachTrialsConditionPowerAndLaserState()
    {
        var off = new CorridorTrial { trialNumber = 3, laserCondition = 2, laserOn = false, laserPowerMw = 0 };
        var on = new CorridorTrial { trialNumber = 4, laserCondition = 1, laserOn = true, laserPowerMw = 2.0 };
        var a = ZapitTrialCommand.Build(off, true);
        Assert.AreEqual(2, a.ConditionNumber);
        Assert.IsFalse(a.LaserOn);
        Assert.AreEqual(0f, a.LaserPower);
        Assert.IsTrue(a.HardwareTriggered);
        var b = ZapitTrialCommand.Build(on, false);
        Assert.IsTrue(b.LaserOn);
        Assert.AreEqual(2f, b.LaserPower);
        Assert.AreEqual(0, b.StimDuration);   // continuous: Bonsai stops it
        Assert.ThrowsException<InvalidOperationException>(() => ZapitTrialCommand.Build(new CorridorTrial(), true));
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
    public void TheFirstTrialWaitsItsIntervalAndPipsEvery50MsFromTheLead()
    {
        var rig = new Rig(Fixture.Setup(Fixture.Task(), 1));
        rig.Now = 100;
        var start = rig.RunTo(CorridorEventKind.Start);
        Assert.AreEqual(100.001 + start.Trial.itiSeconds - 0.1, start.Seconds, 1e-9);   // Go: the interval's end less the 0.1 s lead
        rig.Until(rig.Now + 1.0);
        var pips = rig.Events.Where(e => e.Kind == CorridorEventKind.Pip).Select(e => e.Seconds).ToList();
        Assert.AreEqual(100.001 + start.Trial.itiSeconds, pips[0], 0.0011);   // the first pip: exactly the interval after its start
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
        Assert.AreEqual(outcome.Seconds + next.Trial.itiSeconds - 0.1, next.Seconds, 1e-9);
        rig.Until(rig.Now + 0.2);
        var firstPip = rig.Events.First(e => e.Kind == CorridorEventKind.Pip && e.Trial == next.Trial);
        Assert.AreEqual(outcome.Seconds + next.Trial.itiSeconds, firstPip.Seconds, 0.0011);
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

        // the next trial waits the timeout and its interval; the quiet second, counted from the interval's start, adds nothing
        var next = rig.RunTo(CorridorEventKind.Start);
        Assert.AreEqual(outcome.Seconds + 2.0 + next.Trial.itiSeconds - 0.1, next.Seconds, 1e-9);
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
        var quiet = new Rig(Fixture.Setup(Fixture.Task(), 4));
        var start = quiet.RunTo(CorridorEventKind.Start);   // a quiet mouse: Go at the interval's end less the lead
        var iti = start.Trial.itiSeconds;
        Assert.AreEqual(0.001 + iti - 0.1, start.Seconds, 1e-9);
        var r = new Rig(Fixture.Setup(Fixture.Task(), 4));
        r.Until(Math.Round(0.001 + iti - 0.5, 3));
        r.Lick();                                           // licked late in the interval: Go a quiet second later
        r.Until(Math.Round(r.Now + 0.3, 3));
        r.Lick();                                           // and again
        var go = r.RunTo(CorridorEventKind.Start);
        Assert.AreEqual(Math.Round(0.001 + iti - 0.5, 3) + 0.3 + 1.0, go.Seconds, 1e-9);
        rig_quiet_wait(r, go);
    }

    static void rig_quiet_wait(Rig r, CorridorEvent go)
    {
        r.Wheel(1000);   // the end of the track ends the trial
        var outcome = r.Events.Single(e => e.Kind == CorridorEventKind.Outcome);
        Assert.AreEqual(go.Seconds - (go.Trial.itiSeconds - 0.1 + 0.001), outcome.Row.quietWaitSeconds, 1e-9);
        Assert.AreEqual(go.Seconds, outcome.Row.trialStartTime, 1e-12);
    }

    [TestMethod]
    public void TheWheelsTotalTravelRestartsTheWheelWait()
    {
        var task = Fixture.Task();
        task.TrainingStages[1].WheelQuietSeconds = 1.0;
        task.TrainingStages[1].WheelQuietCm = 1.0;
        var still = new Rig(Fixture.Setup(task, 1));
        still.Wheel(50);
        var start = still.RunTo(CorridorEventKind.Start);
        var iti = start.Trial.itiSeconds;
        Assert.AreEqual(0.001 + iti - 0.1, start.Seconds, 1e-9);
        var rock = new Rig(Fixture.Setup(task, 1));
        rock.Wheel(50);
        rock.Until(Math.Round(iti - 0.5, 3));
        rock.Wheel(50.6);                                   // 0.6 cm forward: within 1 cm
        rock.Until(Math.Round(iti - 0.3, 3));
        rock.Wheel(50.0);                                   // and back: net 0, total 1.2 cm, so the wait restarts
        var go = rock.RunTo(CorridorEventKind.Start);
        Assert.AreEqual(Math.Round(iti - 0.3, 3) + 1.0, go.Seconds, 1e-9);
    }

    [TestMethod]
    public void ZapitAddsNoTimeAndTheTriggerIsSignedFromTheFirstPip()
    {
        var plain = new Rig(Fixture.Setup(Fixture.Task(), 1)).RunTo(CorridorEventKind.Start);
        foreach (var lead in new[] { 0.1, 0.0, -0.5 })
        {
            var task = Fixture.Task();
            Fixture.Opto(task);
            task.Optogenetics.LaserToStimulusSeconds = lead;
            var rig = new Rig(Fixture.Setup(task, 1));
            var arm = rig.RunTo(CorridorEventKind.Arm);
            Assert.AreEqual(0.001, arm.Seconds, 1e-9);       // armed at the interval's start
            rig.Events.AddRange(rig.State.OnZapitReply(new ZapitReply { Command = ZapitCommandType.StartStimulus, Value = arm.Trial.laserCondition, LaserOn = arm.Trial.laserOn ? 1 : 0 }));
            var start = rig.RunTo(CorridorEventKind.Start);
            Assert.AreEqual(0.001 + start.Trial.itiSeconds - Math.Max(lead, 0), start.Seconds, 1e-9);
            rig.Until(rig.Now + 0.7);
            var pip = rig.Events.First(e => e.Kind == CorridorEventKind.Pip).Seconds;
            var trigger = rig.Events.Single(e => e.Kind == CorridorEventKind.Trigger).Seconds;
            Assert.AreEqual(0.001 + start.Trial.itiSeconds, pip, 0.0011, "the first pip: the interval after its start, Zapit or not");
            Assert.AreEqual(pip - lead, trigger, 0.0011, "the trigger: the first pip less laserToStimulusSeconds");
            if (lead == 0.1) Assert.AreEqual(plain.Seconds, start.Seconds, 1e-9, "the same start as without optogenetics");
        }
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
        Assert.AreEqual(start.Seconds + 0.1 + 12.0, outcome.Seconds, 0.0011);
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
    public void AZapitReplyNotInByTheTriggerStopsTheTask()
    {
        var task = Fixture.Task();
        Fixture.Opto(task);
        var rig = new Rig(Fixture.Setup(task, 4));
        rig.RunTo(CorridorEventKind.Arm);
        var error = Assert.ThrowsException<InvalidOperationException>(() => rig.RunTo(CorridorEventKind.Trigger));
        StringAssert.Contains(error.Message, "had not answered trial 1's StartStimulus by its trigger");
        var wrong = new Rig(Fixture.Setup(task, 4));
        wrong.RunTo(CorridorEventKind.Arm);
        Assert.ThrowsException<InvalidOperationException>(() => wrong.State.OnZapitReply(new ZapitReply { Command = ZapitCommandType.StartStimulus, Value = 99 }).ToList());
    }

    [TestMethod]
    public void TheLaserIsCappedOnTheHarpClockOnAStageWithoutATimeLimit()
    {
        var task = Fixture.Task();
        Fixture.Opto(task);
        var rig = new Rig(Fixture.Setup(task, 2, false, "test"));
        var start = rig.RunTo(CorridorEventKind.Start);
        var cap = rig.RunTo(CorridorEventKind.LaserCap);
        Assert.AreEqual(start.Seconds + 12.5, cap.Seconds, 0.0011);   // from the trigger, at Go with the 0.1 s lead
    }

    [TestMethod]
    public void AClockGoingBackwardsStopsTheTask()
    {
        var state = new CorridorState(Fixture.Setup(Fixture.Task(), 0));
        state.OnClock(10).ToList();
        Assert.ThrowsException<InvalidOperationException>(() => state.OnClock(9).ToList());
    }

    [TestMethod]
    public void Stage0RunningAt40CmPerSecondPastTheCeilingIsStillRewarded()
    {
        // 6 s of drift, then 2 cm a beep: the beep that crosses the floor is above the ceiling (the sweep's end, 25000 Hz).
        var rig = new Rig(Fixture.Setup(Fixture.Task(), 0));
        rig.RunTo(CorridorEventKind.Start);
        rig.Until(rig.Now + 6.0);
        double cm = 0;
        CorridorEvent outcome = null;
        for (int i = 0; i < 200 && outcome == null; i++)
        {
            cm += 2.0;
            rig.Wheel(cm);
            var seen = rig.Events.Count;
            rig.Until(rig.Now + 0.05);
            outcome = rig.Events.Skip(seen).FirstOrDefault(e => e.Kind == CorridorEventKind.Outcome);
        }
        Assert.AreEqual("FreqReached", outcome.Row.endReason);
        Assert.IsTrue(outcome.Row.toneEndFrequencyHz > 25000, outcome.Row.toneEndFrequencyHz.ToString());
        Assert.IsTrue(outcome.Rewarded);
        Assert.AreEqual("Correct", outcome.Row.outcome);
        Assert.IsFalse(outcome.Row.punishSound);
    }

    [TestMethod]
    public void AfterAGapInTheHarpClockOnlyTheLatestBeepPips()
    {
        var rig = new Rig(Fixture.Setup(Fixture.Task(), 3));
        var start = rig.RunTo(CorridorEventKind.Start);
        rig.Until(rig.Now + 0.2);
        var seen = rig.Events.Count;
        rig.Now = Math.Round(rig.Now + 0.2, 6);
        rig.Events.AddRange(rig.State.OnClock(rig.Now));
        Assert.AreEqual(1, rig.Events.Skip(seen).Count(e => e.Kind == CorridorEventKind.Pip));
        rig.Until(rig.Now + 0.2);
        var pips = rig.Events.Where(e => e.Kind == CorridorEventKind.Pip).Select(e => e.Seconds).ToList();
        for (int i = 1; i < pips.Count; i++) Assert.IsTrue(pips[i] - pips[i - 1] > 0.049, pips[i].ToString());
        Assert.AreEqual(start.Seconds + 0.6, pips.Last(), 0.0011);   // back on the 50 ms grid
    }

    [TestMethod]
    public void TheTriggerLineIsHeldOnlyInAnOptogeneticsExperiment()
    {
        var off = new TrialDraw(Fixture.Setup(Fixture.Task(), 3)).Next();
        Assert.IsFalse(off.optogenetics);
        Assert.AreEqual(0.0, off.triggerHoldSeconds);
        var task = Fixture.Task();
        Fixture.Opto(task);
        var on = new TrialDraw(Fixture.Setup(task, 3)).Next();
        Assert.IsTrue(on.optogenetics);
        Assert.AreEqual(1.0, on.triggerHoldSeconds);
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
