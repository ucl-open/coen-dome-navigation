/// <summary>A WorkflowData_Trials row; fields in schemas\trial-columns.yml's order, after harpTime (the row's timestamp, its
/// outcome). The fields up to autoReward are the lab's shared block, its core with the quietWait, optogenetics, conditions,
/// stimulus and autoReward blocks (lib-open-rigs open_rigs.rigs.trials), in coen-dome-decision-making's order and words too;
/// this task's own follow. Every distance is in track cm.</summary>
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
    public double stimulusOffTime;      // the loop's (StimulusTiming), or a cutoff's step where neither stimulus plays when earlier
    public double outcomeTime;
    public double rewardOfferedUl;
    public bool rewarded;
    public double penaltyTimeoutSeconds;
    public double penaltyFlashBrightness;   // 0: no flash
    public bool penalised;
    public int laserCondition;
    public string laserPointsMm;
    public double laserPowerMw;
    public double laserTriggerTime;     // NaN without optogenetics or on a trial that ended before its trigger
    public int trialConditionId;        // the 0-based index of its condition in the task's trialConditions
    public int trialCombinationId;      // its combination's row in Settings_Combinations
    public double visualInitialContrast;   // 0: no grating
    public double visualPhaseRad;
    public double auditoryInitialLevelDbfs;   // NaN: no tone (mute)
    public double auditoryInitialAzimuthDeg;   // the one speaker's; NaN on every speaker or with no tone
    public double auditoryInitialElevationDeg;
    public bool autoReward;
    public string outcomeReason;
    public double trackLengthCm;
    public double outcomePositionCm;
    public double auditoryOutcomeFrequencyHz;
    public double visualOutcomeSpatialFrequencyCpd;
    public double auditoryCutoffPositionCm;   // NaN when the tone never switched off
    public double visualCutoffPositionCm;     // NaN when the grating never switched off
}
