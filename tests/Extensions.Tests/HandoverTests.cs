using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenRigs.Trials;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UclOpenCoenDomeNavigationTask;
using YamlDotNet.RepresentationModel;
using TrialEventOf = OpenRigs.Trials.TrialEvent<Trial, StimulusFrame, TrialOutcome>;

/// <summary>The task files app-rig-control writes at launch (tests\fixtures\handover, golden files its tests\test_handover.py
/// writes for every stage of the share's curricula; HANDOVER_DIR names another folder, as its scripts\mutation_check.py
/// does), each read as the workflow reads it (ReadTask) and drawn by this repo's C#: the draw, the old trial types' meaning,
/// and the trials run by lib-open-rigs' TrialLoop with a pretend mouse running and licking at random, each logged row
/// checked. The share's curriculum has one condition per stage, so the weights' shares prove little here; the combinations'
/// shares (track lengths) do.</summary>
[TestClass]
public class HandoverTests
{
    const int Draws = 5000;   // trials drawn per file for the shares
    const int Run = 200;      // trials run through the loop per file

    // What each old trial type showed (app-rig-control's OLD_CORRIDOR): the grating's contrast and the tone's amplitude on or
    // off. Convert-on-read keeps the old type's name as the trialType.
    static readonly Dictionary<string, bool[]> OldShows = new Dictionary<string, bool[]>
    {
        { "visual", new[] { true, false } },
        { "auditory", new[] { false, true } },
        { "audiovisualCoherent", new[] { true, true } },
        { "neutral", new[] { false, false } },
    };

    // Each golden file's name and task as ReadTask reads it, rngSeed set to 1 (app-rig-control leaves it null for the
    // workflow to draw), with the file's conditions as written, read apart from the draw's own reader: (trialType, weight).
    static List<Tuple<string, UclOpenCoenDomeNavigationTaskParameters, List<Tuple<string, int>>>> Goldens()
    {
        var folder = Environment.GetEnvironmentVariable("HANDOVER_DIR");
        if (string.IsNullOrEmpty(folder)) folder = Path.Combine(Fixture.Repo(), "tests", "fixtures", "handover");
        var files = Directory.GetFiles(folder, "*.yml").OrderBy(f => f, StringComparer.Ordinal).ToList();
        Assert.IsTrue(files.Count > 0, "no golden task files in " + folder);
        return files.Select(f =>
        {
            var text = File.ReadAllText(f);
            var task = TrialDraw.Read(text).TaskParameters;
            Assert.IsNull(task.RngSeed, f);
            task.RngSeed = 1;
            var stream = new YamlStream();
            stream.Load(new StringReader(text));
            var root = (YamlMappingNode)((YamlMappingNode)stream.Documents[0].RootNode).Children[new YamlScalarNode("taskParameters")];
            var written = ((YamlSequenceNode)root.Children[new YamlScalarNode("trialConditions")]).Children.Cast<YamlMappingNode>().Select(c => Tuple.Create(
                ((YamlScalarNode)c.Children[new YamlScalarNode("trialType")]).Value, int.Parse(((YamlScalarNode)c.Children[new YamlScalarNode("weight")]).Value))).ToList();
            return Tuple.Create(Path.GetFileName(f), task, written);
        }).ToList();
    }

    static List<TrialConditionDraw> Built(string name, UclOpenCoenDomeNavigationTaskParameters task)
    {
        try { return TrialDraw.CheckTask(task, Fixture.Sweep(), Fixture.Speakers()); }
        catch (InvalidOperationException e) { Assert.Fail(name + ": " + e.Message); return null; }
    }

    [TestMethod]
    public void EachGoldenFileBuildsItsConditionsWithTheirWeights()
    {
        foreach (var golden in Goldens())
        {
            var built = Built(golden.Item1, golden.Item2);
            CollectionAssert.AreEqual(golden.Item3.Select(w => w.Item1).ToList(), built.Select(c => c.TrialType).ToList(), golden.Item1);
            CollectionAssert.AreEqual(golden.Item3.Select(w => w.Item2).ToList(), built.Select(c => c.Weight).ToList(), golden.Item1);
        }
    }

