using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenRigs.Devices;
using OpenRigs.Trials;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using UclOpenCoenDomeNavigationTask;
using TrialEventOf = OpenRigs.Trials.TrialEvent<Trial, StimulusFrame, TrialOutcome>;

/// <summary>The spouts block (OpenRigs.Trials Spouts, SpoutCommands) and the stimulus block (StimulusTiming, the StimulusOff
/// event and AfterOutcome) on the trial loop.</summary>
[TestClass]
public class SpoutAndStimulusOffTests
{
    // Each trial on stage 1 (auto-reward, no auto-progress): the wheel run to 95% of its track after the first pip, so the
    // pip reaching the zone rewards it. Returns each trial's outcome event.
    static List<TrialEventOf> RunRewarded(Rig rig, int count, ref double wheel)
    {
        var outcomes = new List<TrialEventOf>();
        for (int i = 0; i < count; i++)
        {
            var start = rig.RunTo(TrialEventKind.Start);
            rig.RunTo(TrialEventKind.Stimulus);
            wheel += start.Trial.trackLengthWheelCm * 0.95;
            rig.Wheel(wheel);
            outcomes.Add(rig.RunTo(TrialEventKind.Outcome));
        }
        return outcomes;
    }

    // Stage 1 with the grating shown too: the task file's audiovisual condition (its third, no set) drawn alone.
    static UclOpenCoenDomeNavigationTaskParameters Audiovisual(int stage = 1)
    {
        var task = Fixture.Task(stage);
        for (int i = 0; i < task.TrialConditions.Count; i++) task.TrialConditions[i].Weight = i == 2 ? 1 : 0;
        return task;
    }

    static List<LickSpoutCommand> Moves(Rig rig)
    {
        return new SpoutCommands().Process(rig.Events.ToObservable()).ToList().Wait().ToList();
    }

    static List<TrialEventOf> UpdatesAfter(Rig rig, TrialEventOf outcome)
    {
        return rig.Events.Where(e => e.Kind == TrialEventKind.Update && e.Trial == outcome.Trial && e.Seconds > outcome.Seconds + 1e-9).ToList();
    }

    [TestMethod]
    public void AlwaysInSpoutsLeadNothingAndMoveNothing()
    {
        // The task file: spouts always in, stimulusToSpoutsInSeconds -0.5, which an always-in trial ignores (Spouts.Cues): the
        // first pip the laser's 0.1 s lead (2 steps, in every experiment) after the trial start, and the only command is each SpoutsIn cue's SpoutsIn, at the first pip, where Ready put the spouts.
        var task = Fixture.AsWritten(1);
        Assert.IsTrue(task.Spouts.AlwaysIn);
        Assert.AreEqual(-0.5, task.Spouts.StimulusToSpoutsInSeconds);
        var rig = new Rig(Fixture.Setup(task));
        var wheel = 0.0;
        RunRewarded(rig, 3, ref wheel);
        foreach (var start in rig.Events.Where(e => e.Kind == TrialEventKind.Start))
        {
            Assert.AreEqual(2, TrialRules.FirstPipStep(start.Trial), "the laser's lead alone");
            var pip = rig.Events.First(e => e.Kind == TrialEventKind.Update && e.Trial == start.Trial);
            Assert.AreEqual(start.Seconds + 0.1, pip.Seconds, 0.0011);
            var cue = rig.Events.Single(e => e.Kind == TrialEventKind.Cue && e.Cue == Spouts.InCue && e.Trial == start.Trial);
            Assert.AreEqual(pip.Seconds, cue.Seconds, 0.0011);
        }
        CollectionAssert.AreEqual(Enumerable.Repeat(LickSpoutCommand.SpoutsIn, 3).ToList(), Moves(rig));
    }

    [TestMethod]
    public void WithTheSpoutsMovingTheLeftComesInAloneAndGoesOutAfterTheReward()
    {
        // Moving spouts lead the first pip by 0.5 s (10 steps); the next interval starts at the reward's outcome and the spouts
        // go out spouts.rewardConsumptionSeconds (1.5 s) into it.
        var task = Fixture.AsWritten(1);
        task.Spouts.AlwaysIn = false;
        var rig = new Rig(Fixture.Setup(task));
        var wheel = 0.0;
        var outcomes = RunRewarded(rig, 2, ref wheel);
        Assert.AreEqual(10, TrialRules.FirstPipStep(outcomes[0].Trial));
        var iti = rig.Events.Where(e => e.Kind == TrialEventKind.Iti).ElementAt(1);
        Assert.AreEqual(outcomes[0].Seconds, iti.Seconds, 0.0011);
        var outCue = rig.Events.First(e => e.Kind == TrialEventKind.Cue && e.Cue == Spouts.OutCue);
        Assert.AreEqual(iti.Seconds + 1.5, outCue.Seconds, 0.0011);
        CollectionAssert.AreEqual(new[]
        {
            LickSpoutCommand.SpoutsOut, LickSpoutCommand.LeftOnly, LickSpoutCommand.LeftOnly,   // trial 1: out, in, kept at the reward
            LickSpoutCommand.SpoutsOut, LickSpoutCommand.LeftOnly, LickSpoutCommand.LeftOnly,   // out at trial 2's interval start, then as trial 1
        }, Moves(rig));
    }

