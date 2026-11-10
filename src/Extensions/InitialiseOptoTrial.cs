using Bonsai;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Reactive.Linq;
using UclOpenCoenDomeNavigationTask;

// The optogenetics part of av-dome's InitialiseTrial operator, rewritten for this task (zapit-v2). The rest of the trial is
// drawn by main.bonsai, so this only decides, for each new trial, whether the laser is on and which Zapit condition is presented.
// Differences from av-dome:
// - Laser-off trials: in an optogenetics experiment every trial sends a StartStimulus. Laser-off trials (the first trialsBeforeLaser included) still get
//   a real condition, so the galvos move and the masking light comes on exactly as on laser-on trials.
// - Each trial's condition is drawn at random from all of them, independently of every other trial: a shuffled queue made a site unlikely to follow itself and the end of each cycle predictable, which confounds trial-history analyses (Pip, 10 Nov 2026).
// - OptogeneticMaxDuration (laserMaxSeconds) is set on every trial: Bonsai stops the stimulus at the outcome or after this many
//   seconds, whichever comes first. Zapit itself only ever gets continuous stimuli (it ignores StopStimulus for timed ones).
// - Conditions come from ZapitConditions (no YAML library), which also checks they are numbered 1 to N.
[Combinator]
[Description("Draws the optogenetics for each new trial: laser on or off, and which condition from the Zapit conditions file to present. In an optogenetics experiment every trial gets a condition (laser-off trials included). Input is the trial number (from 1) and the task parameters.")]
[WorkflowElementCategory(ElementCategory.Transform)]
public class InitialiseOptoTrial
{
    public InitialiseOptoTrial()
    {
        Seed = -1;
    }

    [Description("Seed for the stimulation draws. -1 (the default) seeds them from the task's rngSeed, or randomly when it is null. Every trial's draw is in BonsaiData_Trial (laserPowerMw, laserCondition), so an experiment can be reconstructed.")]
    public int Seed { get; set; }

    // Instance state: this operator sits inside TaskLogic's per-trial Defer, which calls Process again on every trial with
    // the same instance, so state kept inside Process would be lost between trials. Reset on trial 1, so each experiment starts
    // with a fresh random stream.
    private Random random;
    private List<int> conditions; // null until read from the Zapit conditions file, on the first trial of an optogenetics experiment

    public IObservable<OptoTrial> Process(IObservable<Tuple<int, UclOpenCoenDomeNavigationTaskParameters>> source)
    {
        // Trial numbers start at 1; anything below is ignored.
        return source.Where(input => input.Item1 > 0).Select(input => Draw(input.Item1, input.Item2));
    }

    private OptoTrial Draw(int trialNumber, UclOpenCoenDomeNavigationTaskParameters parameters)
    {
        if (parameters == null)
        {
            throw new ArgumentNullException("parameters", "InitialiseOptoTrial needs the task parameters.");
        }
        if (trialNumber == 1 || random == null)
        {
            Reset(parameters);
        }

        var trial = new OptoTrial();
        trial.TrialNumber = trialNumber;
        var optogenetics = parameters.Optogenetics;
        if (optogenetics == null || !optogenetics.UseZapit)
        {
            // Not an optogenetics experiment: nothing is sent to Zapit.
            trial.OptogeneticEnabled = false;
            trial.OptogeneticLaserPowerMw = 0;
            trial.OptogeneticCondition = 0;
            trial.OptogeneticMaxDuration = 0;
            return trial;
        }

        if (conditions == null)
        {
            Validate(optogenetics, parameters);
            conditions = ZapitConditions.ReadConditionNumbers(optogenetics.ZapitConfigPath);
            Console.WriteLine("InitialiseOptoTrial: Zapit conditions file '" + optogenetics.ZapitConfigPath + "' has " +
                conditions.Count + " conditions");
        }

        // No laser-on trial among the first trialsBeforeLaser; after them the laser is on with probability laserOnProbability.
        var laserOn = trialNumber > optogenetics.TrialsBeforeLaser && random.NextDouble() < optogenetics.LaserOnProbability;
        trial.OptogeneticEnabled = laserOn;
        trial.OptogeneticLaserPowerMw = laserOn ? optogenetics.LaserPowerMw.Value : 0;
        trial.OptogeneticCondition = conditions[random.Next(conditions.Count)];
        trial.OptogeneticMaxDuration = optogenetics.LaserMaxSeconds;
        return trial;
    }

