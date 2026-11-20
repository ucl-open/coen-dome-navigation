using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenRigs.Core;
using OpenRigs.Trials;
using OpenRigs.Devices.Rigs;
using OpenRigs.Devices.Zapit;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reactive.Linq;
using System.Runtime.CompilerServices;
using UclOpenCoenDomeNavigationTask;
using YamlDotNet.Serialization;
using BuiltTrialCondition = OpenRigs.Trials.BuiltTrialCondition<UclOpenCoenDomeNavigationTask.UclOpenCoenDomeNavigationTaskParameters>;
using TrialEventOf = OpenRigs.Trials.TrialEvent<Trial, StimulusFrame, TrialOutcome>;

static class Fixture
{
    public static string Repo([CallerFilePath] string here = "")
    {
        return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here), "..", ".."));
    }

    /// <summary>The task file as written (its curriculum empty), seeded.</summary>
    public static UclOpenCoenDomeNavigationTaskParameters FromFile()
    {
        var text = File.ReadAllText(Path.Combine(Repo(), "config", "task-abstract-corridor.yml"));
        var task = new DeserializerBuilder().Build().Deserialize<UclOpenCoenDomeNavigationTaskLogic>(text).TaskParameters;
        task.RngSeed = 1;
        // The spouts' lead set to 0, so the trial start is the laser's lead before the first pip, as these tests time it;
        // SpoutAndStimulusOffTests times the file's spouts lead.
        task.Spouts.StimulusToSpoutsInSeconds = 0;
        return task;
    }

    /// <summary>The task file as written, its spouts' lead too.</summary>
    public static UclOpenCoenDomeNavigationTaskParameters AsWritten(int stage = 0)
    {
        var text = File.ReadAllText(Path.Combine(Repo(), "config", "task-abstract-corridor.yml"));
        var task = new DeserializerBuilder().Build().Deserialize<UclOpenCoenDomeNavigationTaskLogic>(text).TaskParameters;
        task.RngSeed = 1;
        return Stage(task, stage);
    }

    /// <summary>The task file as app-rig-control writes it for a curriculum stage: that stage's values over the file's, and
    /// curriculum naming it.</summary>
    public static UclOpenCoenDomeNavigationTaskParameters Task(int stage = 0)
    {
        return Stage(FromFile(), stage);
    }

    // The stages of the lab's curriculum (K:\config\curricula\coen-dome-navigation\task-abstract-corridor.conditions.yml)
    // when the rows left the task file, by id: rewardZoneMinHz, rewardZoneMaxHz, autoRewardProbability, wrongLickLimit,
    // trialStructure.lickQuietSeconds, trialStructure.responseWindowSeconds, track.wheelGain, track.autoProgressPerSecond.
    // Stage 5's cutoff trials are a second condition (weight 16, stimulusCutoff.stimuli both, trackFractions 0.4 and 0.6)
    // beside the first (84).
    static readonly double?[][] Stages =
    {
        new double?[] { 18000, 24000, 1, 0, 0, null, 8, 0.04 },  // Zone Reward, Drift
        new double?[] { 18000, 24000, 1, 0, 0, null, 8, 0 },     // Zone Reward
        new double?[] { 15248, 24000, 0, 0, 0, null, 1, 0 },     // Lick to Report
        new double?[] { 15248, 21248, 0, 8, 1, 12, 1, 0 },       // Ceiling In
        new double?[] { 15248, 21248, 0, 5, 1, 12, 1, 0 },       // Fewer Wrong Licks
        new double?[] { 15248, 21248, 0, 5, 1, 12, 1, 0 },       // Stimulus Cutoff
    };

    /// <summary>The max ramp-down in the stand-in Zapit conditions file, s.</summary>
    public const double RampDown = 0.5;

    /// <summary>The task with a curriculum stage's values written over it, as app-rig-control does at launch.</summary>
    public static UclOpenCoenDomeNavigationTaskParameters Stage(UclOpenCoenDomeNavigationTaskParameters task, int stage)
    {
        var v = Stages[stage];
        task.RewardZoneMinHz = v[0].Value;
        task.RewardZoneMaxHz = v[1].Value;
        task.AutoRewardProbability = v[2].Value;
        task.WrongLickLimit = (int)v[3].Value;
        task.TrialStructure.LickQuietSeconds = v[4].Value;
        task.TrialStructure.ResponseWindowSeconds = v[5];
        task.Track.WheelGain = v[6].Value;
        task.Track.AutoProgressPerSecond = v[7].Value;
        if (stage == 5)
        {
            Func<Dictionary<object, object>> none = () => new Dictionary<object, object> { { "visualStimulus.contrasts", new List<object> { 0.0 } } };
            var off = none();
            off["stimulusCutoff.stimuli"] = "both";
            off["stimulusCutoff.trackFractions"] = new List<object> { 0.4, 0.6 };
            task.TrialConditions = new List<TrialCondition>
            {
                new TrialCondition { Weight = 84, Set = none() },
                new TrialCondition { Weight = 16, Set = off },
            };
        }
        task.Curriculum = Curriculum(stage);
        return task;
    }

    /// <summary>A well formed curriculum for a stage (id, code S0nA, a fixed version, path A).</summary>
    public static Curriculum Curriculum(int id)
    {
        return new Curriculum { Id = id, Code = "S0" + id + "A", Version = "k7f2", Path = "A", Label = "stage " + id, Sha256 = "test" };
    }

    public static Sweep Sweep()
    {
        return global::Sweep.Load(Path.Combine(Repo(), "src", "sounds", "sweep.json"));
    }

    /// <summary>The task's text as a task file has it (TaskText, which the rules read): a condition made with no set
    /// written with an empty one.</summary>
    public static string Text(UclOpenCoenDomeNavigationTaskParameters task)
    {
        foreach (var c in task.TrialConditions.Where(c => c.Set == null)) c.Set = new Dictionary<string, object>();
        return TaskYaml.Text(new UclOpenCoenDomeNavigationTaskLogic { TaskParameters = task });
    }

    public static TrialRules Setup(UclOpenCoenDomeNavigationTaskParameters task, bool hardware = true)
    {
        return Setup(Text(task), hardware);
    }

    public static TrialRules Setup(string taskText, bool hardware = true)
    {
        return TrialRules.Create(taskText, Sweep(), hardware, path => new ZapitConditionsFile(4, RampDown), Speakers());
    }

    public static TrialDraw Draw(UclOpenCoenDomeNavigationTaskParameters task, bool speakers = true)
    {
        return Draw(Text(task), speakers);
    }

    public static TrialDraw Draw(string taskText, bool speakers = true)
    {
        return new TrialDraw(taskText, Sweep(), path => new ZapitConditionsFile(4, RampDown), speakers ? Speakers() : null);
    }

    /// <summary>rig.yml's speakerArray: left (-90 deg, channel 0), right (90, channel 1) and centre (0, channel 2), all at
    /// elevation 15.</summary>
    public static EqualisedSpeakerArray Speakers()
    {
        Func<double, int, Speaker> at = (azimuth, channel) => new Speaker { Position = new SphericalPosition { Azimuth = azimuth, Elevation = 15 }, Channel = channel };
        return new EqualisedSpeakerArray { Speakers = new Dictionary<string, Speaker> { { "left", at(-90, 0) }, { "right", at(90, 1) }, { "centre", at(0, 2) } } };
    }

    public static void Opto(UclOpenCoenDomeNavigationTaskParameters task)
    {
        task.Optogenetics.Enabled = true;
        task.Optogenetics.ZapitConditionsPath = "conditions.yml";
        task.Optogenetics.LaserPowerMw = 2.0;
    }
}

/// <summary>Runs lib-open-rigs' TrialLoop with the task's rules on a stand-in Harp clock, 1 ms a tick, the wheel,
/// both spouts and Zapit's replies put in by the test (as RunTrials feeds it).</summary>
class Rig
{
    public readonly TrialLoop<Trial, StimulusFrame, TrialOutcome> Loop;
    public readonly List<TrialEventOf> Events = new List<TrialEventOf>();
    public double Now;

