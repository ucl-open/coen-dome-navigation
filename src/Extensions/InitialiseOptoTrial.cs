using Bonsai;
using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.IO;
using System.Reactive.Linq;
using UclOpenCoenAvCorridorTask;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

// The optogenetics part of av-dome's InitialiseTrial operator (am-zapit-integration branch), ported for this task.
// The rest of the trial is drawn by main.bonsai, so this only decides whether each trial is stimulated and with which Zapit condition.
[Combinator]
[Description("Draws the optogenetics for each new trial: whether it is stimulated, and with which condition from the Zapit stimulus config. Input is the trial number (from 1) and the task parameters.")]
[WorkflowElementCategory(ElementCategory.Transform)]
public class InitialiseOptoTrial
{
    public InitialiseOptoTrial()
    {
        Seed = -1;
    }

    [Description("Seed for the stimulation draws. Set to -1 for a random seed (recommended). The draw for every trial is in BonsaiData_Trial (optoStimulated, optoCondition), so an experiment can be reconstructed either way.")]
    public int Seed { get; set; }

    // Instance state, as in av-dome's InitialiseTrial: this operator sits inside TaskLogic's per-trial Defer, which calls Process again on every trial,
    // so state kept inside Process would be lost between trials. It is reset on trial 1, so each experiment starts with a fresh queue.
    private Random rand;
    private readonly List<int> zapitConditions = new List<int>();
    private readonly List<int> conditionQueue = new List<int>();
    private bool configLoaded;

    public IObservable<OptoTrial> Process(IObservable<Tuple<int, UclOpenCoenAvCorridorTaskParameters>> source)
    {
        return source.Where(input => input.Item1 > 0).Select(input =>
        {
            var trialNumber = input.Item1;
            var optogenetics = input.Item2.Optogenetics;
            var trialData = new OptoTrial();
            trialData.TrialNumber = trialNumber;
            ClearOptogeneticParameters(trialData);

            if (trialNumber == 1 || rand == null)
            {
                rand = Seed != -1 ? new Random(Seed) : new Random(Guid.NewGuid().GetHashCode());
                conditionQueue.Clear();
                configLoaded = false;
            }

            // Check if optogenetics is enabled
            if (!optogenetics.Enabled)
                return trialData;

            if (!configLoaded)
            {
                LoadZapitConditions(optogenetics.ZapitConfigPath, zapitConditions);
                configLoaded = true;
                Console.WriteLine("Loaded Zapit config with " + zapitConditions.Count + " stimulation conditions");
            }

            if (trialNumber <= optogenetics.WarmupTrials)
            {
                Console.WriteLine("Optogenetics warmup: trial " + trialNumber + "/" + optogenetics.WarmupTrials + " - no stimulation");
                return trialData;
            }

            // Roll for stimulation
            if (rand.NextDouble() < optogenetics.StimProbability)
            {
                trialData.OptogeneticEnabled = true;

                // Pseudorandom condition selection
                if (optogenetics.PseudorandomSelection)
                {
                    trialData.OptogeneticCondition = GetPseudorandomCondition(rand, zapitConditions, conditionQueue);
                }
                else
                {
                    trialData.OptogeneticCondition = zapitConditions[rand.Next(zapitConditions.Count)];
                }

                // As av-dome's InitialiseTrial: Zapit starts the laser StimDelay after the trigger; the workflow stops it at the outcome, StimDuration capping it
                trialData.OptogeneticDelay = optogenetics.StimDelay;
                trialData.OptogeneticDuration = optogenetics.StimDuration;
            }
            return trialData;
        });
    }

    private static void LoadZapitConditions(string configPath, List<int> zapitConditions)
    {
        // An experiment that is meant to stimulate stops here rather than running without stimulation. The task schema refuses
        // an empty path before Go (rig-control checks it); a path that names no file is only found here, at trial 1.
        if (string.IsNullOrEmpty(configPath) || !File.Exists(configPath))
        {
            throw new InvalidOperationException("Optogenetics is enabled but the Zapit stimulus config was not found: '" + configPath + "'");
        }

        var yaml = File.ReadAllText(configPath);
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        var yamlData = deserializer.Deserialize<Dictionary<string, object>>(yaml);
        zapitConditions.Clear();

        foreach (var kvp in yamlData)
        {
            if (kvp.Key.StartsWith("stimLocations"))
            {
                var numberStr = kvp.Key.Substring("stimLocations".Length);
                int conditionNumber;  // Declare separately
                if (int.TryParse(numberStr, out conditionNumber))
                {
                    zapitConditions.Add(conditionNumber);
                }
            }
        }

        zapitConditions.Sort();
        if (zapitConditions.Count == 0)
        {
            throw new InvalidOperationException("Optogenetics is enabled but the Zapit stimulus config has no stimLocations entries: '" + configPath + "'");
        }
    }

    private static void ClearOptogeneticParameters(OptoTrial trialData)
    {
        trialData.OptogeneticEnabled = false;
        trialData.OptogeneticCondition = 0;
        trialData.OptogeneticDelay = 0;
        trialData.OptogeneticDuration = 0;
    }

    private static int GetPseudorandomCondition(Random rand, List<int> zapitConditions, List<int> conditionQueue)
    {
        // Initialize or refill the condition queue when empty
        if (conditionQueue.Count == 0)
        {
            conditionQueue.AddRange(zapitConditions);

            // Shuffle using Fisher-Yates
            int n = conditionQueue.Count;
            while (n > 1)
            {
                n--;
                int k = rand.Next(n + 1);
                int temp = conditionQueue[k];
                conditionQueue[k] = conditionQueue[n];
                conditionQueue[n] = temp;
            }
        }

        // Take the next condition from the shuffled queue
        int selectedCondition = conditionQueue[0];
        conditionQueue.RemoveAt(0);
        return selectedCondition;
    }
}

// Optogenetics for one trial. Field names match av-dome's Trial class, so the logs of the two tasks line up.
public class OptoTrial
{
    public int TrialNumber;  // Trial this draw is for, as in the TrialNumber subject
    public bool OptogeneticEnabled;  // Whether this trial is stimulated
    public int OptogeneticCondition;  // Zapit condition (its stimLocations number), 0 when not stimulated
    public double OptogeneticDelay;  // Seconds from the trigger (beep 0) to the laser, Zapit's StartDelay. 0 when not stimulated
    public double OptogeneticDuration;  // Longest the laser stays on, in seconds; it is stopped at the outcome. 0 when not stimulated
}
