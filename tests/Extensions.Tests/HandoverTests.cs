using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenRigs.Core;
using OpenRigs.Trials;
using OpenRigs.Devices.Zapit;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UclOpenCoenDomeNavigationTask;
using YamlDotNet.RepresentationModel;
using BuiltTrialCondition = OpenRigs.Trials.BuiltTrialCondition<UclOpenCoenDomeNavigationTask.UclOpenCoenDomeNavigationTaskParameters>;
using TrialEventOf = OpenRigs.Trials.TrialEvent<Trial, StimulusFrame, TrialOutcome>;

/// <summary>The task files app-rig-control writes at launch (tests\fixtures\handover, golden files its tests\test_handover.py
/// writes for every stage of the share's curricula; HANDOVER_DIR names another folder, as its scripts\mutation_check.py
/// does), each read as the workflow reads it (ReadTask) and drawn by this repo's C#: the draw and the trials run by lib-open-rigs' TrialLoop with a pretend mouse running and licking at random, each logged row
/// checked. The share's curriculum has one condition per stage, so the weights' shares prove little here; the combinations'
/// shares (track lengths) do.</summary>
[TestClass]
public class HandoverTests
{
    const int Draws = 5000;   // trials drawn per file for the shares
    const int Run = 200;      // trials run through the loop per file

    // Each golden file (OpenRigs.Trials GoldenTaskFiles: its text, rngSeed set to 1, as app-rig-control leaves it null for
    // the workflow to draw), its task as ReadTask reads it, and its conditions as written, read apart from the draw's own
    // reader: their weights (a condition stores no trialType: it is derived, and test_combinations.py holds the derived one
    // in Settings_Combinations to the Python build's).
    class Golden
    {
        public string Name, Text;
        public UclOpenCoenDomeNavigationTaskParameters Task;
        public List<int> Written;
    }

    static List<Golden> Goldens()
    {
        return GoldenTaskFiles.Read(Path.Combine(Fixture.Repo(), "tests", "fixtures", "handover")).Select(f =>
        {
            var stream = new YamlStream();
            stream.Load(new StringReader(f.Value));
            var root = (YamlMappingNode)((YamlMappingNode)stream.Documents[0].RootNode).Children[new YamlScalarNode("taskParameters")];
            var written = ((YamlSequenceNode)root.Children[new YamlScalarNode("trialConditions")]).Children.Cast<YamlMappingNode>().Select(c =>
                int.Parse(((YamlScalarNode)c.Children[new YamlScalarNode("weight")]).Value)).ToList();
            return new Golden { Name = f.Key, Text = f.Value, Task = TaskYaml.Read<UclOpenCoenDomeNavigationTaskLogic>(f.Value).TaskParameters, Written = written };
        }).ToList();
    }

    static List<BuiltTrialCondition> Built(Golden golden)
    {
        UclOpenCoenDomeNavigationTaskParameters read;
        try { return TrialDraw.CheckTask(golden.Text, Fixture.Sweep(), Fixture.Speakers(), out read); }
        catch (InvalidOperationException e) { Assert.Fail(golden.Name + ": " + e.Message); return null; }
    }

    [TestMethod]
    public void EachGoldenFileBuildsItsConditionsWithTheirWeights()
    {
        foreach (var golden in Goldens())
        {
            var built = Built(golden);
            CollectionAssert.AreEqual(golden.Written, built.Select(c => c.Weight).ToList(), golden.Name);
        }
    }

    [TestMethod]
    public void EachCombinationIsDrawnAtItsConditionsShareOfTheWeights()
    {
        foreach (var golden in Goldens())
        {
            var built = Built(golden);
            var draw = Fixture.Draw(golden.Text);
            var counts = new Dictionary<int, int>();
            for (int n = 0; n < Draws; n++)
            {
                var t = draw.Next();
                Check(golden.Name, built, t.trialType, t.trialConditionId, t.trialCombinationId, t.visualContrast, t.auditoryLevelDbfs, t.auditoryInitialAzimuthDeg, t.TrackLengthCm, t.WheelGain, t.rewardOfferedUl);
                counts[t.trialCombinationId] = (counts.ContainsKey(t.trialCombinationId) ? counts[t.trialCombinationId] : 0) + 1;
            }
            var problems = GoldenTaskFiles.ShareProblems(golden.Name, built, counts, Draws);
            Assert.AreEqual(0, problems.Count, string.Join("; ", problems));
        }
    }