    public Rig(TrialRules rules)
    {
        Loop = new TrialLoop<Trial, StimulusFrame, TrialOutcome>(rules, true);
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
    public void Touch(bool touched, int spout = 0) { Events.AddRange(Loop.OnLick(spout, touched)); }
    public void Lick(int spout = 0) { Touch(true, spout); Touch(false, spout); }
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
    public void TheRulesAreReadOnlyAndTheDrawChecksTheTask()
    {
        // As coen-dome-decision-making's, no public mutable fields, the checks in TrialDraw.CheckTask.
        Assert.AreEqual(0, typeof(TrialRules).GetFields(BindingFlags.Public | BindingFlags.Instance).Length);
        UclOpenCoenDomeNavigationTaskParameters read;
        TrialDraw.CheckTask(Fixture.Text(Fixture.Task(2)), Fixture.Sweep(), Fixture.Speakers(), out read);
    }

    [TestMethod]
    public void TheCurriculumIsTheTaskFilesOnlyRecordOfTheStage()
    {
        // Go refuses a task file app-rig-control has not filled (the repo's, its curriculum empty) or filled malformed.
        StringAssert.Contains(Assert.ThrowsException<InvalidOperationException>(() => Fixture.Setup(Fixture.FromFile())).Message, "The task file's curriculum is empty");
        foreach (var field in new[] { "code", "version", "path", "label", "sha256", "id" })
        {
            var task = Fixture.Task(0);
            switch (field)
            {
                case "code": task.Curriculum.Code = "S3A"; break;
                case "version": task.Curriculum.Version = "k7f1"; break;
                case "path": task.Curriculum.Path = "a"; break;
                case "label": task.Curriculum.Label = ""; break;
                case "sha256": task.Curriculum.Sha256 = null; break;
                case "id": task.Curriculum.Id = -1; break;
            }
            var message = Assert.ThrowsException<InvalidOperationException>(() => Fixture.Setup(task)).Message;
            StringAssert.Contains(message, "The task file's curriculum is malformed (");
            StringAssert.Contains(message, field == "id" ? "id -1" : field);
        }
        var big = Fixture.Task(0);
        big.Curriculum.Id = 42;   // an id past the old rows: the curriculum's ids are never reused
        Assert.AreEqual("S00A-k7f2", Fixture.Setup(big).CurriculumStage);
    }

    [TestMethod]
    public void ARigWithoutAZapitBlockMakesTheRulesWhenTheLaserIsOff()
    {
        var text = Fixture.Text(Fixture.Task());
        var rig = new LabRig { SpeakerArray = Fixture.Speakers() };   // rig.yml with no zapit block: Zapit is null
        Assert.IsNull(rig.Zapit);
        var rules = new CheckTaskAtGo().Process(Observable.Return(Tuple.Create(text, rig, Fixture.Sweep()))).Wait();
        Assert.IsNull(rules.Laser);
    }

    [TestMethod]
    public void EmptyTrackLengthsAreRefused()
    {
        var task = Fixture.Task(0);
        task.Track.LengthsCm.Clear();
        StringAssert.Contains(Assert.ThrowsException<InvalidOperationException>(() => Fixture.Setup(task)).Message, "track.lengthsCm");
    }

    [TestMethod]
    public void TheSameSeedDrawsTheSameTrials()
    {
        var a = Fixture.Draw(Fixture.Task(5));
        var b = Fixture.Draw(Fixture.Task(5));
        for (int i = 0; i < 50; i++)
        {
            var x = a.Next();
            var y = b.Next();
            Assert.AreEqual(x.TrackLengthCm, y.TrackLengthCm);
            Assert.AreEqual(x.cutoffFraction, y.cutoffFraction);
            Assert.AreEqual(x.itiSeconds, y.itiSeconds);
            Assert.AreEqual(x.trialType, y.trialType);
        }
    }

    static readonly string[] Labels = { "visual", "auditory", "audiovisualCoherent" };

    /// <summary>What a label's condition sets in the task file: visual the tone off, auditory the grating off, audiovisualCoherent nothing.</summary>
    static Dictionary<object, object> Shows(string label)
    {
        if (label == "visual") return new Dictionary<object, object> { { "auditoryStimulus.levelDbfs", "mute" } };
        if (label == "auditory") return new Dictionary<object, object> { { "visualStimulus.contrasts", new List<object> { 0.0 } } };
        return new Dictionary<object, object>();
    }

    /// <summary>One condition per label, as the task file's (visual, auditory, audiovisualCoherent), with these weights.</summary>
    static List<TrialCondition> Conditions(int visual, int auditory, int coherent)
    {
        return new[] { visual, auditory, coherent }.Select((w, i) => new TrialCondition { Weight = w, Set = Shows(Labels[i]) }).ToList();
    }

    [TestMethod]
    public void TheWeightsAreHonouredAndEachTrialLogsItsCondition()
    {
        // Two conditions of one type are told apart by trialConditionId, their index; a seeded run follows the weights.
        var task = Fixture.Task(3);
        task.TrialConditions = Conditions(1, 2, 0);
        task.TrialConditions.Add(new TrialCondition { Weight = 5, Set = Shows("visual") });
        var draw = Fixture.Draw(task);
        var trials = Enumerable.Range(0, 4000).Select(i => draw.Next()).ToList();
        var weights = new[] { 1, 2, 0, 5 };
        for (int i = 0; i < weights.Length; i++)
        {
            Assert.AreEqual(weights[i] / 8.0, trials.Count(t => t.trialConditionId == i) / 4000.0, 0.02, "condition " + i);
        }
        // Each trial's trialType is its condition's, derived: the labels', then visual's again.
        var names = new[] { "visual", "auditory", "audiovisualCoherent", "visual" };
        Assert.IsTrue(trials.All(t => t.trialType == names[t.trialConditionId]));
        task.TrialConditions = Conditions(0, 0, 0);
        task.TrialConditions.Add(new TrialCondition { Weight = 1 });
        var rig = new Rig(Fixture.Setup(task));
        rig.RunTo(TrialEventKind.Start);
        rig.Wheel(1000);
        Assert.AreEqual(3, rig.RunTo(TrialEventKind.Outcome).Outcome.Row.Value.trialConditionId, "the row logs it");
    }

    [TestMethod]
    public void EachRowLogsTheConditionItsTrialStartedWith()
    {
        // A running loop over mixed conditions: each Outcome row's trialConditionId and trialType are its Start's, never
        // the next trial's (the loop draws ahead). Rows are matched by trialNumber: Wheel can end a trial in its own call.
        var task = Fixture.Task(3);
        task.TrialConditions = Conditions(1, 2, 1);
        task.TrialConditions.Add(new TrialCondition { Weight = 3 });
        var rig = new Rig(Fixture.Setup(task));
        // The labels' names, then audiovisualCoherent: a condition with no set shows the task's grating and tone.
        var names = new[] { "visual", "auditory", "audiovisualCoherent", "audiovisualCoherent" };
        var seen = new HashSet<int>();
        for (int i = 0; i < 30; i++)
        {
            var start = rig.RunTo(TrialEventKind.Start).Trial;
            int from = rig.Events.Count, at = -1;
            Func<bool> ended = () =>
            {
                for (; from < rig.Events.Count; from++)
                    if (rig.Events[from].Kind == TrialEventKind.Outcome && rig.Events[from].Outcome.Row.Value.trialNumber == start.trialNumber) at = from;
                return at >= 0;
            };
            rig.Wheel(1000);
            if (!ended()) rig.Until(rig.Now + 60, ended);
            var row = rig.Events[at].Outcome.Row.Value;
            Assert.AreEqual(start.trialConditionId, row.trialConditionId, "trial " + start.trialNumber);
            Assert.AreEqual(names[row.trialConditionId], row.trialType, "trial " + start.trialNumber);
            seen.Add(row.trialConditionId);
        }
        CollectionAssert.AreEquivalent(new[] { 0, 1, 2, 3 }, seen.ToArray());
    }

    [TestMethod]
    public void EachTrialsTypeIsDrawnFromTheTasksWeights()
    {
        // The task file: auditory alone, as before trialConditions
        var task = Fixture.Task(3);
        var file = Fixture.Draw(task);
        for (int i = 0; i < 100; i++) Assert.AreEqual("auditory", file.Next().trialType);
        task.TrialConditions = Conditions(0, 1, 3);   // weights edited on Setup
        var draw = Fixture.Draw(task);
        var types = Enumerable.Range(0, 4000).Select(i => draw.Next().trialType).ToList();
        Assert.AreEqual(0, types.Count(t => t == "visual"));   // weight 0: never
        Assert.AreEqual(0.75, types.Count(t => t == "audiovisualCoherent") / 4000.0, 0.03);
        Assert.AreEqual(1000, types.Count(t => t == "auditory"), 120);
        var again = Fixture.Draw(task);   // the same seed: the same types
        CollectionAssert.AreEqual(types, Enumerable.Range(0, 4000).Select(i => again.Next().trialType).ToList());
        task.RngSeed = 2;
        var other = Fixture.Draw(task);
        CollectionAssert.AreNotEqual(types, Enumerable.Range(0, 4000).Select(i => other.Next().trialType).ToList());
    }

    [TestMethod]
    public void ConditionsAllZeroNegativeOrSettingWhatTheyMayNotAreRefused()
    {
        Func<string, object, List<TrialCondition>> setting = (path, value) =>
        {
            var c = Conditions(0, 1, 0);
            var set = Shows("auditory");   // still auditory, its trialType derived from what it shows
            set[path] = value;
            c[1].Set = set;
            return c;
        };
        var unequal = setting("visualStimulus.contrasts", new List<object> { 0.5, 1.0 });
        ((Dictionary<object, object>)unequal[1].Set)["auditoryStimulus.initialAzimuthsDeg"] = new List<object> { 20.0 };
        unequal[1].Pairs = new List<List<string>> { new List<string> { "visualStimulus.contrasts", "auditoryStimulus.initialAzimuthsDeg" } };
        var alone = Conditions(0, 1, 0);
        alone[1].Pairs = new List<List<string>> { new List<string> { "visualStimulus.contrasts" } };
        var many = setting("visualStimulus.contrasts", Enumerable.Range(1, 33).Select(i => (object)(i / 100.0)).ToList());
        ((Dictionary<object, object>)many[1].Set)["auditoryStimulus.initialAzimuthsDeg"] = Enumerable.Range(1, 32).Select(i => (object)(i - 16.0)).ToList();
        var cases = new[]
        {
            Tuple.Create(new List<TrialCondition>(), "trialConditions needs a condition"),
            Tuple.Create(Conditions(0, 0, 0), "trialConditions needs a condition with a weight above 0"),
            Tuple.Create(Conditions(-1, 2, 0), "trialConditions[0] (visual): its weight is below 0"),
            Tuple.Create(setting("rngSeed", 2.0), "trialConditions[1] (auditory) sets or pairs rngSeed, a session field (x-session-fields: rngSeed)"),
            Tuple.Create(setting("optogenetics.enabled", true), "trialConditions[1] (auditory) sets or pairs optogenetics.enabled, a session field (x-session-fields: optogenetics.enabled)"),
            Tuple.Create(setting("trialStructure.lengthCm", 2.0), "trialConditions[1] (auditory) sets or pairs trialStructure.lengthCm, which is not a task field"),
            Tuple.Create(setting("trialStructure", 2.0), "trialConditions[1] (auditory) sets or pairs trialStructure, which is not a task field"),
            Tuple.Create(setting("rewardZoneMinHz", 2.0), "trialConditions[1] (auditory): the reward zone (2 to 24000 Hz) is not inside the track's tones (2000 to 24000 Hz)"),
            Tuple.Create(setting("optogenetics.laserOnProbability", 0.5), "trialConditions[1] (auditory) sets optogenetics.laserOnProbability with optogenetics off"),
            Tuple.Create(setting("reward.sizeUl", 0.0), "trialConditions[1] (auditory): reward.sizeUl must be above 0 and at most 20 ul"),
            Tuple.Create(setting("autoRewardProbability", 2.0), "trialConditions[1] (auditory): autoRewardProbability is 2, not from 0 to 1"),
            Tuple.Create(setting("auditoryStimulus.initialAzimuthsDeg", new List<object> { "left" }), "trialConditions[1] (auditory): auditoryStimulus.initialAzimuthsDeg must each be all or a number in [-180, 180]"),
            Tuple.Create(setting("auditoryStimulus.initialAzimuthsDeg", new List<object> { 200.0 }), "trialConditions[1] (auditory): auditoryStimulus.initialAzimuthsDeg must each be all or a number in [-180, 180]"),
            Tuple.Create(setting("auditoryStimulus.levelDbfs", 0.0), "trialConditions[1] (auditory): auditoryStimulus.levelDbfs (0) must be from -120 to -1 dBFS, or mute"),
            Tuple.Create(setting("auditoryStimulus.levelDbfs", "loud"), "trialConditions[1] (neutral): auditoryStimulus.levelDbfs: "),   // loud is no number, so no tone sounds
            Tuple.Create(setting("visualStimulus.contrasts", 0.5), "trialConditions[1] (audiovisualCoherent): its values do not read as the task's fields"),
            Tuple.Create(setting("visualStimulus.contrasts", new List<object> { 0.5, -1.0 }), "trialConditions[1] (audiovisualCoherent) with visualStimulus.contrasts [-1]: visualStimulus.contrasts must each be a finite number, 0 or more"),
            Tuple.Create(setting("visualStimulus.contrasts", new List<object>()), "trialConditions[1] (auditory): visualStimulus.contrasts is an empty list"),
            Tuple.Create(unequal, "trialConditions[1] (audiovisualCoherent): pairs [visualStimulus.contrasts, auditoryStimulus.initialAzimuthsDeg] have lists of lengths 2, 1; paired lists must be equally long"),
            Tuple.Create(alone, "trialConditions[1] (auditory): each pairs group needs two paths or more"),
            Tuple.Create(many, "trialConditions[1] (audiovisualCoherent) expands into 1056 combinations, more than 1024 (x-max-combinations)"),
        };
        foreach (var c in cases)
        {
            var task = Fixture.Task(2);
            // One track length and one fraction, so a combination is named by the condition's own picks alone.
            task.Track.LengthsCm = new List<double> { 100 };
            task.StimulusCutoff.TrackFractions = new List<double> { 0.5 };
            task.TrialConditions = c.Item1;
            StringAssert.Contains(Assert.ThrowsException<InvalidOperationException>(() => Fixture.Setup(task)).Message, c.Item2);
        }
    }

    [TestMethod]
    public void TheWeightCapASetThatIsNotAMapAndWeightsTooBigToSumAreRefusedAtGo()
    {
        Func<List<TrialCondition>, string> refusal = c =>
        {
            var task = Fixture.Task(2);
            task.TrialConditions = c;
            return Assert.ThrowsException<InvalidOperationException>(() => Fixture.Setup(task)).Message;
        };
        StringAssert.Contains(refusal(Conditions(TrialConditions.MaxWeight + 1, 1, 0)), "trialConditions[0] (visual): its weight is above 1000000");
        var notMap = Conditions(0, 1, 0);
        notMap[1].Set = "5";
        StringAssert.Contains(refusal(notMap), "trialConditions[1] (audiovisualCoherent): its set is not a map");   // named from the task's values alone
        // 2200 conditions of the cap's weight sum past int, which random.Next cannot take.
        var heavy = Enumerable.Range(0, 2200).Select(i => new TrialCondition { Weight = TrialConditions.MaxWeight }).ToList();
        StringAssert.Contains(refusal(heavy), "weights add to more than 2147483647");
    }

    [TestMethod]
    public void OneConditionAtTheCapAndManyConditionsAreDrawnAsWritten()
    {
        var task = Fixture.Task(3);
        task.TrialConditions = new List<TrialCondition> { new TrialCondition { Weight = TrialConditions.MaxWeight } };
        var one = Fixture.Draw(task);
        Assert.IsTrue(Enumerable.Range(0, 30).Select(i => one.Next()).All(t => t.trialConditionId == 0 && t.trialType == "audiovisualCoherent"));
        task.TrialConditions = Enumerable.Range(0, 120).Select(i => new TrialCondition { Weight = 1, Set = Shows(Labels[i % 3]) }).ToList();
        var many = Fixture.Draw(task);
        var trials = Enumerable.Range(0, 3000).Select(i => many.Next()).ToList();
        Assert.IsTrue(trials.All(t => t.trialType == Labels[t.trialConditionId % 3]));
        Assert.AreEqual(120, trials.Select(t => t.trialConditionId).Distinct().Count());
    }

    [TestMethod]
    public void TheTaskFilesConditionsAreReadByTheirYamlNamesAndDrawWhatTheirTrialTypesSay()
    {
        var text = File.ReadAllText(Path.Combine(Fixture.Repo(), "config", "task-abstract-corridor.yml"))
            .Replace("- { weight: 0, set: { auditoryStimulus.levelDbfs: mute } }", "- { weight: 2, set: { auditoryStimulus.levelDbfs: mute } }");
        foreach (var task in new[] { new DeserializerBuilder().Build().Deserialize<UclOpenCoenDomeNavigationTaskLogic>(text).TaskParameters, TaskYaml.Read<UclOpenCoenDomeNavigationTaskLogic>(text).TaskParameters })
        {
            Assert.AreEqual(0, ((System.Collections.IDictionary)task.TrialConditions[2].Set).Count, "set: {} reads as an empty map");
            var problems = new List<string>();
            var built = TrialDraw.BuildConditions(text, task, problems);
            Assert.AreEqual(0, problems.Count, string.Join("; ", problems));
            CollectionAssert.AreEqual(new[] { 2, 1, 0 }, built.Select(c => c.Weight).ToArray());
            // visual: the grating at the task's contrast, no tone; auditory: the tone at its level, no grating; both.
            // Each expands into the task's 3 track lengths times its 1 cutoff fraction (1.0, the default).
            CollectionAssert.AreEqual(new[] { "1:mute", "0:-55", "1:-55" }, built.Select(c => c.Combinations[0].Task).Select(m => string.Join(",", m.VisualStimulus.Contrasts) + ":" + Convert.ToString(m.AuditoryStimulus.LevelDbfs, System.Globalization.CultureInfo.InvariantCulture)).ToArray());
            Assert.IsTrue(built.All(c => c.Combinations.Count == 3));
        }
        // ReadTask keeps a set value a number, so Settings_Task logs it as one (the typed read's text would log "0").
        var read = TaskYaml.Read<UclOpenCoenDomeNavigationTaskLogic>(text).TaskParameters;
        Assert.IsNotInstanceOfType(((System.Collections.IDictionary)read.TrialConditions[1].Set)["visualStimulus.contrasts"], typeof(string));
        // Each condition's trialType is derived from what it shows (x-trial-types) on the task as Go reads it (ReadTask: the
        // typed read's text values would name every condition visual).
        var names = TrialDraw.BuildConditions(text, read, new List<string>()).Select(c => c.TrialType).ToArray();
        CollectionAssert.AreEqual(new[] { "visual", "auditory", "audiovisualCoherent" }, names);
    }

    [TestMethod]
    public void AConditionsContrastAndLevelDriveTheCorridorAndNameIt()
    {
        // A condition setting a dim grating and a quieter tone shows both, as set, and is named audiovisualCoherent.
        var task = Fixture.Task(1);
        task.TrialConditions = new List<TrialCondition> { new TrialCondition { Weight = 1, Set = new Dictionary<object, object> { { "visualStimulus.contrasts", new List<object> { 0.5 } }, { "auditoryStimulus.levelDbfs", -64.1 } } } };
        var rig = new Rig(Fixture.Setup(task));
        rig.RunTo(TrialEventKind.Start);
        var pip = rig.RunTo(TrialEventKind.Update).Update;
        Assert.AreEqual(Fixture.Sweep().GainOf(-64.1), pip.AuditoryGain, 1e-9);   // -6 dB under the stored pips
        Assert.AreEqual(1.0, pip.Grating.Opacity);
        Assert.AreEqual(0.5, pip.Grating.Contrast);
        rig.Wheel(1000);
        var row = rig.RunTo(TrialEventKind.Outcome).Outcome.Row.Value;
        Assert.AreEqual("audiovisualCoherent", row.trialType);
        Assert.AreEqual(0.5, row.visualContrast);
        Assert.AreEqual(-64.1, row.auditoryLevelDbfs);
        // Neither: a neutral trial runs the track with nothing shown.
        task.TrialConditions = new List<TrialCondition> { new TrialCondition { Weight = 1, Set = new Dictionary<object, object> { { "visualStimulus.contrasts", new List<object> { 0.0 } }, { "auditoryStimulus.levelDbfs", "mute" } } } };
        rig = new Rig(Fixture.Setup(task));
        rig.RunTo(TrialEventKind.Start);
        pip = rig.RunTo(TrialEventKind.Update).Update;
        Assert.AreEqual(0.0, pip.AuditoryGain);
        Assert.AreEqual(0.0, pip.Grating.Opacity);
    }

    [TestMethod]
    public void TheGratingFollowsEachPipAndHidesAtTheOutcome()
    {
        // GratingStates (the Grating subject): each pip's grating as it comes; at the outcome the last pip's at opacity 0.
        var task = Fixture.Task(1);
        task.TrialConditions = new List<TrialCondition> { new TrialCondition { Weight = 1, Set = new Dictionary<object, object> { { "visualStimulus.contrasts", new List<object> { 0.5 } } } } };
        var rig = new Rig(Fixture.Setup(task));
        rig.RunTo(TrialEventKind.Start);
        rig.RunTo(TrialEventKind.Update);
        rig.Wheel(1000);
        rig.RunTo(TrialEventKind.Outcome);
        var states = new GratingStates().Process(rig.Events.ToObservable()).ToList().Wait();
        var pips = rig.Events.Where(e => e.Kind == TrialEventKind.Update).Select(e => e.Update.Grating).ToList();
        Assert.IsTrue(pips.Count > 1);
        CollectionAssert.AreEqual(pips, states.Take(pips.Count).ToList());
        var last = states.Last();
        Assert.AreEqual(pips.Count + 1, states.Count);
        Assert.AreEqual(0.0, last.Opacity);
        Assert.AreEqual(1.0, pips.Last().Opacity);
        Assert.AreEqual(pips.Last().SpatialFrequencyCpd, last.SpatialFrequencyCpd);
        Assert.AreEqual(0.5, last.Contrast);
    }

    [TestMethod]
    public void ListsAreDrawnPerTrialAndPairedListsShareOneIndex()
    {
        var task = Fixture.Task(1);
        Func<bool, List<Trial>> trials = pair =>
        {
            task.TrialConditions = new List<TrialCondition> { new TrialCondition { Weight = 1,
                Set = new Dictionary<object, object> { { "visualStimulus.contrasts", new List<object> { 0.25, 0.5 } }, { "auditoryStimulus.initialAzimuthsDeg", new List<object> { -90.0, 90.0 } } },
                Pairs = pair ? new List<List<string>> { new List<string> { "visualStimulus.contrasts", "auditoryStimulus.initialAzimuthsDeg" } } : new List<List<string>>() } };
            var draw = Fixture.Draw(task);
            return Enumerable.Range(0, 400).Select(i => draw.Next()).ToList();
        };
        var paired = trials(true).Select(t => t.visualContrast + ":" + t.auditoryInitialAzimuthDeg).Distinct().OrderBy(x => x).ToArray();
        CollectionAssert.AreEqual(new[] { "0.25:-90", "0.5:90" }, paired);
        Assert.AreEqual(4, trials(false).Select(t => t.visualContrast + ":" + t.auditoryInitialAzimuthDeg).Distinct().Count());
        // The task's own contrasts list is drawn per trial too.
        task.VisualStimulus.Contrasts = new List<double> { 0.3, 0.6 };
        task.TrialConditions = Conditions(1, 0, 0);
        var draw2 = Fixture.Draw(task);
        CollectionAssert.AreEquivalent(new[] { 0.3, 0.6 }, Enumerable.Range(0, 100).Select(i => draw2.Next().visualContrast).Distinct().ToArray());
    }

    [TestMethod]
    public void TheMaxToneIsTheMaxLevelAWeightedConditionCanDraw()
    {
        var task = Fixture.Task(1);
        task.TrialConditions = new List<TrialCondition>
        {
            new TrialCondition { Weight = 1, Set = new Dictionary<object, object> { { "auditoryStimulus.levelDbfs", -50.0 } } },
            new TrialCondition { Weight = 0, Set = new Dictionary<object, object> { { "auditoryStimulus.levelDbfs", -30.0 } } },
            new TrialCondition { Weight = 1, Set = new Dictionary<object, object> { { "auditoryStimulus.levelDbfs", "mute" } } },
        };
        var max = Fixture.Setup(task).MaxStimulusLevel;
        Assert.AreEqual(-50.0, max.LevelDbfs);   // the weight 0 condition's -30 is never drawn
        StringAssert.StartsWith(max.Where, "trialConditions[0] (audiovisualCoherent)");   // the task's grating, shown too
    }

    [TestMethod]
    public void TheSessionFieldsAndTheCapAreTheSchemas()
    {
        var schema = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(Path.Combine(Fixture.Repo(), "src", "DataSchemas", "ucl_open_coen_dome_navigation_task.json")));
        var parameters = schema["$defs"]["UclOpenCoenDomeNavigationTaskParameters"];
        CollectionAssert.AreEqual(TaskSchema.SessionFields, parameters["x-session-fields"].ToObject<string[]>());
        Assert.AreEqual(TaskSchema.MaxCombinations, parameters["x-max-combinations"].ToObject<int>());
    }

