/// <summary>A WorkflowData_Trials row; fields in schemas\trial-columns.yml's order, after harpTime (the row's timestamp, its
/// outcome). The fields up to laserTriggerTime are the lab's shared block, its core with the optogenetics and quietWait blocks (lib-open-rigs
/// open_rigs.rigs.trials), in coen-dome-decision-making's order and words too;
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
    public double rewardOfferedUl;
    public bool rewarded;
    public bool penaltySoundOn;
    public double penaltyTimeoutSeconds;
    public bool penalised;
    public int laserCondition;
    public double laserPowerMw;
    public double laserTriggerTime;     // NaN without optogenetics or on a trial that ended before its trigger
    public int trialCondition;          // the 0-based index of its condition in the task's trialConditions
    public int trialCombinationId;      // its combination's row in Settings_Combinations
    public string outcomeReason;
    public double trackLengthCm;
    public double wheelGain;
    public double outcomePositionCm;
    public double outcomeAuditoryFrequencyHz;
    public double outcomeVisualSpatialFrequencyCpd;
    public double auditoryOffPositionCm;  // NaN when the tone never switched off
    public double visualOffPositionCm;    // NaN when the grating never switched off
    public bool autoRewarded;
    public double visualContrast;       // 0: no grating
    public double auditoryAmplitude;    // 0: no tone
    public double auditoryAzimuthDeg;   // the one speaker's; NaN on every speaker or with no tone
    public double auditoryElevationDeg;
}