    [TestMethod]
    public void SpoutsInAfterTheWindowOpensOffTheGridOrPastTheIntervalAreRefused()
    {
        Func<Action<UclOpenCoenDomeNavigationTaskParameters>, string> refused = change =>
        {
            var task = Fixture.AsWritten(1);
            change(task);
            return Assert.ThrowsException<InvalidOperationException>(() => Fixture.Setup(task)).Message;
        };
        StringAssert.Contains(refused(t => t.Spouts.StimulusToSpoutsInSeconds = 0.1), "the response window would open before the spouts arrive");
        StringAssert.Contains(refused(t => t.Spouts.StimulusToSpoutsInSeconds = -0.07), "spouts.stimulusToSpoutsInSeconds must be a whole number of 0.05 s steps when negative");
        // spouts.rewardConsumptionSeconds (1.5) plus the lead within itiMinSeconds (2.5), and the consumption alone within it.
        StringAssert.Contains(refused(t => { t.Spouts.AlwaysIn = false; t.Spouts.StimulusToSpoutsInSeconds = -1.5; }), "the spouts would come in before they are drawn out");
        StringAssert.Contains(refused(t => t.Spouts.RewardConsumptionSeconds = 3), "spouts.rewardConsumptionSeconds (3 s) is above itiMinSeconds");
        // The off at most the shortest outcome to next interval (0: every trial can be rewarded) plus itiMinSeconds (2.5).
        Fixture.Setup(Change(Fixture.AsWritten(1), t => t.Stimulus.ResponseToStimulusOffSeconds = 2.5));
        StringAssert.Contains(refused(t => t.Stimulus.ResponseToStimulusOffSeconds = 2.6), "stimulus.responseToStimulusOffSeconds");
        StringAssert.Contains(refused(t => t.Stimulus.ResponseToStimulusOffSeconds = null), "would never end");
    }

    static UclOpenCoenDomeNavigationTaskParameters Change(UclOpenCoenDomeNavigationTaskParameters task, Action<UclOpenCoenDomeNavigationTaskParameters> change)
    {
        change(task);
        return task;
    }

    [TestMethod]
    public void LicksCountFromTheResponseWindowsOpeningAndASpoutMayComeInUpToIt()
    {
        // stimulusToResponseWindowSeconds 0.5 on stage 4 (wrong licks end the trial at 5): licks in the first 0.5 s after the
        // first pip are not counted; from then they are, and the 12 s window runs from that opening.
        var task = Fixture.Task(4);
        task.TrialStructure.StimulusToResponseWindowSeconds = 0.5;
        task.Spouts.StimulusToSpoutsInSeconds = 0.5;   // a positive spouts-in up to the opening
        var rules = Fixture.Setup(task);
        var rig = new Rig(rules);
        var start = rig.RunTo(TrialEventKind.Start);
        Assert.AreEqual(12.5, rules.Timing(start.Trial).ResponseDelaySeconds + rules.Timing(start.Trial).ResponseWindowSeconds, 1e-9);
        var onset = rig.RunTo(TrialEventKind.Stimulus);
        for (int i = 0; i < 6; i++) { rig.Lick(); rig.Until(rig.Now + 0.05); }   // all before 0.5 s
        Assert.IsFalse(rig.Events.Any(e => e.Kind == TrialEventKind.Outcome), "no lick counted before the opening");
        rig.Until(onset.Seconds + 0.55);
        for (int i = 0; i < 5; i++) { rig.Lick(); rig.Until(rig.Now + 0.01); }
        Assert.AreEqual("wrongLickLimit", rig.Events.Single(e => e.Kind == TrialEventKind.Outcome).Outcome.Row.Value.outcomeReason);
        task.Spouts.StimulusToSpoutsInSeconds = 0.55;   // after the opening: refused (lib's Spouts.Problems)
        StringAssert.Contains(Assert.ThrowsException<InvalidOperationException>(() => Fixture.Setup(task)).Message, "the response window would open before the spouts arrive");
    }

    [TestMethod]
    public void ByDefaultTheStimulusGoesOffAtTheOutcomeAndTheGratingHidesThen()
    {
        var rig = new Rig(Fixture.Setup(Audiovisual()));
        var wheel = 0.0;
        var outcome = RunRewarded(rig, 1, ref wheel)[0];
        rig.Until(rig.Now + 1);
        var off = rig.Events.Single(e => e.Kind == TrialEventKind.StimulusOff);
        Assert.AreEqual(outcome.Seconds, off.Seconds);
        Assert.AreEqual(0, UpdatesAfter(rig, outcome).Count);
        Assert.AreEqual(outcome.Outcome.Row.Value.outcomeTime, outcome.Outcome.Row.Value.stimulusOffTime);
        var gratings = new GratingStates().Process(rig.Events.ToObservable()).ToList().Wait();
        Assert.AreEqual(0.0, gratings.Last().Opacity);
        Assert.AreEqual(1, gratings.Count(g => g.Opacity == 0), "hidden once, at the StimulusOff");
    }

