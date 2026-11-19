using System.Collections.Generic;
using System.Linq;
using OpenRigs.Core;
using OpenRigs.Trials;

// The generated task parameters as the lab's conditioned task (OpenRigs.Trials TrialConditions builds its conditions).
// Explicit members, so neither SerializeToJson nor the YAML writer sees them.
namespace UclOpenCoenDomeNavigationTask
{
    partial class UclOpenCoenDomeNavigationTaskParameters : IConditionedTask
    {
        IList<ITrialCondition> IConditionedTask.Conditions { get { return TrialConditions == null ? null : TrialConditions.Cast<ITrialCondition>().ToList(); } }
        ICurriculum IConditionedTask.Stage { get { return Curriculum; } }
        bool IConditionedTask.OptogeneticsEnabled { get { return Optogenetics != null && Optogenetics.Enabled; } }
    }
}
