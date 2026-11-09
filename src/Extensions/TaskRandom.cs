using Bonsai;
using System;
using System.ComponentModel;
using System.Reactive.Linq;
using UclOpenCoenDomeNavigationTask;

// The experiment's one random number generator, seeded with the task's rngSeed (Pip, 9 Nov 2026: rngSeed seeds
// every draw, as av-dome's). rig-control draws a null rngSeed at Go and writes it into the task file; from the
// editor a null seed is drawn here. The seed used is logged once as a RngSeed event in BonsaiData_Async.
public class TaskRandom
{
    public TaskRandom(int seed)
    {
        Seed = seed;
        Random = new Random(seed);
    }

    public int Seed { get; private set; }
    public Random Random { get; private set; }
}

[Combinator]
[Description("The experiment's random number generator, from the task file's rngSeed (a drawn seed when it is null). Draw from it with DrawIti and DrawTrackLength, so each draw follows from the seed.")]
[WorkflowElementCategory(ElementCategory.Transform)]
public class CreateTaskRandom
{
    public IObservable<TaskRandom> Process(IObservable<double?> source)
    {
        return source.Select(seed => new TaskRandom(seed.HasValue ? (int)seed.Value : Guid.NewGuid().GetHashCode()));
    }
}

[Combinator]
[Description("Draws the trial's inter-trial interval evenly between trialStructure.itiMinSeconds and itiMaxSeconds.")]
[WorkflowElementCategory(ElementCategory.Transform)]
public class DrawIti
{
    public IObservable<TimeSpan> Process(IObservable<Tuple<TaskRandom, UclOpenCoenDomeNavigationTaskParameters>> source)
    {
        return source.Select(value =>
        {
            var structure = value.Item2.TrialStructure;
            double u;
            lock (value.Item1.Random) u = value.Item1.Random.NextDouble();
            return TimeSpan.FromSeconds(structure.ItiMinSeconds + (structure.ItiMaxSeconds - structure.ItiMinSeconds) * u);
        });
    }
}

[Combinator]
[Description("Draws the trial's track length (one of trialStructure.trackLengthsCm, before the stage's scale) and a number in [0, 1) that decides whether the trial is truncated. Output: (track length, truncation draw).")]
[WorkflowElementCategory(ElementCategory.Transform)]
public class DrawTrackLength
{
    public IObservable<Tuple<double, double>> Process(IObservable<Tuple<TaskRandom, UclOpenCoenDomeNavigationTaskParameters>> source)
    {
        return source.Select(value =>
        {
            var lengths = value.Item2.TrialStructure.TrackLengthsCm;
            if (lengths == null || lengths.Count == 0)
            {
                throw new InvalidOperationException("trialStructure.trackLengthsCm is empty: list at least one track length.");
            }
            var random = value.Item1.Random;
            int pick;
            double truncation;
            lock (random)
            {
                pick = random.Next(lengths.Count);
                truncation = random.NextDouble();
            }
            return Tuple.Create(lengths[pick], truncation);
        });
    }
}