    // One trial against its combination (the file's values as BuildConditions picked them): its trialType the file's, the grating's contrast, the tone's level on the speaker nearest its azimuth (NaN silent or
    // at all), the track (track cm, and its wheel gain) and the reward.
    static void Check(string file, List<BuiltTrialCondition> built, string trialType, int condition, int combinationId, double contrast,
        double levelDbfs, double auditoryAzimuth, double trackLengthCm, double wheelGain, double rewardOfferedUl)
    {
        var where = file + " " + built[condition].Where + " combination " + combinationId;
        Assert.AreEqual(built[condition].TrialType, trialType, where);
        // The trial's trialCombinationId is a row of its own condition.
        var combination = built[condition].Combinations.SingleOrDefault(m => m.Id == combinationId);
        Assert.IsNotNull(combination, where + ": not a combination of its condition");
        var c = combination.Task;
        Assert.AreEqual(c.VisualStimulus.Contrasts[0], contrast, where);
        Assert.AreEqual(SoundLevels.Parse(c.AuditoryStimulus.LevelDbfs), levelDbfs, where);
        var azimuth = SoundAzimuths.Parse(c.AuditoryStimulus.InitialAzimuthsDeg[0]).Value;
        Assert.AreEqual(!double.IsNaN(levelDbfs) && !double.IsNaN(azimuth) ? SoundAzimuths.NearestSpeaker(Fixture.Speakers(), azimuth, 0).Value.Position.Azimuth : double.NaN, auditoryAzimuth, where);
        Assert.AreEqual(c.Track.LengthsCm[0], trackLengthCm, 1e-9, where);
        Assert.AreEqual(c.Track.WheelGain, wheelGain, where);
        Assert.IsTrue(rewardOfferedUl == c.Reward.SizeUl || rewardOfferedUl == c.Reward.SizeUl * c.Reward.LargeMultiplier, where);
    }

    [TestMethod]
    public void EachGoldenFileRunsInTheTrialLoopAndLogsItsCombinationsValues()
    {
        // Item 3: the trials as the workflow runs them (TrialRules on lib-open-rigs' TrialLoop, a 1 ms clock), a pretend
        // mouse running at a speed drawn per trial and licking the left spout at random between the stimulus and the outcome;
        // each row as LogTrials writes it.
        foreach (var golden in Goldens())
        {
            var built = Built(golden);
            var rig = new Rig(Fixture.Setup(golden.Text));
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
                Assert.IsTrue(rig.Now < Run * 60, golden.Name + ": the loop stalled at " + rows.Count + " rows");
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
                Check(golden.Name, built, r.trialType, r.trialConditionId, r.trialCombinationId, r.visualContrast, r.auditoryLevelDbfs, r.auditoryInitialAzimuthDeg, r.trackLengthCm, trials[r.trialNumber].WheelGain, r.rewardOfferedUl);
                Assert.AreEqual(trials[r.trialNumber].trialCombinationId, r.trialCombinationId, golden.Name);
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
            foreach (var condition in golden.Task.TrialConditions)
            {
                var set = condition.Set as System.Collections.IDictionary;
                if (set != null) foreach (var key in set.Keys) allowed.Add(key.ToString());
                foreach (var group in condition.Pairs ?? new List<List<string>>()) foreach (var path in group) allowed.Add(path);
            }
            var json = new TrialRules(golden.Text, Fixture.Sweep(), true, path => new ZapitConditionsFile(2, 0.0), Fixture.Speakers()).CombinationsJson;
            var built = Built(golden);
            var own = TaskYaml.Flatten(golden.Task, TaskSchema.ListFields);
            var rows = (Newtonsoft.Json.Linq.JArray)Newtonsoft.Json.Linq.JObject.Parse(json)["rows"];
            var combinations = built.SelectMany((c, i) => c.Combinations.Select(m => Tuple.Create(i, c, m))).ToList();
            Assert.AreEqual(combinations.Count, rows.Count, golden.Name);
            for (int n = 0; n < rows.Count; n++)
            {
                var row = rows[n];
                var where = golden.Name + " row " + n;
                Assert.AreEqual(n, (int)row["trialCombinationId"], where);
                Assert.AreEqual(n, combinations[n].Item3.Id, where);
                Assert.AreEqual(combinations[n].Item1, (int)row["trialConditionId"], where);
                Assert.AreEqual(combinations[n].Item2.TrialType, (string)row["trialType"], where);
                Assert.AreEqual(combinations[n].Item2.Weight, (int)row["conditionWeight"], where);
                var values = (Newtonsoft.Json.Linq.JObject)row["values"];
                foreach (var p in values.Properties())
                    Assert.IsTrue(allowed.Any(a => p.Name == a || p.Name.StartsWith(a + ".")), where + ": " + p.Name + " is no set, pairs or list-field path");
                var rebuilt = new Dictionary<string, Newtonsoft.Json.Linq.JToken>(own);
                foreach (var p in values.Properties()) rebuilt[p.Name] = p.Value;
                var expected = TaskYaml.Flatten(combinations[n].Item3.Task, TaskSchema.ListFields);
                CollectionAssert.AreEquivalent(expected.Keys.ToList(), rebuilt.Keys.ToList(), where);
                foreach (var path in expected.Keys.Where(k => !TaskSchema.SessionFields.Any(f => k == f || k.StartsWith(f + "."))))
                    Assert.IsTrue(Newtonsoft.Json.Linq.JToken.DeepEquals(expected[path], rebuilt[path]), where + ": " + path);
            }
            var file = Path.Combine(folder, Path.ChangeExtension(golden.Name, ".json"));
            if (regen) File.WriteAllText(file, json);
            Assert.IsTrue(File.Exists(file), file + " is missing: run the C# tests with COMBINATIONS_REGEN=1");
            Assert.AreEqual(File.ReadAllText(file).Replace("\r\n", "\n"), json.Replace("\r\n", "\n"), golden.Name + ": Settings_Combinations differs from " + file + " (COMBINATIONS_REGEN=1 rewrites it)");
        }
    }
}