    [TestMethod]
    public void EachTrialReadsItsCombinationAndLogsItsIndex()
    {
        var task = Fixture.Task(2);
        task.StimulusCutoff.TrackFractions = new List<double> { 0.5 };
        task.TrialConditions = new List<TrialCondition> { new TrialCondition { Weight = 1,
            Set = new Dictionary<object, object> { { "track.lengthsCm", new List<object> { 40.0, 80.0 } }, { "track.wheelGain", 2.0 }, { "penalty.noResponse.timeoutSeconds", 0.5 } } } };
        var draw = Fixture.Draw(task);
        var trials = Enumerable.Range(0, 200).Select(i => draw.Next()).ToList();
        // The lengths are the one list with a choice: combination 0 draws 40 track cm, 1 draws 80, each at the condition's gain.
        Assert.IsTrue(trials.All(t => t.TrackLengthCm == (t.trialCombinationId == 0 ? 40 : 80) && t.WheelGain == 2 && t.trackLengthWheelCm == t.TrackLengthCm / 2));
        CollectionAssert.AreEquivalent(new[] { 0, 1 }, trials.Select(t => t.trialCombinationId).Distinct().ToArray());
        var rig = new Rig(Fixture.Setup(task));
        var trial = rig.RunTo(TrialEventKind.Start).Trial;
        rig.RunTo(TrialEventKind.Update);
        rig.Wheel(1000);
        var outcome = rig.Events.Last(e => e.Kind == TrialEventKind.Outcome).Outcome;
        Assert.AreEqual(trial.trialCombinationId, outcome.Row.Value.trialCombinationId);
        // Run to the track's end: a miss, penalised with the condition's penalty.noResponse timeout.
        Assert.AreEqual(0.5, outcome.PenaltyTimeoutSeconds);
    }

    [TestMethod]
    public void TheTaskFilesConditionsShowWhatTheirTrialTypesSayAndLogIt()
    {
        foreach (var label in Labels)
        {
            var type = label;
            var task = Fixture.Task(1);
            task.TrialConditions = Conditions(type == "visual" ? 1 : 0, type == "auditory" ? 1 : 0, type == "audiovisualCoherent" ? 1 : 0);
            var rig = new Rig(Fixture.Setup(task));
            rig.RunTo(TrialEventKind.Start);
            var pip = rig.RunTo(TrialEventKind.Update).Update;
            Assert.AreEqual(type == "visual" ? 0.0 : Fixture.Sweep().GainOf(-55), pip.AuditoryGain, 1e-9, type);
            Assert.AreEqual(type == "auditory" ? 0.0 : 1.0, pip.Grating.Opacity, type);
            rig.Wheel(1000);
            var row = rig.RunTo(TrialEventKind.Outcome).Outcome.Row.Value;
            Assert.AreEqual(type, row.trialType);
            Assert.AreEqual(type == "auditory" ? 0.0 : 1.0, row.visualContrast, type);
            Assert.AreEqual(type == "visual" ? double.NaN : -55, row.auditoryLevelDbfs, type);
        }
    }

    [TestMethod]
    public void TheIntervalIsDrawnBeforeEveryTrialTheFirstToo()
    {
        var draw = Fixture.Draw(Fixture.Task(0));
        for (int i = 0; i < 100; i++)
        {
            var iti = draw.Next().itiSeconds;
            Assert.IsTrue(iti >= 2.5 && iti <= 3.5, iti.ToString());
        }
    }

    [TestMethod]
    public void TheWheelRunsTheTrackOverTheStagesGainAndStimulusCutoffFollowsItsConditionsShare()
    {
        var draw = Fixture.Draw(Fixture.Task(0));
        var drawn = Enumerable.Range(0, 200).Select(_ => draw.Next()).ToList();
        CollectionAssert.AreEqual(new[] { 10.0, 12.5, 15.0 }, drawn.Select(t => t.trackLengthWheelCm).Distinct().OrderBy(l => l).ToList());   // 80, 100, 120 track cm at wheelGain 8
        CollectionAssert.AreEqual(new[] { 80.0, 100.0, 120.0 }, drawn.Select(t => t.TrackLengthCm).Distinct().OrderBy(l => l).ToList());
        Assert.IsTrue(drawn.All(t => t.WheelGain == 8));
        draw = Fixture.Draw(Fixture.Task(5));
        var trials = Enumerable.Range(0, 2000).Select(_ => draw.Next()).ToList();
        var off = trials.Where(t => !double.IsNaN(t.cutoffFraction)).ToList();
        Assert.AreEqual(0.16, off.Count / 2000.0, 0.03);
        CollectionAssert.AreEquivalent(new[] { 0.4, 0.6 }, off.Select(t => t.cutoffFraction).Distinct().ToList());
        Assert.IsTrue(off.All(t => t.auditoryCutoff && t.visualCutoff));
        Assert.IsTrue(trials.Except(off).All(t => !t.auditoryCutoff && !t.visualCutoff));
    }

