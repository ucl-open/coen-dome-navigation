using Bonsai;
using Bonsai.Harp;
using System;
using System.ComponentModel;
using System.Reactive.Linq;

/// <summary>A BonsaiData_Trial row; fields in schemas\trial-columns.yml's order, after harpTime (the row's timestamp, its
/// outcome). The fields up to laserTriggerTime are the lab's shared block, its core with the opto and quietWait blocks (lib-open-rigs
/// open_rigs.rigs.trials), in coen-dome-decision-making's order and words too (Pip, 14 Nov 2026);
/// this task's own follow.</summary>
public class TrialRow
{
    public int trialNumber;
    public string trialType;
    public string outcome;
    public double itiStartTime;
    public double itiSeconds;
    public double quietWaitSeconds;
    public double trialStartTime;
    public double stimulusOnsetTime;
    public double outcomeTime;
    public double rewardSizeUl;
    public bool penaltySound;
    public double penaltyTimeoutSeconds;
    public int laserCondition;
    public double laserPowerMw;
    public double laserTriggerTime;     // NaN without optogenetics or on a trial that ended before its trigger
    public string endReason;
    public double trackLengthCm;
    public double endPositionCm;
    public double toneEndFrequencyHz;
    public double visualEndSpatialFrequencyCpd;
    public double truncationFraction;
}

[Combinator]
[Description("An Outcome event's trial row, stamped with the Harp time the outcome was known, for LogTrial.")]
[WorkflowElementCategory(ElementCategory.Transform)]
public class CreateTrialRow
{
    public IObservable<Timestamped<TrialRow>> Process(IObservable<CorridorEvent> source)
    {
        return source.Where(e => e.Kind == CorridorEventKind.Outcome).Select(e => Timestamped.Create(e.Row, e.Seconds));
    }
}