    [TestMethod]
    public void EachCombinationIsDrawnAtItsConditionsShareOfTheWeights()
    {
        foreach (var golden in Goldens())
        {
            var built = Built(golden.Item1, golden.Item2);
            var draw = Fixture.Draw(golden.Item2);
            var counts = new Dictionary<string, int>();
            for (int n = 0; n < Draws; n++)
            {
                var t = draw.Next();
                Check(golden.Item1, built, t.trialType, t.trialCondition, t.trialCombination, t.visualContrast, t.auditoryAmplitude, t.auditoryAzimuthDeg, t.trackLengthCm, t.wheelGain, t.rewardOfferedUl);
                var key = t.trialCondition + "/" + t.trialCombination;
                counts[key] = (counts.ContainsKey(key) ? counts[key] : 0) + 1;
            }
            double total = built.Sum(c => c.Weight);
            for (int i = 0; i < built.Count; i++)
            {
                for (int k = 0; k < built[i].Combinations.Count; k++)
                {
                    var p = built[i].Weight / total / built[i].Combinations.Count;
                    var key = i + "/" + k;
                    var share = (counts.ContainsKey(key) ? counts[key] : 0) / (double)Draws;
                    Assert.IsTrue(Math.Abs(share - p) <= 4.5 * Math.Sqrt(p * (1 - p) / Draws), string.Format("{0} {1}: share {2}, expected {3}", golden.Item1, built[i].Where + " " + built[i].Combinations[k].Picks, share, p));
                }
            }
        }
    }

    // One trial against its combination (the file's values as BuildConditions picked them): its trialType the file's and
    // what its old type showed, the grating's contrast, the tone's amplitude on the speaker nearest its azimuth (NaN silent or
    // at all), the track (track cm over the wheel gain) and the reward.
    static void Check(string file, List<TrialConditionDraw> built, string trialType, int condition, int combination, double contrast,
        double amplitude, double auditoryAzimuth, double trackLengthCm, double wheelGain, double rewardOfferedUl)
    {
        var where = file + " " + built[condition].Where + " combination " + combination;
        Assert.AreEqual(built[condition].TrialType, trialType, where);
        var c = built[condition].Combinations[combination].Task;
        Assert.AreEqual(c.Contrasts[0], contrast, where);
        Assert.AreEqual(c.AuditoryStimulus.Amplitude, amplitude, where);
        var azimuth = TrialDraw.Azimuth(c.AuditoryStimulus.InitialAzimuthsDeg[0]);
        Assert.AreEqual(amplitude > 0 && azimuth is double ? TrialDraw.NearestSpeaker(Fixture.Speakers(), (double)azimuth).Value.Position.Azimuth : double.NaN, auditoryAzimuth, where);
        Assert.AreEqual(c.TrialStructure.TrackLengthsCm[0] / c.WheelGain, trackLengthCm, 1e-9, where);
        Assert.AreEqual(c.WheelGain, wheelGain, where);
        Assert.AreEqual(c.Reward.RewardSizeUl, rewardOfferedUl, where);
        // Item 2: what the old type showed
        Assert.IsTrue(OldShows.ContainsKey(trialType), where + ": no old type " + trialType);
        CollectionAssert.AreEqual(OldShows[trialType], new[] { contrast > 0, amplitude > 0 }, where + ": grating and tone on or off");
    }

    [TestMethod]
    public void EachGoldenFileRunsInTheTrialLoopAndLogsItsCombinationsValues()
    {
        // Item 3: the trials as the workflow runs them (TrialRules on lib-open-rigs' TrialLoop, a 1 ms clock), a pretend
        // mouse running at a speed drawn per trial and licking the left spout at random between the stimulus and the outcome;
        // each row as LogTrials writes it.
        foreach (var golden in Goldens())
        {
            var built = Built(golden.Item1, golden.Item2);
            var rig = new Rig(Fixture.Setup(golden.Item2));
            var mouse = new Random(1);
            var rows = new List<TrialRow>();
            var trials = new Dictionary<int, Trial>();
            bool running = false;
            double wheel = 0, speed = 0;
            Action<IEnumerable<TrialEventOf>> take = events =>
            {
                foreach (var e in events)
                {
                    if (e.Kind == TrialEventKind.Start) trials[e.Trial.trialNumber] = e.Trial;
                    if (e.Kind == TrialEventKind.Stimulus) { running = true; speed = 20 + 40 * mouse.NextDouble(); }
                    if (e.Kind == TrialEventKind.Outcome) { running = false; rows.Add(e.Outcome.Row.Value); }
                }
            };
            rig.Wheel(0);
            while (rows.Count < Run)
            {
                Assert.IsTrue(rig.Now < Run * 60, golden.Item1 + ": the loop stalled at " + rows.Count + " rows");
                rig.Until(rig.Now + 0.001);
                if (running)
                {
                    wheel += speed * 0.001;
                    rig.Wheel(wheel);
                    if (mouse.NextDouble() < 0.002) rig.Lick();
                }
                take(rig.Events);
                rig.Events.Clear();
            }
            foreach (var r in rows)
            {
                Check(golden.Item1, built, r.trialType, r.trialCondition, r.trialCombination, r.visualContrast, r.auditoryAmplitude, r.auditoryAzimuthDeg, r.trackLengthCm, r.wheelGain, r.rewardOfferedUl);
                Assert.AreEqual(trials[r.trialNumber].trialCombination, r.trialCombination, golden.Item1);
            }
        }
    }
}