    [TestMethod]
    public void AfterTheOutcomeTheStimulusFollowsTheWheelHeldAtTheTracksEndUntilItsOff()
    {
        var task = Audiovisual();
        task.Stimulus.ResponseToStimulusOffSeconds = 1.0;
        var rig = new Rig(Fixture.Setup(task));
        var wheel = 0.0;
        var outcome = RunRewarded(rig, 1, ref wheel)[0];
        var trial = outcome.Trial;
        Assert.AreEqual(outcome.Seconds + 1.0, outcome.Outcome.Row.Value.stimulusOffTime, 1e-9);
        rig.Until(rig.Now + 0.3);
        rig.Wheel(wheel + trial.trackLengthWheelCm);   // far past the track's end
        rig.Until(outcome.Seconds + 1.5);
        var off = rig.Events.Single(e => e.Kind == TrialEventKind.StimulusOff && e.Trial == trial);
        Assert.AreEqual(outcome.Seconds + 1.0, off.Seconds, 0.0011);
        Assert.AreEqual(1, rig.Events.Count(e => e.Kind == TrialEventKind.Outcome), "the wheel never ends the trial again");
        var after = UpdatesAfter(rig, outcome);
        Assert.IsTrue(after.Count >= 15 && after.All(e => e.Seconds <= off.Seconds), "a pip every 50 ms until the off, none after");
        Assert.IsTrue(after.All(e => e.Update.Row.auditoryLevelDbfs == trial.auditoryInitialLevelDbfs && e.Update.Grating.Opacity == 1));
        Assert.AreEqual(trial.TrackLengthCm, after.Last().Update.Row.positionCm, 1e-9, "held at the track's end");
        Assert.IsTrue(after.First().Update.Row.positionCm < trial.TrackLengthCm);
        var gratings = new GratingStates().Process(rig.Events.ToObservable()).ToList().Wait();
        Assert.AreEqual(0.0, gratings.Last().Opacity);
    }

    [TestMethod]
    public void ACutStimulusStaysOffAfterTheOutcomeAndItsCutIsTheEarlierOff()
    {
        var task = Audiovisual();
        task.Stimulus.ResponseToStimulusOffSeconds = 1.0;
        var rig = new Rig(Fixture.Setup(task));
        var start = rig.RunTo(TrialEventKind.Start);
        start.Trial.cutoffFraction = 0.5;
        start.Trial.auditoryCutoff = true;
        start.Trial.visualCutoff = true;
        rig.RunTo(TrialEventKind.Stimulus);
        rig.Wheel(start.Trial.trackLengthWheelCm * 0.95);
        var outcome = rig.RunTo(TrialEventKind.Outcome);
        var row = outcome.Outcome.Row.Value;
        Assert.IsTrue(row.stimulusOffTime < row.outcomeTime, "the cut, before the outcome's off");
        rig.Wheel(start.Trial.trackLengthWheelCm * 0.1);   // back below the cut point
        rig.Until(outcome.Seconds + 0.5);
        var after = UpdatesAfter(rig, outcome);
        Assert.IsTrue(after.Count > 0 && after.Last().Update.Row.positionCm < start.Trial.TrackLengthCm * 0.5);
        Assert.IsTrue(after.All(e => double.IsNaN(e.Update.Row.auditoryLevelDbfs) && e.Update.Row.visualContrast == 0 && e.Update.AuditoryGain == 0));
    }

    [TestMethod]
    public void AStimulusDurationEndsTheStimulusBeforeTheOutcome()
    {
        var task = Audiovisual();
        task.Stimulus.DurationSeconds = 0.3;
        task.Stimulus.ResponseToStimulusOffSeconds = null;
        var rig = new Rig(Fixture.Setup(task));
        var start = rig.RunTo(TrialEventKind.Start);
        var onset = rig.RunTo(TrialEventKind.Stimulus);
        rig.Wheel(start.Trial.trackLengthWheelCm * 0.3);
        rig.Until(onset.Seconds + 0.5);
        var off = rig.Events.Single(e => e.Kind == TrialEventKind.StimulusOff);
        Assert.AreEqual(onset.Seconds + 0.3, off.Seconds, 0.0011);
        var pips = rig.Events.Where(e => e.Kind == TrialEventKind.Update).ToList();
        Assert.IsTrue(pips.Where(e => e.Seconds < off.Seconds - 1e-9).All(e => !double.IsNaN(e.Update.Row.auditoryLevelDbfs)));
        var silent = pips.Where(e => e.Seconds >= off.Seconds - 1e-9).ToList();
        Assert.IsTrue(silent.Count >= 3 && silent.All(e => double.IsNaN(e.Update.Row.auditoryLevelDbfs) && e.Update.Grating.Opacity == 0));
        rig.Wheel(start.Trial.trackLengthWheelCm * 0.95);
        var row = rig.RunTo(TrialEventKind.Outcome).Outcome.Row.Value;
        Assert.AreEqual(off.Seconds, row.stimulusOffTime, 0.0011);
        Assert.IsTrue(double.IsNaN(row.auditoryCutoffPositionCm), "a timed off is no cutoff");
    }
}