    [TestMethod]
    public void EveryOptogeneticsTrialHasAConditionAndLaserOffIsZeroMilliwatts()
    {
        var task = Fixture.Task(4);
        Fixture.Opto(task);
        var draw = Fixture.Draw(task);
        var trials = Enumerable.Range(0, 500).Select(_ => draw.Next()).ToList();
        Assert.IsTrue(trials.All(t => t.laserCondition >= 1 && t.laserCondition <= 4));
        Assert.IsTrue(trials.Take(20).All(t => !t.laserOn));
        Assert.IsTrue(trials.Where(t => !t.laserOn).All(t => t.laserPowerMw == 0));
        Assert.IsTrue(trials.Where(t => t.laserOn).All(t => t.laserPowerMw == 2.0));
        Assert.AreEqual(0.4, trials.Skip(20).Count(t => t.laserOn) / 480.0, 0.07);
        var rules = Fixture.Setup(task);
        Assert.IsTrue(trials.All(t => TrialRules.FirstPipStep(t) == 2 && rules.Timing(t).Laser && rules.Timing(t).LaserToStimulusSeconds == 0.1));
        Assert.IsTrue(rules.Laser.HardwareTriggered);   // armed during the interval
    }

    [TestMethod]
    public void AConditionSetsTheRewardAutoRewardAndLaserOnChances()
    {
        var task = Fixture.Task(4);
        Fixture.Opto(task);
        task.TrialConditions = new List<TrialCondition>
        {
            new TrialCondition { Weight = 1, Set = new Dictionary<object, object> {
                { "reward.sizeUl", new List<object> { 2.0, 4.0 } }, { "autoRewardProbability", 1.0 }, { "optogenetics.laserOnProbability", 0.0 } } },
            new TrialCondition { Weight = 1, Set = new Dictionary<object, object> {
                { "auditoryStimulus.levelDbfs", "mute" }, { "optogenetics.laserOnProbability", 1.0 } } },
        };
        var draw = Fixture.Draw(task);
        var trials = Enumerable.Range(0, 400).Select(_ => draw.Next()).Skip(20).ToList();
        var auditory = trials.Where(t => t.trialConditionId == 0).ToList();
        var visual = trials.Where(t => t.trialConditionId == 1).ToList();
        CollectionAssert.AreEquivalent(new[] { 2.0, 4.0 }, auditory.Select(t => t.rewardOfferedUl).Distinct().ToArray());
        Assert.IsTrue(auditory.All(t => t.autoReward && !t.laserOn));
        Assert.IsTrue(visual.All(t => t.rewardOfferedUl == 3.0 && !t.autoReward && t.laserOn));
    }

    [TestMethod]
    public void ThePipsPlayOnEverySpeakerOrTheOneNearestThePickedAzimuth()
    {
        // all (the task file's): every speaker, logged NaN with a tone sounding.
        var task = Fixture.Task(1);
        var all = Fixture.Draw(task).Next();
        Assert.AreEqual(SoundAzimuths.AllSpeakersChannel, all.speakerChannel);
        Assert.IsTrue(double.IsNaN(all.auditoryInitialAzimuthDeg) && double.IsNaN(all.auditoryElevationDeg) && all.auditoryLevelDbfs == -55);
        // A number: rig.yml's speaker nearest it.
        task.TrialConditions = new List<TrialCondition> { new TrialCondition { Weight = 1,
            Set = new Dictionary<object, object> { { "auditoryStimulus.initialAzimuthsDeg", new List<object> { -70.0, "all", 20.0 } } } } };
        var draw = Fixture.Draw(task);
        var picked = Enumerable.Range(0, 200).Select(_ => draw.Next()).Select(t => t.speakerChannel + ":" + t.auditoryInitialAzimuthDeg + ":" + t.auditoryElevationDeg).Distinct().OrderBy(x => x).ToArray();
        CollectionAssert.AreEqual(new[] { "0:-90:15", "-2:NaN:NaN", "2:0:15" }.OrderBy(x => x).ToArray(), picked);
        // Silent: no speaker, NaN.
        task.TrialConditions[0].Set = new Dictionary<object, object> { { "auditoryStimulus.levelDbfs", "mute" }, { "auditoryStimulus.initialAzimuthsDeg", new List<object> { 20.0 } } };
        var silent = Fixture.Draw(task).Next();
        Assert.AreEqual(-1, silent.speakerChannel);
        Assert.IsTrue(double.IsNaN(silent.auditoryInitialAzimuthDeg));
        // A number or all on a rig without speakers is refused at Go; silent, it is not.
        foreach (var azimuth in new object[] { 20.0, "all" })
        {
            task.TrialConditions[0].Set = new Dictionary<object, object> { { "auditoryStimulus.initialAzimuthsDeg", new List<object> { azimuth } } };
            StringAssert.Contains(Assert.ThrowsException<InvalidOperationException>(() => Fixture.Draw(task, false)).Message, "rig.yml has no speakerArray.speakers to play auditoryStimulus.initialAzimuthsDeg on");
        }
        task.AuditoryStimulus.LevelDbfs = "mute";
        task.TrialConditions[0].Set = new Dictionary<object, object>();
        Fixture.Draw(task, false);
        // The trial row logs the speaker; the pip carries its channel to PipBuffer.
        var rig = new Rig(Fixture.Setup(Fixture.Task(1)));
        var start = rig.RunTo(TrialEventKind.Start);
        start.Trial.speakerChannel = 2;
        start.Trial.auditoryInitialAzimuthDeg = 0;
        rig.Until(rig.Now + 0.3);
        Assert.AreEqual(2, rig.Events.Last(e => e.Kind == TrialEventKind.Update).Update.SpeakerChannel);
    }

    [TestMethod]
    public void ASoftwareTriggeredRigIsNeverArmedAhead()
    {
        // Only the test mouse runs there: lib-open-rigs' ConnectZapit refuses a real one (RequireZapitHardwareTrigger).
        var task = Fixture.Task(4);
        Fixture.Opto(task);
        Assert.IsFalse(Fixture.Setup(task, false).Laser.HardwareTriggered);
    }

    [TestMethod]
    public void ALeadTimeOutsideWholeStepsUpTo200MsIsRefused()
    {
        foreach (var seconds in new[] { 0.07, -0.07, 0.25 })
        {
            var task = Fixture.Task(4);
            Fixture.Opto(task);
            task.Optogenetics.LaserToStimulusSeconds = seconds;
            StringAssert.Contains(Assert.ThrowsException<InvalidOperationException>(() => Fixture.Setup(task)).Message, "laserToStimulusSeconds");
        }
        var ok = Fixture.Task(4);
        Fixture.Opto(ok);
        ok.Optogenetics.LaserToStimulusSeconds = 0.2;
        Assert.AreEqual(4, TrialRules.FirstPipStep(Fixture.Draw(ok).Next()));
        ok.Optogenetics.LaserToStimulusSeconds = -0.5;   // after the first pip: the first pip at the trial start, the trigger 10 steps later
        var late = Fixture.Draw(ok).Next();
        Assert.IsTrue(TrialRules.FirstPipStep(late) == 0 && Fixture.Setup(ok).Timing(late).LaserToStimulusSeconds == -0.5);
        ok.Optogenetics.LaserToStimulusSeconds = -12.05;  // past stage 4's 12 s limit
        StringAssert.Contains(Assert.ThrowsException<InvalidOperationException>(() => Fixture.Setup(ok)).Message, "responseWindowSeconds");
        ok.Optogenetics.LaserToStimulusSeconds = -2.05;   // past the 2 s on a stage without one
        StringAssert.Contains(Assert.ThrowsException<InvalidOperationException>(() => Fixture.Setup(Fixture.Stage(ok, 2))).Message, "without responseWindowSeconds");
        var tight = Fixture.Task(1);
        tight.TrialStructure.ItiMinSeconds = 0.35;
        tight.Optogenetics.LaserToStimulusSeconds = 0.2;
        StringAssert.Contains(Assert.ThrowsException<InvalidOperationException>(() => Fixture.Setup(tight)).Message, "reply margin");
    }

    [TestMethod]
    public void TheQuietWaitsAreRefusedNegativeOrHalfSet()
    {
        var task = Fixture.Task(1);
        task.TrialStructure.WheelQuietSeconds = 1;
        Fixture.Setup(task);   // wheelQuietCm 0 allows no movement: the lab's QuietWait no longer reads it as half set
        task.TrialStructure.WheelQuietCm = -2;
        StringAssert.Contains(Assert.ThrowsException<InvalidOperationException>(() => Fixture.Setup(task)).Message, "trialStructure.wheelQuietCm");
        task.TrialStructure.WheelQuietCm = 2;
        Fixture.Setup(task);
        task.TrialStructure.LickQuietSeconds = -1;
        StringAssert.Contains(Assert.ThrowsException<InvalidOperationException>(() => Fixture.Setup(task)).Message, "lickQuietSeconds is -1, not from 0 to 10 s");
    }

    [TestMethod]
    public void AQuietWaitAboveItsMaximumIsRefusedAtGo()
    {
        var cases = new Dictionary<string, Action<UclOpenCoenDomeNavigationTaskParameters>>
        {
            { "lickQuietSeconds is 10.5, not from 0 to 10 s", t => t.TrialStructure.LickQuietSeconds = 10.5 },
            { "trialStructure.wheelQuietSeconds is 11, not from 0 to 10 s", t => { t.TrialStructure.WheelQuietSeconds = 11; t.TrialStructure.WheelQuietCm = 1; } },
            { "trialStructure.wheelQuietCm is 21, not from 0 to 20 cm", t => { t.TrialStructure.WheelQuietSeconds = 1; t.TrialStructure.WheelQuietCm = 21; } },
        };
        foreach (var c in cases)
        {
            var task = Fixture.Task(4);
            c.Value(task);
            StringAssert.Contains(Assert.ThrowsException<InvalidOperationException>(() => Fixture.Setup(task), c.Key).Message, c.Key);
        }
        var atMax = Fixture.Task(4);
        atMax.TrialStructure.LickQuietSeconds = 10;
        atMax.TrialStructure.WheelQuietSeconds = 10;
        atMax.TrialStructure.WheelQuietCm = 20;
        Fixture.Setup(atMax);
    }

