using Bonsai;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.ComponentModel;
using System.IO;
using System.Reactive.Linq;

// The Zapit stimulus config as JSON, for OpenRigs.Logging's LogDataSchema (which writes Settings\<Name>_<time>.json and
// parses its input as JSON, so the YAML text cannot go in as it is). The trial rows record only condition numbers, so
// this copy is what says which sites, laser power and ramp-down each condition meant in an experiment.
[Combinator]
[Description("Reads the Zapit stimulus config named by the input path and returns it as JSON: its full path and its text, unchanged. For LogDataSchema, which only writes JSON.")]
[WorkflowElementCategory(ElementCategory.Transform)]
public class ZapitStimConfigSnapshot
{
    public IObservable<string> Process(IObservable<string> source)
    {
        return source.Select(path =>
        {
            var fullPath = Path.GetFullPath(path);
            var snapshot = new JObject();
            snapshot["zapitConfigPath"] = fullPath;
            snapshot["contents"] = File.ReadAllText(fullPath);
            return snapshot.ToString(Formatting.Indented);
        });
    }
}