    private void Reset(UclOpenCoenDomeNavigationTaskParameters parameters)
    {
        // Seed, else the task's rngSeed (one on from TaskRandom's, so the stimulation draws are not the trial draws over
        // again; Pip, 9 Nov 2026), else a random one.
        random = Seed != -1 ? new Random(Seed)
            : parameters.RngSeed.HasValue ? new Random(unchecked((int)parameters.RngSeed.Value + 1))
            : new Random(Guid.NewGuid().GetHashCode());
        conditions = null;
    }

    const double BeepIntervalSec = 0.05;        // HarpBeat's beep; task.py BEEP_INTERVAL_SEC
    const double LaserMaxMarginSec = 0.25;  // task.py LASER_MAX_MARGIN_SEC

    // The task schema enforces most of these, but rig-control only checks the schema, not task.py's cross-field rules,
    // so they are checked again here: a bad value would silently change every trial.
    private static void Validate(OptogeneticsConfig optogenetics, UclOpenCoenDomeNavigationTaskParameters parameters)
    {
        if (!(optogenetics.LaserOnProbability >= 0 && optogenetics.LaserOnProbability <= 1))
        {
            throw new InvalidOperationException(string.Format(
                "optogenetics.laserOnProbability must be between 0 and 1; got {0}.", optogenetics.LaserOnProbability));
        }
        if (!(optogenetics.LaserPowerMw > 0) || double.IsInfinity(optogenetics.LaserPowerMw.Value))
        {
            throw new InvalidOperationException(string.Format(
                "optogenetics.laserPowerMw must be a power in mW above 0 when optogenetics is on; got {0}.",
                optogenetics.LaserPowerMw.HasValue ? optogenetics.LaserPowerMw.Value.ToString() : "nothing"));
        }
        // The stimulus starts on a beep and the first tone pip comes LaserToStimulusSeconds later, so LaserToStimulusSeconds must be 1 to 4 whole beeps.
        // At most 4 (0.2 s): licks before the first pip count toward the wrong-lick limits, and a longer lead could end a
        // trial before its first pip, which leaves the trial loop without an outcome.
        var beeps = optogenetics.LaserToStimulusSeconds / BeepIntervalSec;
        if (!(Math.Abs(beeps - Math.Round(beeps)) < 1e-6 && Math.Round(beeps) >= 1 && Math.Round(beeps) <= 4))
        {
            throw new InvalidOperationException(string.Format(
                "optogenetics.laserToStimulusSeconds must be a multiple of 0.05 s from 0.05 to 0.2; got {0}.", optogenetics.LaserToStimulusSeconds));
        }
        // The laser is meant to stay on until the outcome, so the cap must be longer than the longest trial: the time limit
        // (trialStructure.timeLimitSeconds, main.bonsai's MaxTrialTimeSec, counted from the first tone pip, LaserToStimulusSeconds after onset) plus a margin.
        var timeLimit = parameters.TrialStructure != null ? parameters.TrialStructure.TimeLimitSeconds : 12.0;
        var shortestCap = timeLimit + optogenetics.LaserToStimulusSeconds + LaserMaxMarginSec;
        if (!(optogenetics.LaserMaxSeconds >= shortestCap - 1e-9) || double.IsInfinity(optogenetics.LaserMaxSeconds))
        {
            throw new InvalidOperationException(string.Format(
                "optogenetics.laserMaxSeconds must be at least {0} s (trialStructure.timeLimitSeconds, {1} s from the first tone pip, plus laserToStimulusSeconds, "
                + "plus {2} s), or a timed-out trial's laser would stop before its outcome; got {3}.", shortestCap, timeLimit, LaserMaxMarginSec, optogenetics.LaserMaxSeconds));
        }
    }
}

// Optogenetics for one trial. Field names match av-dome's Trial class where they overlap.
public class OptoTrial
{
    public int TrialNumber;  // Trial this draw is for, as in the TrialNumber subject
    public bool OptogeneticEnabled;  // Laser on. False on laser-off trials and in experiments without optogenetics
    public double OptogeneticLaserPowerMw;  // Laser power sent with StartStimulus, in mW (the task's laserPowerMw). 0 on laser-off trials and when optogenetics is off
    public int OptogeneticCondition;  // Zapit condition (its stimLocations number) presented on this trial, laser on or off. 0 when optogenetics is off
    public double OptogeneticMaxDuration;  // Longest the stimulus stays on, in seconds; normally stopped at the outcome. 0 when optogenetics is off

    public override string ToString()
    {
        return string.Format(
            "OptoTrial(trial={0}, laserOn={1}, laserPowerMw={2}, condition={3}, maxDuration={4})",
            TrialNumber, OptogeneticEnabled, OptogeneticLaserPowerMw, OptogeneticCondition, OptogeneticMaxDuration);
    }
}