    [TestMethod]
    public void ANaNOrInfiniteTimeIsRefusedAtGo()
    {
        // YAML's .nan or .inf: an interval, quiet wait or timeout of NaN or infinity would hold the task for ever, with no error.
        var cases = new Dictionary<string, Action<UclOpenCoenDomeNavigationTaskParameters>>
        {
            { "itiMaxSeconds", t => t.TrialStructure.ItiMaxSeconds = double.PositiveInfinity },
            { "itiMinSeconds", t => t.TrialStructure.ItiMinSeconds = double.NaN },
            { "timeoutSeconds", t => t.Penalty.Incorrect.TimeoutSeconds = double.NaN },
            { "lickQuietSeconds", t => t.TrialStructure.LickQuietSeconds = double.NaN },
            { "wheelQuietSeconds", t => t.TrialStructure.WheelQuietSeconds = double.PositiveInfinity },
            { "wheelGain", t => t.Track.WheelGain = 0 },
            { "autoProgressPerSecond", t => t.Track.AutoProgressPerSecond = double.NaN },
            { "responseWindowSeconds", t => t.TrialStructure.ResponseWindowSeconds = double.NaN },
            { "lengthsCm", t => t.Track.LengthsCm[0] = double.PositiveInfinity },
        };
        foreach (var c in cases)
        {
            var task = Fixture.Task(4);
            c.Value(task);
            StringAssert.Contains(Assert.ThrowsException<InvalidOperationException>(() => Fixture.Setup(task), c.Key).Message, c.Key);
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
}

/// <summary>The task's trials: TrialLoop driven tick by tick with the task's rules.</summary>
[TestClass]
public class TrialRulesTests
{
    [TestMethod]
    public void TheFirstTrialWaitsItsIntervalAndPipsEvery50MsFromTheLead()
    {
        var rig = new Rig(Fixture.Setup(Fixture.Task(1)));
        rig.Now = 100;
        var start = rig.RunTo(TrialEventKind.Start);
        Assert.AreEqual(100.001 + start.Trial.itiSeconds - 0.1, start.Seconds, 1e-9);   // the trial start: the interval's end less the 0.1 s lead
        rig.Until(rig.Now + 1.0);
        var pips = rig.Events.Where(e => e.Kind == TrialEventKind.Update).Select(e => e.Seconds).ToList();
        Assert.AreEqual(100.001 + start.Trial.itiSeconds, pips[0], 0.0011);   // the first pip: exactly the interval after its start
        for (int i = 1; i < pips.Count; i++) Assert.AreEqual(0.05, pips[i] - pips[i - 1], 1e-6);
        Assert.IsTrue(rig.Events.Where(e => e.Kind == TrialEventKind.Update).All(e => e.Update.Row.auditoryFrequencyHz == 2000 && e.Update.PipIndex == 0));
    }

    [TestMethod]
    public void ReachingTheZoneOnStage1IsRewardedAndTheNextIntervalStartsAtTheOutcome()
    {
        var task = Fixture.Task(1);
        var rig = new Rig(Fixture.Setup(task));
        rig.RunTo(TrialEventKind.Start);
        rig.Wheel(1000);   // the end of a short track: the sweep's end
        var outcome = rig.RunTo(TrialEventKind.Outcome);
        Assert.IsTrue(outcome.Outcome.Rewarded);
        Assert.AreEqual(TrialOutcomes.Auto, outcome.Outcome.Row.Value.outcome);   // the auto-reward answered it
        Assert.AreEqual("rewardZoneReached", outcome.Outcome.Row.Value.outcomeReason);
        Assert.AreEqual(Tuple.Create("left", 3.0), outcome.Outcome.Reward);   // the task file's reward.sizeUl, 3 ul
        Assert.IsTrue(outcome.Outcome.Row.Value.rewarded && !outcome.Outcome.Row.Value.penalised && outcome.Outcome.Row.Value.rewardOfferedUl == 3, "the row says what the valve did");
        Assert.IsTrue(outcome.Outcome.Row.Value.autoReward, "stage 1 auto-rewards every trial");
        // Rewarded: outcome, then the interval (spouts.rewardConsumptionSeconds inside it); no timeout, no flash.
        Assert.AreEqual(0.0, outcome.Outcome.Row.Value.penaltyTimeoutSeconds);
        Assert.AreEqual(0.0, outcome.Outcome.Row.Value.penaltyFlashBrightness);
        Assert.IsFalse(outcome.Outcome.PenaltyFlash.Plays);
        var next = rig.RunTo(TrialEventKind.Start);
        Assert.AreEqual(outcome.Seconds + next.Trial.itiSeconds - 0.1, next.Seconds, 1e-9);
        rig.Until(rig.Now + 0.2);
        var firstPip = rig.Events.First(e => e.Kind == TrialEventKind.Update && e.Trial == next.Trial);
        Assert.AreEqual(outcome.Seconds + next.Trial.itiSeconds, firstPip.Seconds, 0.0011);
    }

    [TestMethod]
    public void Stage0AutoProgressesIntoTheZoneWithoutRunning()
    {
        var rig = new Rig(Fixture.Setup(Fixture.Task(0)));
        var outcome = rig.RunTo(TrialEventKind.Outcome);
        Assert.AreEqual("rewardZoneReached", outcome.Outcome.Row.Value.outcomeReason);
        Assert.IsTrue(outcome.Outcome.Row.Value.outcomeAuditoryFrequencyHz >= 18000);
        Assert.IsTrue(outcome.Seconds - outcome.Outcome.Row.Value.trialStartTime > 10 && outcome.Seconds - outcome.Outcome.Row.Value.trialStartTime < 25);
        Assert.AreEqual(outcome.Seconds, outcome.Outcome.Row.Value.outcomeTime, "outcomeTime is the row's own time, harpTime");
    }

    [TestMethod]
    public void AutoProgressAndTheGratingsFrequenciesAreTheTaskFiles()
    {
        var task = Fixture.Task(0);
        task.Track.AutoProgressPerSecond = 0.2;   // the whole track in 5 s
        task.VisualStimulus.SpatialFrequencyStartCpd = 0.05;
        var rig = new Rig(Fixture.Setup(task));
        var outcome = rig.RunTo(TrialEventKind.Outcome);
        var first = rig.Events.First(e => e.Kind == TrialEventKind.Update).Update.Row;
        Assert.AreEqual(0.05, first.visualSpatialFrequencyCpd, 0.005);   // progressed a little by the first pip, after the lead
        Assert.AreEqual("rewardZoneReached", outcome.Outcome.Row.Value.outcomeReason);
        Assert.IsTrue(outcome.Seconds - outcome.Outcome.Row.Value.trialStartTime < 5);
    }

    [TestMethod]
    public void AutoRewardIsDrawnPerTrialWithTheStagesProbability()
    {
        var task = Fixture.Task(4);
        task.AutoRewardProbability = 0.5;
        var draw = Fixture.Draw(task);
        var auto = Enumerable.Range(0, 400).Count(i => draw.Next().autoReward);
        Assert.IsTrue(auto > 150 && auto < 250, auto.ToString());
        Assert.IsTrue(Enumerable.Range(0, 50).All(i => Fixture.Draw(Fixture.Task(1)).Next().autoReward));
        var never = Fixture.Draw(Fixture.Task(4));
        Assert.IsFalse(Enumerable.Range(0, 50).Any(i => never.Next().autoReward));
        task.AutoRewardProbability = 1.5;
        StringAssert.Contains(Assert.ThrowsException<InvalidOperationException>(() => Fixture.Setup(task)).Message, "autoRewardProbability is 1.5, not from 0 to 1");
    }

    [TestMethod]
    public void TheIntervalAndResponseWindowAreCheckedByTheLabsBlocks()
    {
        var task = Fixture.Task(4);
        task.TrialStructure.ItiMaxSeconds = task.TrialStructure.ItiMinSeconds - 0.5;
        task.TrialStructure.ResponseWindowSeconds = 0;
        var message = Assert.ThrowsException<InvalidOperationException>(() => Fixture.Setup(task)).Message;
        StringAssert.Contains(message, "trialStructure.itiMaxSeconds (");
        StringAssert.Contains(message, "responseWindowSeconds is 0, not a finite number above 0");
    }

    [TestMethod]
    public void AnAutoRewardTrialAtTheTracksEndEndsRewardedOnTheNextPip()
    {
        var task = Fixture.Task(4);
        task.AutoRewardProbability = 1;
        var rig = new Rig(Fixture.Setup(task));
        var start = rig.RunTo(TrialEventKind.Start);
        rig.Until(rig.Now + 0.1);
        rig.Lick();                                         // ignored on an auto-reward trial
        rig.Wheel(start.Trial.trackLengthWheelCm + 5);
        Assert.IsFalse(rig.Events.Any(e => e.Kind == TrialEventKind.Outcome));
        var outcome = rig.RunTo(TrialEventKind.Outcome);
        Assert.AreEqual("rewardZoneReached", outcome.Outcome.Row.Value.outcomeReason);
        Assert.IsTrue(outcome.Outcome.Rewarded && !outcome.Outcome.Row.Value.penalised);
        Assert.IsTrue(outcome.Outcome.Row.Value.autoReward);
    }

    [TestMethod]
    public void ALickInTheZoneIsRewardedAndOutsideCountsTowardTheLimit()
    {
        var task = Fixture.Task(4);
        var rig = new Rig(Fixture.Setup(task));  // zone 15248-21248 Hz, 5 wrong licks
        rig.RunTo(TrialEventKind.Start);
        rig.Until(rig.Now + 0.2);
        for (int i = 0; i < 4; i++) rig.Lick();
        Assert.IsFalse(rig.Events.Any(e => e.Kind == TrialEventKind.Outcome));
        rig.Lick();
        var outcome = rig.Events.Single(e => e.Kind == TrialEventKind.Outcome);
        Assert.AreEqual(TrialOutcomes.Incorrect, outcome.Outcome.Row.Value.outcome);
        Assert.AreEqual("wrongLickLimit", outcome.Outcome.Row.Value.outcomeReason);
        Assert.IsTrue(outcome.Outcome.Penalised && outcome.Outcome.PenaltyLevelDbfs == -35);
        Assert.IsTrue(outcome.Outcome.Row.Value.penalised && !outcome.Outcome.Row.Value.rewarded && !outcome.Outcome.Rewarded, "penalised, unrewarded");
        Assert.AreEqual(3.0, outcome.Outcome.Row.Value.rewardOfferedUl, "the offer kept on a trial not rewarded");
        Assert.AreEqual(4.0, outcome.Outcome.Row.Value.penaltyTimeoutSeconds, "penalty.incorrect's");
        Assert.AreEqual(0.0, outcome.Outcome.Row.Value.penaltyFlashBrightness, "the file's flash is off");

        // the next trial waits the timeout and its interval; the quiet second, counted from the interval's start, adds nothing
        var next = rig.RunTo(TrialEventKind.Start);
        Assert.AreEqual(outcome.Seconds + 4.0 + next.Trial.itiSeconds - 0.1, next.Seconds, 1e-9);
        rig.Until(rig.Now + 0.2);
        var track = next.Trial.trackLengthWheelCm;
        rig.Wheel(track * 0.85);  // inside the zone
        rig.Until(rig.Now + 0.06);
        rig.Lick();
        var rewarded = rig.Events.Last(e => e.Kind == TrialEventKind.Outcome);
        Assert.AreEqual("correctLick", rewarded.Outcome.Row.Value.outcomeReason);
        Assert.IsTrue(rewarded.Outcome.Row.Value.rewarded && !rewarded.Outcome.Row.Value.autoReward, "earned, not auto-rewarded");
        Assert.AreEqual(4.0, rewarded.Outcome.Row.Value.itiStartTime - outcome.Seconds, 0.0011);
        Assert.AreEqual(next.Trial.itiSeconds, rewarded.Outcome.Row.Value.itiSeconds);
    }

    [TestMethod]
    public void ARightLickHoldsTheStartAndNeverAnswers()
    {
        var iti = new Rig(Fixture.Setup(Fixture.Task(4))).RunTo(TrialEventKind.Start).Trial.itiSeconds;
        var r = new Rig(Fixture.Setup(Fixture.Task(4)));
        var at = Math.Round(0.001 + iti - 0.5, 3);
        r.Until(at);
        r.Lick(1);                                          // the right spout, late in the interval: the start a quiet second later
        var started = r.RunTo(TrialEventKind.Start);
        Assert.AreEqual(at + 1.0, started.Seconds, 1e-9);
        r.Lick(1);                                          // and a right lick in the trial is no answer
        r.Until(Math.Round(r.Now + 0.5, 3));
        Assert.IsFalse(r.Events.Any(e => e.Kind == TrialEventKind.Outcome));
    }

    [TestMethod]
    public void TheQuietWaitRestartsWithEachLick()
    {
        var quiet = new Rig(Fixture.Setup(Fixture.Task(4)));
        var start = quiet.RunTo(TrialEventKind.Start);   // a quiet mouse: the start at the interval's end less the lead
        var iti = start.Trial.itiSeconds;
        Assert.AreEqual(0.001 + iti - 0.1, start.Seconds, 1e-9);
        var r = new Rig(Fixture.Setup(Fixture.Task(4)));
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
        Assert.AreEqual(started.Seconds - (started.Trial.itiSeconds - 0.1 + 0.001), outcome.Outcome.Row.Value.quietWaitSeconds, 1e-9);
        Assert.AreEqual(started.Seconds, outcome.Outcome.Row.Value.trialStartTime, 1e-12);
    }

    [TestMethod]
    public void TheWheelWaitCountsTheLastWheelQuietSeconds()
    {
        var task = Fixture.Task(1);
        task.TrialStructure.WheelQuietSeconds = 1.0;
        task.TrialStructure.WheelQuietCm = 1.0;
        var still = new Rig(Fixture.Setup(task));
        still.Wheel(50);
        var start = still.RunTo(TrialEventKind.Start);
        var iti = start.Trial.itiSeconds;
        Assert.AreEqual(0.001 + iti - 0.1, start.Seconds, 1e-9);
        var rock = new Rig(Fixture.Setup(task));
        rock.Wheel(50);
        rock.Until(Math.Round(iti - 0.5, 3));
        rock.Wheel(50.6);                                   // 0.6 cm forward: within 1 cm
        rock.Until(Math.Round(iti - 0.3, 3));
        rock.Wheel(50.0);                                   // and back: net 0, 1.2 cm within a second, so the start a second after the first swing
        var started = rock.RunTo(TrialEventKind.Start);
        Assert.AreEqual(Math.Round(iti - 0.5, 3) + 1.0, started.Seconds, 1e-9);

        // 0.3 cm every 0.4 s up to the start: past 1 cm in all, never more than 0.9 cm within a second, so no delay.
        var fidget = new Rig(Fixture.Setup(task));
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
        var r = new Rig(Fixture.Setup(Fixture.Task(4)));  // lickQuietSeconds 1
        var iti = new Rig(Fixture.Setup(Fixture.Task(4))).RunTo(TrialEventKind.Start).Trial.itiSeconds;
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
        var plain = new Rig(Fixture.Setup(Fixture.Task(4))).RunTo(TrialEventKind.Start);   // stage 4: optogenetics needs a response window
        foreach (var lead in new[] { 0.1, 0.0, -0.5 })
        {
            var task = Fixture.Task(4);
            Fixture.Opto(task);
            task.Optogenetics.LaserToStimulusSeconds = lead;
            var rig = new Rig(Fixture.Setup(task));
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
        var task = Fixture.Task(4);
        Fixture.Opto(task);
        task.Optogenetics.LaserToStimulusSeconds = -0.5;   // the trigger half a second after the first pip
        task.Optogenetics.TrialsBeforeLaser = 0;
        task.Optogenetics.LaserOnProbability = 1;
        var rig = new Rig(Fixture.Setup(task));
        var arm = rig.RunTo(TrialEventKind.Arm);
        rig.Loop.OnZapitReply(new ZapitReply { Command = ZapitCommandType.StartStimulus, Value = arm.Trial.laserCondition, LaserOn = 1 });
        rig.RunTo(TrialEventKind.Start);
        rig.Until(rig.Now + 0.1);
        rig.Wheel(1000);                                    // the end of the track ends the trial before the trigger
        var outcome = rig.Events.Single(e => e.Kind == TrialEventKind.Outcome);
        Assert.IsTrue(arm.Trial.laserOn);
        Assert.AreEqual(0, outcome.Outcome.Row.Value.laserPowerMw, "no laser came, so none is logged");
        Assert.IsFalse(rig.Events.Any(e => e.Kind == TrialEventKind.Trigger));
        Assert.IsTrue(double.IsNaN(outcome.Outcome.Row.Value.laserTriggerTime), "answered before the trigger: laserTriggerTime NaN");
    }

    // laserTriggerTime (the shared block's 16th column) is the trigger's Harp time when it came.
    [TestMethod]
    public void LaserTriggerTimeIsTheTriggersTimeLaserOnOrOff()
    {
        foreach (var probability in new[] { 1.0, 0.0 })
        {
            var task = Fixture.Task(4);
            Fixture.Opto(task);
            task.Optogenetics.TrialsBeforeLaser = 0;
            task.Optogenetics.LaserOnProbability = probability;
            var rig = new Rig(Fixture.Setup(task));
            var arm = rig.RunTo(TrialEventKind.Arm);
            rig.Loop.OnZapitReply(new ZapitReply { Command = ZapitCommandType.StartStimulus, Value = arm.Trial.laserCondition, LaserOn = arm.Trial.laserOn ? 1 : 0 });
            var trigger = rig.RunTo(TrialEventKind.Trigger);
            rig.Until(rig.Now + 0.2);
            rig.Wheel(1000);                                // the end of the track ends the trial after the trigger
            var outcome = rig.Events.Single(e => e.Kind == TrialEventKind.Outcome);
            Assert.AreEqual(trigger.Seconds, outcome.Outcome.Row.Value.laserTriggerTime, 1e-12);
            Assert.AreEqual(probability > 0 ? 2.0 : 0.0, outcome.Outcome.Row.Value.laserPowerMw, "laser off: a trigger time, 0 mW");
        }
    }

    [TestMethod]
    public void LaserTriggerTimeIsNaNWithoutOptogenetics()
    {
        var rig = new Rig(Fixture.Setup(Fixture.Task(1)));
        rig.RunTo(TrialEventKind.Start);
        rig.Wheel(1000);
        var outcome = rig.RunTo(TrialEventKind.Outcome);
        Assert.IsTrue(double.IsNaN(outcome.Outcome.Row.Value.laserTriggerTime));
        Assert.AreEqual(0, outcome.Outcome.Row.Value.laserCondition);
    }

    [TestMethod]
    public void TheTrialRowsFieldsAreTheTrialColumnsInOrder()
    {
        // schemas\trial-columns.yml's names, harpTime aside (the row's timestamp), are TrialRow's fields in order:
        // LogTrials writes the fields in declaration order.
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
        var rig = new Rig(Fixture.Setup(Fixture.Task(4)));
        var start = rig.RunTo(TrialEventKind.Start);
        rig.Wheel(start.Trial.trackLengthWheelCm * 0.85);
        var outcome = rig.RunTo(TrialEventKind.Outcome);
        Assert.AreEqual("responseWindowEnd", outcome.Outcome.Row.Value.outcomeReason);
        Assert.AreEqual(TrialOutcomes.NoResponse, outcome.Outcome.Row.Value.outcome);
        Assert.IsTrue(outcome.Outcome.Penalised && outcome.Outcome.PenaltyLevelDbfs == -35);
        Assert.IsTrue(outcome.Outcome.Row.Value.penalised && !outcome.Outcome.Row.Value.rewarded);
        Assert.AreEqual(start.Seconds + 0.1 + 12.0, outcome.Seconds, 0.0011);
    }

    [TestMethod]
    public void TheEndOfTheTrackEndsTheTrial()
    {
        var rig = new Rig(Fixture.Setup(Fixture.Task(2)));
        var start = rig.RunTo(TrialEventKind.Start);
        rig.Until(rig.Now + 0.1);
        rig.Wheel(start.Trial.trackLengthWheelCm + 5);
        var outcome = rig.Events.Single(e => e.Kind == TrialEventKind.Outcome);
        Assert.AreEqual("trackEnd", outcome.Outcome.Row.Value.outcomeReason);
        Assert.AreEqual(start.Trial.TrackLengthCm, outcome.Outcome.Row.Value.outcomePositionCm, 1e-9);
    }

    [TestMethod]
    public void AStimulusCutoffTrialCutsTheGratingsVisualContrastToZero()
    {
        var rig = new Rig(Fixture.Setup(Fixture.Task(3)));
        var start = rig.RunTo(TrialEventKind.Start);
        start.Trial.visualContrast = 0.5;
        start.Trial.cutoffFraction = 0.5;
        start.Trial.visualCutoff = true;
        rig.Wheel(start.Trial.trackLengthWheelCm * 0.3);
        rig.Until(rig.Now + 0.1);
        Assert.AreEqual(0.5, rig.Events.Last(e => e.Kind == TrialEventKind.Update).Update.Row.visualContrast);
        rig.Wheel(start.Trial.trackLengthWheelCm * 0.7);
        rig.Until(rig.Now + 0.1);
        var pip = rig.Events.Last(e => e.Kind == TrialEventKind.Update).Update;
        Assert.AreEqual(0.0, pip.Row.visualContrast);
        Assert.AreEqual(0.0, pip.Grating.Opacity);
    }

    [TestMethod]
    public void AStimulusCutoffTrialSwitchesOffItsListedStimuliPastItsTrackFraction()
    {
        // The tone alone listed: it goes off once the mouse has run half the track; the grating stays on.
        var setup = Fixture.Setup(Fixture.Task(3));
        var rig = new Rig(setup);
        var start = rig.RunTo(TrialEventKind.Start);
        start.Trial.cutoffFraction = 0.5;
        start.Trial.auditoryCutoff = true;
        start.Trial.visualContrast = 1;   // a grating shown (stage 3 also draws auditory-alone trials, contrast 0)
        rig.Wheel(start.Trial.trackLengthWheelCm * 0.3);
        rig.Until(rig.Now + 0.1);
        var before = rig.Events.Last(e => e.Kind == TrialEventKind.Update).Update;
        Assert.AreEqual(Fixture.Sweep().GainOf(-55), before.AuditoryGain, 1e-9);
        Assert.IsTrue(before.Row.auditoryLevelDbfs == -55 && before.Row.visualContrast == start.Trial.visualContrast);
        rig.Wheel(start.Trial.trackLengthWheelCm * 0.7);
        rig.Until(rig.Now + 0.1);
        var pip = rig.Events.Last(e => e.Kind == TrialEventKind.Update).Update;
        Assert.AreEqual(0.0, pip.AuditoryGain);
        Assert.IsTrue(double.IsNaN(pip.Row.auditoryLevelDbfs));   // the cut, in each pip's row (and auditoryCutoffPositionCm)
        Assert.AreEqual(start.Trial.visualContrast, pip.Row.visualContrast);   // the grating stays on
        rig.Wheel(start.Trial.trackLengthWheelCm + 5);
        var row = rig.Events.Single(e => e.Kind == TrialEventKind.Outcome).Outcome.Row.Value;
        Assert.AreEqual(pip.Row.positionCm, row.auditoryCutoffPositionCm, 1e-9);
        Assert.IsTrue(double.IsNaN(row.visualCutoffPositionCm));
        Assert.AreEqual(-55, row.auditoryLevelDbfs);   // the trial's level; the pips' rows say where it was cut
        Assert.AreEqual(row.outcomeTime, row.stimulusOffTime, "the grating played on, so the stimulus went off at the outcome");
        Assert.IsNull(typeof(StimulusStateRow).GetField("auditoryOn"));
        Assert.IsNull(typeof(StimulusStateRow).GetField("visualOn"));
        Assert.AreEqual(typeof(double), typeof(StimulusStateRow).GetField("visualContrast").FieldType);
    }

    [TestMethod]
    public void AnOrdinaryTrialLogsNoOffPosition()
    {
        var rig = new Rig(Fixture.Setup(Fixture.Task(2)));
        var start = rig.RunTo(TrialEventKind.Start);
        rig.Until(rig.Now + 0.1);
        rig.Wheel(start.Trial.trackLengthWheelCm + 5);
        var row = rig.Events.Single(e => e.Kind == TrialEventKind.Outcome).Outcome.Row.Value;
        Assert.IsTrue(double.IsNaN(row.auditoryCutoffPositionCm) && double.IsNaN(row.visualCutoffPositionCm));
        Assert.AreEqual(row.outcomeTime, row.stimulusOffTime, "both played to the outcome");
    }

    [TestMethod]
    public void AStimulusCutoffTrialWithBothListedGoesOffAtTheStepNeitherPlays()
    {
        // Tone and grating listed: past half the track neither plays, so stimulusOffTime is that pip's step on the 50 ms
        // grid from the trial start, before the outcome.
        var rig = new Rig(Fixture.Setup(Fixture.Task(3)));
        var start = rig.RunTo(TrialEventKind.Start);
        start.Trial.cutoffFraction = 0.5;
        start.Trial.auditoryCutoff = true;
        start.Trial.visualCutoff = true;
        rig.Wheel(start.Trial.trackLengthWheelCm * 0.3);
        rig.Until(rig.Now + 0.1);
        rig.Wheel(start.Trial.trackLengthWheelCm * 0.7);
        rig.Until(rig.Now + 0.1);
        var first = rig.Events.First(e => e.Kind == TrialEventKind.Update && double.IsNaN(e.Update.Row.auditoryLevelDbfs));
        Assert.AreEqual(0.0, first.Update.Row.visualContrast);
        rig.Wheel(start.Trial.trackLengthWheelCm + 5);
        var row = rig.Events.Single(e => e.Kind == TrialEventKind.Outcome).Outcome.Row.Value;
        var steps = (row.stimulusOffTime - row.trialStartTime) / TrialRules.StepSeconds;
        Assert.AreEqual(Math.Round(steps), steps, 1e-6, "on the pip grid");
        Assert.IsTrue(first.Seconds - row.stimulusOffTime >= -1e-9 && first.Seconds - row.stimulusOffTime < TrialRules.StepSeconds, "the step of the first silent pip");
        Assert.IsTrue(row.stimulusOffTime < row.outcomeTime);
    }

    [TestMethod]
    public void AZapitReplyNotInByTheTriggerStopsTheTask()
    {
        var task = Fixture.Task(4);
        Fixture.Opto(task);
        var rig = new Rig(Fixture.Setup(task));
        rig.RunTo(TrialEventKind.Arm);
        var error = Assert.ThrowsException<InvalidOperationException>(() => rig.RunTo(TrialEventKind.Trigger));
        StringAssert.Contains(error.Message, "had not answered trial 1's StartStimulus by its trigger");
        var wrong = new Rig(Fixture.Setup(task));
        wrong.RunTo(TrialEventKind.Arm);
        Assert.ThrowsException<InvalidOperationException>(() => wrong.Loop.OnZapitReply(new ZapitReply { Command = ZapitCommandType.StartStimulus, Value = 99 }));
    }

    // The armed reply is checked by OpenRigs.Devices' ZapitTrial.CheckArmed, the dome tasks' one check:
    // its words, and a refusal for an error reply, another condition or another laser state.
    [TestMethod]
    public void TheArmedReplyIsZapitTrialsCheckArmed()
    {
        var task = Fixture.Task(4);
        Fixture.Opto(task);
        task.Optogenetics.TrialsBeforeLaser = 0;
        task.Optogenetics.LaserOnProbability = 1;
        Func<Rig> armed = () => { var r = new Rig(Fixture.Setup(task)); r.RunTo(TrialEventKind.Arm); return r; };
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
        var task = Fixture.Task(4);
        Fixture.Opto(task);
        var rig = new Rig(Fixture.Setup(task, false));
        rig.RunTo(TrialEventKind.Trigger);
        Assert.IsFalse(rig.Events.Any(e => e.Kind == TrialEventKind.Arm));
        Assert.ThrowsException<InvalidOperationException>(() => rig.Loop.OnZapitReply(new ZapitReply { Command = ZapitCommandType.StartStimulus, Value = 99 }));
        var right = new Rig(Fixture.Setup(task, false));
        var trigger = right.RunTo(TrialEventKind.Trigger);
        right.Loop.OnZapitReply(new ZapitReply { Command = ZapitCommandType.StartStimulus, Value = trigger.Trial.laserCondition, LaserOn = trigger.Trial.laserOn ? 1 : 0 });
    }

    [TestMethod]
    public void TheLaserCapIsTheResponseWindowPlusTheLeadPlusAQuarterSecond()
    {
        // The cap comes after the window's own end (12 s from the first pip), so it only backstops a missed outcome.
        var task = Fixture.Task(4);
        Fixture.Opto(task);
        Assert.AreEqual(12 + 0.1 + 0.25, Fixture.Setup(task, false).Laser.LaserCapSeconds, 1e-9);
    }

    [TestMethod]
    public void Stage0RunningAt40CmPerSecondPastTheCeilingIsStillRewarded()
    {
        // 6 s of auto-progress, then 2 wheel cm a step (16 track cm at wheelGain 8, over a tenth of the track): the step
        // that crosses the floor (18 kHz) passes a 20 kHz ceiling.
        var task = Fixture.Task(0);
        task.RewardZoneMaxHz = 20000;
        var rig = new Rig(Fixture.Setup(task));
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
        Assert.AreEqual("rewardZoneReached", outcome.Outcome.Row.Value.outcomeReason);
        Assert.IsTrue(outcome.Outcome.Row.Value.outcomeAuditoryFrequencyHz > 20000, outcome.Outcome.Row.Value.outcomeAuditoryFrequencyHz.ToString());
        Assert.IsTrue(outcome.Outcome.Rewarded);
        Assert.AreEqual(TrialOutcomes.Auto, outcome.Outcome.Row.Value.outcome);
        Assert.IsFalse(outcome.Outcome.Penalised);
    }

    [TestMethod]
    public void AfterAGapInTheHarpClockOnlyTheLatestStepPips()
    {
        var rig = new Rig(Fixture.Setup(Fixture.Task(3)));
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
        var off = Fixture.Setup(Fixture.Task(3));
        Assert.IsNull(off.Laser);
        var plain = new Rig(off);
        var ended = plain.RunTo(TrialEventKind.Outcome);
        plain.Until(ended.Seconds + 2.0);
        Assert.IsFalse(plain.Events.Any(e => e.Kind == TrialEventKind.Trigger || e.Kind == TrialEventKind.TriggerOff || e.Kind == TrialEventKind.Arm));
        var task = Fixture.Task(3);
        Fixture.Opto(task);
        var on = Fixture.Setup(task);
        Assert.AreEqual(Fixture.RampDown, on.Laser.TriggerHoldSeconds);   // the conditions file's max ramp-down
    }

    // The line falls the conditions file's max ramp-down after the outcome, on the Harp clock (TrialLoop's TriggerOff, which replaced
    // TaskLogic's HarpDelay), and the next trial is armed then.
    [TestMethod]
    public void TheTriggerLineFallsTriggerHoldSecondsAfterTheOutcomeAndTheNextIsArmedThen()
    {
        var task = Fixture.Task(4);
        Fixture.Opto(task);
        task.Penalty.NoResponse.TimeoutSeconds = 0;   // the next interval from the outcome (the track's end, a miss): armed once the line falls, not at its start
        var rig = new Rig(Fixture.Setup(task));
        var arm = rig.RunTo(TrialEventKind.Arm);
        rig.Loop.OnZapitReply(new ZapitReply { Command = ZapitCommandType.StartStimulus, Value = arm.Trial.laserCondition, LaserOn = arm.Trial.laserOn ? 1 : 0 });
        rig.RunTo(TrialEventKind.Trigger);
        rig.Wheel(1000);   // the end of the track ends the trial
        var outcome = rig.Events.Single(e => e.Kind == TrialEventKind.Outcome);
        var off = rig.RunTo(TrialEventKind.TriggerOff);
        Assert.AreEqual(outcome.Seconds + Fixture.RampDown, off.Seconds, 0.0011);
        var next = rig.Events.Single(e => e.Kind == TrialEventKind.Arm && e.Trial.trialNumber == 2);   // on the line's tick
        Assert.AreEqual(off.Seconds, next.Seconds, 1e-9);
    }

    [TestMethod]
    public void TheLaserCapAndTheTriggerHoldAreDerivedAtGo()
    {
        // The cap: the max responseWindowSeconds + laserToStimulusSeconds over the weighted combinations, + 0.25; the hold:
        // the conditions file's max ramp-down.
        var task = Fixture.Task(4);
        Fixture.Opto(task);
        task.Optogenetics.LaserToStimulusSeconds = 0.05;
        task.TrialConditions = new List<TrialCondition>
        {
            new TrialCondition { Weight = 1, Set = new Dictionary<object, object> { { "trialStructure.responseWindowSeconds", 8.0 } } },
            new TrialCondition { Weight = 1, Set = new Dictionary<object, object> { { "trialStructure.responseWindowSeconds", 15.0 }, { "optogenetics.laserToStimulusSeconds", 0.15 } } },
            new TrialCondition { Weight = 0, Set = new Dictionary<object, object> { { "trialStructure.responseWindowSeconds", 40.0 } } },   // never drawn: not in the cap
        };
        var draw = Fixture.Draw(task);
        Assert.AreEqual(15.0 + 0.15 + 0.25, draw.LaserCapSeconds, 1e-9);
        Assert.AreEqual(Fixture.RampDown, draw.MaxRampDownSeconds);
        var rules = TrialRules.Create(Fixture.Text(task), Fixture.Sweep(), true, path => new ZapitConditionsFile(4, 0.6), Fixture.Speakers());
        Assert.AreEqual(0.6, rules.Laser.TriggerHoldSeconds);
        Assert.AreEqual(15.0 + 0.15 + 0.25, rules.Laser.LaserCapSeconds, 1e-9);
        Assert.AreEqual(double.PositiveInfinity, Fixture.Draw(Fixture.Task(4)).LaserCapSeconds, "without optogenetics: no cap");
        Assert.AreEqual(0.0, Fixture.Draw(Fixture.Task(4)).MaxRampDownSeconds);
    }

    [TestMethod]
    public void ARampDownTooLongForTheShortestIntervalIsRefusedAndWithoutOptogeneticsNotRead()
    {
        var task = Fixture.Task(4);
        var lead = task.Optogenetics.LaserToStimulusSeconds;
        var maxRampDown = task.TrialStructure.ItiMinSeconds - lead - ZapitTrial.ReplyMarginSeconds;   // 2.2 s
        Fixture.Opto(task);
        TrialRules.Create(Fixture.Text(task), Fixture.Sweep(), true, path => new ZapitConditionsFile(4, maxRampDown), Fixture.Speakers());   // exactly the max: allowed
        var message = Assert.ThrowsException<InvalidOperationException>(() => TrialRules.Create(Fixture.Text(task), Fixture.Sweep(), true, path => new ZapitConditionsFile(4, maxRampDown + 0.1), Fixture.Speakers())).Message;
        StringAssert.Contains(message, "max ramp-down");
        StringAssert.Contains(message, "itiMinSeconds - laserToStimulusSeconds");
        task.Optogenetics.Enabled = false;
        TrialRules.Create(Fixture.Text(task), Fixture.Sweep(), true, path => new ZapitConditionsFile(4, 99.0), Fixture.Speakers());   // the file is not read without optogenetics
    }

    [TestMethod]
    public void OptogeneticsWithoutAResponseWindowIsRefused()
    {
        var task = Fixture.Task(2);   // stage 2 has none
        Assert.IsNull(task.TrialStructure.ResponseWindowSeconds);
        Fixture.Setup(task);
        Fixture.Opto(task);
        StringAssert.Contains(Assert.ThrowsException<InvalidOperationException>(() => Fixture.Setup(task)).Message, "optogenetics needs trialStructure.responseWindowSeconds");
    }

    [TestMethod]
    public void ARewardZoneAboveTheTracksTopIsRefusedAtGoNamingTheZone()
    {
        var task = Fixture.Task(4);
        task.RewardZoneMaxHz = 25000;   // the tone files reach 25 kHz, the track only 24 kHz
        var message = Assert.ThrowsException<InvalidOperationException>(() => Fixture.Setup(task)).Message;
        StringAssert.Contains(message, "the reward zone (15248 to 25000 Hz) is not inside the track's tones (2000 to 24000 Hz)");
        task.RewardZoneMaxHz = 24000;
        Fixture.Setup(task);
    }

    [TestMethod]
    public void ATrialThatRunsOutCostsPenaltyNoResponseAndTheWrongLickLimitPenaltyIncorrect()
    {
        // Out of track (ranOut, outcome noResponse): the miss's own timeout, level and flash, the row logging the timeout
        // and flash brightness used.
        var task = Fixture.Task(2);
        task.Penalty.NoResponse.TimeoutSeconds = 5;
        task.Penalty.NoResponse.Sound.LevelDbfs = "mute";
        task.Penalty.NoResponse.Flash.Brightness = 0.5;
        task.Penalty.NoResponse.Flash.RateHz = 5;
        var rules = Fixture.Setup(task);
        Assert.IsTrue(rules.PenaltyFlashes.Plays);
        Assert.AreEqual(5.0, rules.PenaltyFlashes.ShortestTimeoutSeconds, "the shortest timeout of a kind that flashes");
        var rig = new Rig(rules);
        rig.RunTo(TrialEventKind.Start);
        rig.Wheel(1000);
        var ran = rig.Events.Last(e => e.Kind == TrialEventKind.Outcome).Outcome;
        Assert.AreEqual(TrialOutcomes.NoResponse, ran.Row.Value.outcome);
        Assert.IsTrue(ran.Penalised && ran.PenaltyTimeoutSeconds == 5 && ran.Row.Value.penaltyTimeoutSeconds == 5 && double.IsNaN(ran.PenaltyLevelDbfs));
        Assert.AreEqual(0.5, ran.Row.Value.penaltyFlashBrightness);
        Assert.IsTrue(ran.PenaltyFlash.Plays && ran.PenaltyFlash.Brightness == 0.5 && ran.PenaltyFlash.RateHz == 5);
        // The wrong-lick limit is incorrect: penalty.incorrect's values whatever noResponse says, its flash off.
        task = Fixture.Task(4);
        task.Penalty.NoResponse.TimeoutSeconds = 5;
        task.Penalty.NoResponse.Sound.LevelDbfs = "mute";
        task.Penalty.NoResponse.Flash.Brightness = 0.5;
        rig = new Rig(Fixture.Setup(task));
        rig.RunTo(TrialEventKind.Start);
        rig.Until(rig.Now + 0.2);
        for (int i = 0; i < 5; i++) rig.Lick();
        var wrong = rig.Events.Single(e => e.Kind == TrialEventKind.Outcome).Outcome;
        Assert.AreEqual("wrongLickLimit", wrong.Row.Value.outcomeReason);
        Assert.IsTrue(wrong.Penalised && wrong.PenaltyTimeoutSeconds == 4 && wrong.Row.Value.penaltyTimeoutSeconds == 4 && wrong.PenaltyLevelDbfs == -35);
        Assert.IsTrue(wrong.Row.Value.penaltyFlashBrightness == 0 && !wrong.PenaltyFlash.Plays);
    }

    [TestMethod]
    public void APenaltyNoResponseOfNullIsRefusedAsMissesArePenalised()
    {
        var task = Fixture.Task(4);
        task.Penalty.NoResponse = null;
        StringAssert.Contains(Assert.ThrowsException<InvalidOperationException>(() => Fixture.Setup(task)).Message, "penalty.noResponse is null, but this task penalises a miss");
    }

    [TestMethod]
    public void TheMaxPenaltyLevelCountsAMissesLevelSetByACondition()
    {
        var task = Fixture.Task(2);
        task.TrialConditions = new List<TrialCondition>
        {
            new TrialCondition { Weight = 1, Set = new Dictionary<object, object> { { "visualStimulus.contrasts", new List<object> { 0.0 } } } },
            new TrialCondition { Weight = 1, Set = new Dictionary<object, object> { { "penalty.noResponse.sound.levelDbfs", -30.0 } } },
            new TrialCondition { Weight = 0, Set = new Dictionary<object, object> { { "penalty.noResponse.sound.levelDbfs", -20.0 } } },
        };
        var rules = Fixture.Setup(task);
        Assert.AreEqual(-30.0, rules.MaxPenaltyLevelDbfs, "the weight-0 condition's -20 is never drawn");
        var rig = new Rig(rules);
        var seen = new Dictionary<int, double>();
        for (int i = 0; i < 12; i++)
        {
            rig.RunTo(TrialEventKind.Start);
            rig.Wheel(1000 * (i + 1));
            var outcome = rig.Events.Last(e => e.Kind == TrialEventKind.Outcome).Outcome;
            seen[outcome.Row.Value.trialConditionId] = outcome.PenaltyLevelDbfs;
        }
        Assert.AreEqual(-30.0, seen[1], "the condition's miss level");
        Assert.AreEqual(-35.0, seen[0], "the file's own penalty.noResponse level");
    }

    [TestMethod]
    public void TheMissesOwnLevelAndTimeoutAreCheckedAsTheChoices()
    {
        var task = Fixture.Task(4);
        task.Penalty.NoResponse.Sound.LevelDbfs = 0.0;
        task.Penalty.NoResponse.TimeoutSeconds = double.NaN;
        var message = Assert.ThrowsException<InvalidOperationException>(() => Fixture.Setup(task)).Message;
        StringAssert.Contains(message, "penalty.noResponse.sound.levelDbfs (0) must be from -120 to -1 dBFS");
        StringAssert.Contains(message, "penalty.noResponse.timeoutSeconds must be a finite number, 0 or more");
    }

    [TestMethod]
    public void EachTrialsPenaltyLevelFollowsItsConditionAndTheMaxIsTheBufferLevel()
    {
        Func<object, object, List<TrialCondition>> conditions = (a, b) => new List<TrialCondition>
        {
            new TrialCondition { Weight = 1, Set = new Dictionary<object, object> { { "penalty.noResponse.sound.levelDbfs", a }, { "visualStimulus.contrasts", new List<object> { 0.0 } } } },
            new TrialCondition { Weight = 1, Set = new Dictionary<object, object> { { "penalty.noResponse.sound.levelDbfs", b } } },
        };
        var task = Fixture.Task(2);   // no auto-reward: a trial run to the track's end is a penalised miss
        task.Penalty.Incorrect.Sound.LevelDbfs = "mute";   // so the max is the misses' alone
        task.TrialConditions = conditions(-40.0, "mute");
        var rules = Fixture.Setup(task);
        Assert.AreEqual(-40.0, rules.MaxPenaltyLevelDbfs);
        var seen = new Dictionary<int, double>();
        var rig = new Rig(rules);
        for (int i = 0; i < 12; i++)
        {
            rig.RunTo(TrialEventKind.Start);
            rig.Wheel(1000 * (i + 1));   // the wheel keeps its place between trials
            var outcome = rig.Events.Last(e => e.Kind == TrialEventKind.Outcome).Outcome;
            Assert.IsTrue(outcome.Penalised);
            seen[outcome.Row.Value.trialConditionId] = outcome.PenaltyLevelDbfs;
        }
        Assert.AreEqual(-40.0, seen[0]);
        Assert.IsTrue(double.IsNaN(seen[1]), "mute is NaN: the sound never plays");
        task.TrialConditions = conditions("mute", -30.0);
        Assert.AreEqual(-30.0, Fixture.Setup(task).MaxPenaltyLevelDbfs);
        task.TrialConditions = conditions("mute", "mute");
        Assert.IsTrue(double.IsNaN(Fixture.Setup(task).MaxPenaltyLevelDbfs));
        // The reward tone follows its condition too: one at -50, one mute (auto-reward trials, each rewarded on the next pip).
        var tones = Fixture.Task(1);
        tones.TrialConditions = new List<TrialCondition>
        {
            new TrialCondition { Weight = 1, Set = new Dictionary<object, object> { { "reward.tone.levelDbfs", -50.0 } } },
            new TrialCondition { Weight = 1, Set = new Dictionary<object, object> { { "reward.tone.levelDbfs", "mute" } } },
        };
        var toneRules = Fixture.Setup(tones);
        Assert.AreEqual(-50.0, toneRules.MaxRewardToneLevelDbfs);
        var toneSeen = new Dictionary<int, double>();
        var toneRig = new Rig(toneRules);
        for (int i = 0; i < 12; i++)
        {
            toneRig.RunTo(TrialEventKind.Start);
            toneRig.Wheel(1000 * (i + 1));
            var reward = toneRig.RunTo(TrialEventKind.Outcome).Outcome;
            Assert.IsTrue(reward.Rewarded);
            toneSeen[reward.Row.Value.trialConditionId] = reward.RewardToneLevelDbfs;
        }
        Assert.AreEqual(-50.0, toneSeen[0]);
        Assert.IsTrue(double.IsNaN(toneSeen[1]), "mute is NaN: the tone never plays");
    }

    [TestMethod]
    public void DistancesAreLoggedInTrackCmAtTheTrialsWheelGain()
    {
        var task = Fixture.Task(2);
        task.Track.WheelGain = 2;
        task.Track.LengthsCm = new List<double> { 100 };
        task.StimulusCutoff.TrackFractions = new List<double> { 0.5 };
        var rig = new Rig(Fixture.Setup(task));
        var start = rig.RunTo(TrialEventKind.Start);
        Assert.AreEqual(50, start.Trial.trackLengthWheelCm, 1e-9);   // 100 track cm: 50 wheel cm
        rig.Wheel(10);
        rig.Until(rig.Now + 0.2);
        Assert.AreEqual(20, rig.Events.Last(e => e.Kind == TrialEventKind.Update).Update.Row.positionCm, 1e-9);   // 10 wheel cm run
        rig.Wheel(20);
        rig.Until(rig.Now + 0.1);
        var row = rig.Events.Last(e => e.Kind == TrialEventKind.Update).Update.Row;
        Assert.AreEqual(40, row.positionCm, 1e-9);
        rig.Wheel(1000);
        var logged = rig.Events.Last(e => e.Kind == TrialEventKind.Outcome).Outcome.Row.Value;
        Assert.AreEqual(100, logged.trackLengthCm, 1e-9);
        Assert.AreEqual(100, logged.outcomePositionCm, 1e-9);
        // A cutoff trial's off position is in track cm too: the tone off at half the track, 50 track cm.
        var off = Fixture.Task(2);
        off.Track.WheelGain = 2;
        off.Track.LengthsCm = new List<double> { 100 };
        off.StimulusCutoff.TrackFractions = new List<double> { 0.5 };
        off.TrialConditions = new List<TrialCondition> { new TrialCondition { Weight = 1, Set = new Dictionary<object, object> { { "stimulusCutoff.stimuli", "auditory" } } } };
        var rig2 = new Rig(Fixture.Setup(off));
        rig2.RunTo(TrialEventKind.Start);
        rig2.Wheel(30);
        rig2.Until(rig2.Now + 0.1);
        rig2.Wheel(1000);
        var row2 = rig2.Events.Last(e => e.Kind == TrialEventKind.Outcome).Outcome.Row.Value;
        Assert.AreEqual(60, row2.auditoryCutoffPositionCm, 1e-9);   // the first pip after 30 wheel cm, past the half track (50 track cm)
        Assert.IsTrue(double.IsNaN(row2.visualCutoffPositionCm));
        Assert.AreEqual("audiovisualCoherentTruncated", row2.trialType);   // a cutoff condition's trialType takes Truncated
    }

    [TestMethod]
    public void ACutPointOfOneNeverSwitchesAStimulusCutoff()
    {
        // 1.0 is the track's end: never during the trial, on a trial ended at the track's end (past 0.9995, which rounds
        // to 1.000) and on an auto-reward trial whose last pip plays at the end; both off positions NaN.
        foreach (var autoReward in new[] { 0.0, 1.0 })
        {
            var task = Fixture.Task(2);
            task.Track.LengthsCm = new List<double> { 100 };
            task.AutoRewardProbability = autoReward;
            task.StimulusCutoff.TrackFractions = new List<double> { 1.0 };
            task.TrialConditions = new List<TrialCondition> { new TrialCondition { Weight = 1, Set = new Dictionary<object, object> { { "stimulusCutoff.stimuli", "both" } } } };
            var rig = new Rig(Fixture.Setup(task));
            var trial = rig.RunTo(TrialEventKind.Start).Trial;
            Assert.IsTrue(double.IsNaN(trial.cutoffFraction));
            rig.Wheel(trial.trackLengthWheelCm * 0.9996);
            rig.Until(rig.Now + 0.1);
            rig.Wheel(trial.trackLengthWheelCm + 1);
            rig.Until(rig.Now + 0.2);
            var outcome = rig.Events.Last(e => e.Kind == TrialEventKind.Outcome).Outcome.Row.Value;
            var rows = rig.Events.Where(e => e.Kind == TrialEventKind.Update).Select(e => e.Update.Row).ToList();
            Assert.IsTrue(rows.Count > 0);
            Assert.IsTrue(rows.All(r => r.auditoryLevelDbfs.Equals(trial.auditoryLevelDbfs) && r.visualContrast == trial.visualContrast), "autoReward " + autoReward);
            Assert.IsTrue(double.IsNaN(outcome.auditoryCutoffPositionCm) && double.IsNaN(outcome.visualCutoffPositionCm), "autoReward " + autoReward);
        }
    }

    [TestMethod]
    public void ALargeRewardIsOfferedAtLargeProbabilityOne()
    {
        var task = Fixture.Task(2);
        task.Reward.LargeProbability = 1;
        task.Reward.LargeMultiplier = 2.5;
        task.Reward.SizeUl = 3;
        var draw = Fixture.Draw(task);
        Assert.IsTrue(Enumerable.Range(0, 50).All(i => draw.Next().rewardOfferedUl == 7.5));
        var rig = new Rig(Fixture.Setup(task));
        rig.RunTo(TrialEventKind.Start);
        rig.Wheel(1000);
        var row = rig.Events.Last(e => e.Kind == TrialEventKind.Outcome).Outcome.Row.Value;
        Assert.AreEqual(7.5, row.rewardOfferedUl, "the row logs the offer on a trial not rewarded too");
        task.Reward.LargeProbability = 0;
        Assert.IsTrue(Enumerable.Range(0, 50).All(i => Fixture.Draw(task).Next().rewardOfferedUl == 3));
        task.Reward.LargeProbability = 0.5;
        var mixed = Fixture.Draw(task);
        var offers = Enumerable.Range(0, 1000).Select(i => mixed.Next().rewardOfferedUl).ToList();
        CollectionAssert.AreEquivalent(new[] { 3.0, 7.5 }, offers.Distinct().ToList());
        Assert.AreEqual(0.5, offers.Count(o => o == 7.5) / 1000.0, 0.06);
    }

    [TestMethod]
    public void TheGratingsPhaseIsDrawnPerTrialInZeroToTwoPiAndLogged()
    {
        var draw = Fixture.Draw(Fixture.Task(2));
        var trials = Enumerable.Range(0, 100).Select(i => draw.Next()).ToList();
        Assert.IsTrue(trials.All(t => t.visualPhaseRad >= 0 && t.visualPhaseRad < 2 * Math.PI));
        Assert.IsTrue(trials.Select(t => t.visualPhaseRad).Distinct().Count() > 90, "a phase per trial");
        var rig = new Rig(Fixture.Setup(Fixture.Task(2)));
        var start = rig.RunTo(TrialEventKind.Start);
        var pip = rig.RunTo(TrialEventKind.Update).Update;
        Assert.AreEqual(start.Trial.visualPhaseRad, pip.Grating.PhaseRad);
        rig.Wheel(1000);
        Assert.AreEqual(start.Trial.visualPhaseRad, rig.Events.Last(e => e.Kind == TrialEventKind.Outcome).Outcome.Row.Value.visualPhaseRad);
    }
}
