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
            var counts = new Dictionary<int, int>();
            for (int n = 0; n < Draws; n++)
            {
                var t = draw.Next();
                Check(golden.Item1, built, t.trialType, t.trialCondition, t.trialCombinationId, t.visualContrast, t.auditoryAmplitude, t.auditoryAzimuthDeg, t.trackLengthCm, t.wheelGain, t.rewardOfferedUl);
                counts[t.trialCombinationId] = (counts.ContainsKey(t.trialCombinationId) ? counts[t.trialCombinationId] : 0) + 1;
            }
            double total = built.Sum(c => c.Weight);
            for (int i = 0; i < built.Count; i++)
            {
                for (int k = 0; k < built[i].Combinations.Count; k++)
                {
                    // Each Settings_Combinations row at its condition's weight over the sum of weights over its number of combinations.
                    var p = built[i].Weight / total / built[i].Combinations.Count;
                    var key = built[i].Combinations[k].Id;
                    var share = (counts.ContainsKey(key) ? counts[key] : 0) / (double)Draws;
                    Assert.IsTrue(Math.Abs(share - p) <= 4.5 * Math.Sqrt(p * (1 - p) / Draws), string.Format("{0} {1}: share {2}, expected {3}", golden.Item1, built[i].Where + " " + built[i].Combinations[k].Picks, share, p));
                }
            }
        }
    }

    // One trial against its combination (the file's values as BuildConditions picked them): its trialType the file's and
    // what its old type showed, the grating's contrast, the tone's amplitude on the speaker nearest its azimuth (NaN silent or
    // at all), the track (track cm over the wheel gain) and the reward.
    static void Check(string file, List<TrialConditionDraw> built, string trialType, int condition, int combinationId, double contrast,
        double amplitude, double auditoryAzimuth, double trackLengthCm, double wheelGain, double rewardOfferedUl)
    {
        var where = file + " " + built[condition].Where + " combination " + combinationId;
        Assert.AreEqual(built[condition].TrialType, trialType, where);
        // The trial's trialCombinationId is a row of its own condition.
        var combination = built[condition].Combinations.SingleOrDefault(m => m.Id == combinationId);
        Assert.IsNotNull(combination, where + ": not a combination of its condition");
        var c = combination.Task;
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
                Check(golden.Item1, built, r.trialType, r.trialCondition, r.trialCombinationId, r.visualContrast, r.auditoryAmplitude, r.auditoryAzimuthDeg, r.trackLengthCm, r.wheelGain, r.rewardOfferedUl);
                Assert.AreEqual(trials[r.trialNumber].trialCombinationId, r.trialCombinationId, golden.Item1);
            }
        }
    }

    [TestMethod]
    public void EachGoldenFileWritesSettingsCombinationsThatRebuildEveryCombination()
    {
        // Settings_Combinations (TrialRules.CombinationsJson) for each golden file: a row per combination in build order, each
        // row's values over the task's own normalised values rebuilding its combination (session fields aside); values only
        // at the conditions' set and pairs paths and the list fields (no constant leaks in). Kept in
        // tests\fixtures\combinations, which tests\test_combinations.py holds equal to task.py's condition_combinations;
        // COMBINATIONS_REGEN=1 rewrites them.
        var folder = Path.Combine(Fixture.Repo(), "tests", "fixtures", "combinations");
        var regen = Environment.GetEnvironmentVariable("COMBINATIONS_REGEN") == "1";
        if (regen) Directory.CreateDirectory(folder);
        foreach (var golden in Goldens())
        {
            var allowed = new HashSet<string>(TaskSchema.ListFields);
            foreach (var condition in golden.Item2.TrialConditions)
            {
                var set = condition.Set as System.Collections.IDictionary;
                if (set != null) foreach (var key in set.Keys) allowed.Add(key.ToString());
                foreach (var group in condition.Pairs ?? new List<List<string>>()) foreach (var path in group) allowed.Add(path);
            }
            var json = new TrialRules(golden.Item2, Fixture.Sweep(), true, path => 2, Fixture.Speakers()).CombinationsJson;
            var built = Built(golden.Item1, golden.Item2);
            var own = TrialDraw.Flatten(golden.Item2);
            var rows = (Newtonsoft.Json.Linq.JArray)Newtonsoft.Json.Linq.JObject.Parse(json)["rows"];
            var combinations = built.SelectMany((c, i) => c.Combinations.Select(m => Tuple.Create(i, c, m))).ToList();
            Assert.AreEqual(combinations.Count, rows.Count, golden.Item1);
            for (int n = 0; n < rows.Count; n++)
            {
                var row = rows[n];
                var where = golden.Item1 + " row " + n;
                Assert.AreEqual(n, (int)row["trialCombinationId"], where);
                Assert.AreEqual(n, combinations[n].Item3.Id, where);
                Assert.AreEqual(combinations[n].Item1, (int)row["trialCondition"], where);
                Assert.AreEqual(combinations[n].Item2.TrialType, (string)row["trialType"], where);
                Assert.AreEqual(combinations[n].Item2.Weight, (int)row["conditionWeight"], where);
                var values = (Newtonsoft.Json.Linq.JObject)row["values"];
                foreach (var p in values.Properties())
                    Assert.IsTrue(allowed.Any(a => p.Name == a || p.Name.StartsWith(a + ".")), where + ": " + p.Name + " is no set, pairs or list-field path");
                var rebuilt = new Dictionary<string, Newtonsoft.Json.Linq.JToken>(own);
                foreach (var p in values.Properties()) rebuilt[p.Name] = p.Value;
                var expected = TrialDraw.Flatten(combinations[n].Item3.Task);
                CollectionAssert.AreEquivalent(expected.Keys.ToList(), rebuilt.Keys.ToList(), where);
                foreach (var path in expected.Keys.Where(k => !TaskSchema.SessionFields.Any(f => k == f || k.StartsWith(f + "."))))
                    Assert.IsTrue(Newtonsoft.Json.Linq.JToken.DeepEquals(expected[path], rebuilt[path]), where + ": " + path);
            }
            var file = Path.Combine(folder, Path.ChangeExtension(golden.Item1, ".json"));
            if (regen) File.WriteAllText(file, json);
            Assert.IsTrue(File.Exists(file), file + " is missing: run the C# tests with COMBINATIONS_REGEN=1");
            Assert.AreEqual(File.ReadAllText(file).Replace("\r\n", "\n"), json.Replace("\r\n", "\n"), golden.Item1 + ": Settings_Combinations differs from " + file + " (COMBINATIONS_REGEN=1 rewrites it)");
        }
    }
}
