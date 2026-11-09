using Bonsai;
using System;
using System.ComponentModel;
using System.Reactive.Linq;
using UclOpenCoenDomeNavigationTask;

// Startup check for optogenetics experiments. Bonsai sends Zapit condition numbers that it reads from the experiment's stimulus
// config (optogenetics.zapitConditionsPath), and Zapit presents whatever it has at that position in the config it has loaded.
// If the two files differ, the wrong sites would be stimulated without any error, so the experiment stops at startup instead.
[Combinator]
[Description("Compares Zapit's GetNumConditions reply with the number of conditions in the task file's Zapit conditions file, and stops the experiment if they differ or Zapit answered with an error. Passes the reply through when they match. Input is the GetNumConditions reply and the task parameters.")]
[WorkflowElementCategory(ElementCategory.Transform)]
public class CheckZapitConditionCount
{
    public IObservable<ZapitReply> Process(IObservable<Tuple<ZapitReply, UclOpenCoenDomeNavigationTaskParameters>> source)
    {
        return source.Select(input =>
        {
            Check(input.Item1, input.Item2);
            return input.Item1;
        });
    }

    private static void Check(ZapitReply reply, UclOpenCoenDomeNavigationTaskParameters parameters)
    {
        if (reply == null)
        {
            throw new ArgumentNullException("reply", "CheckZapitConditionCount needs Zapit's GetNumConditions reply.");
        }
        if (reply.Command != ZapitCommandType.GetNumConditions)
        {
            // A wiring mistake: only the answer to GetNumConditions carries the condition count.
            throw new InvalidOperationException(string.Format(
                "CheckZapitConditionCount expects Zapit's GetNumConditions reply but received {0}.", reply));
        }
        if (parameters == null || parameters.Optogenetics == null)
        {
            throw new ArgumentNullException("parameters", "CheckZapitConditionCount needs the task parameters.");
        }

        var path = parameters.Optogenetics.ZapitConditionsPath;
        var count = ZapitConditions.ReadConditionNumbers(path).Count;
        ZapitConditions.CheckRampDowns(path);
        if (reply.IsError)
        {
            throw new InvalidOperationException(string.Format(
                "Zapit answered GetNumConditions with an error ({0}), so the number of conditions it has loaded is unknown. " +
                "The task file's conditions file {1} has {2} - load the same file in Zapit and restart the workflow.",
                reply, path, count));
        }
        if (reply.Value != count)
        {
            throw new InvalidOperationException(string.Format(
                "Zapit has {0} conditions loaded but the task file's conditions file {1} has {2} - load the same file in Zapit " +
                "and restart the workflow.",
                reply.Value, path, count));
        }
    }
}
