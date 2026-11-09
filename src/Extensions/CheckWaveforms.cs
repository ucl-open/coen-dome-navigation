using Bonsai;
using System;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Reactive.Linq;
using UclOpenCoenAvCorridorTask;

// The tone files in src\waveforms are made once, by ucl_open_coen_av_corridor.waveforms, for one
// sweep and loudness, and committed; sweep.txt records them as "start end bins amplitude". Before,
// gen_session.py wrote them again before every experiment, so they always matched its task.
// Now a task file edited on the form could ask for another sweep, and the workflow would play the
// committed tones under the task's numbers with no error: so the experiment stops at Go instead.
[Combinator]
[Description("Passes the task parameters on when src\\waveforms (its sweep.txt) was made for their startFreq, endFreq, nFreqBins and amplitude; otherwise stops the workflow saying what differs.")]
[WorkflowElementCategory(ElementCategory.Transform)]
public class CheckWaveforms
{
    public CheckWaveforms()
    {
        FileName = @"waveforms\sweep.txt";
    }

    [Description("sweep.txt, relative to the workflow's folder (src).")]
    public string FileName { get; set; }

    public IObservable<UclOpenCoenAvCorridorTaskParameters> Process(IObservable<UclOpenCoenAvCorridorTaskParameters> source)
    {
        return source.Select(p =>
        {
            string[] made = File.ReadAllText(FileName).Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            if (made.Length != 4)
            {
                throw new InvalidOperationException(string.Format("{0} should hold \"start end bins amplitude\"; it holds \"{1}\". Run `uv run generate-waveforms`.", FileName, string.Join(" ", made)));
            }
            int start = int.Parse(made[0], CultureInfo.InvariantCulture);
            int end = int.Parse(made[1], CultureInfo.InvariantCulture);
            int bins = int.Parse(made[2], CultureInfo.InvariantCulture);
            double amplitude = double.Parse(made[3], CultureInfo.InvariantCulture);
            if (start != p.StartFreq || end != p.EndFreq || bins != p.NFreqBins || Math.Abs(amplitude - p.Amplitude) > 1e-9)
            {
                throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture,
                    "The task asks for a {0}-{1} Hz sweep in {2} tones at amplitude {3}, but src\\waveforms was made for {4}-{5} Hz in {6} at {7}. Use those values, or run `uv run generate-waveforms` for the new ones and commit.",
                    p.StartFreq, p.EndFreq, p.NFreqBins, p.Amplitude, start, end, bins, amplitude));
            }
            return p;
        });
    }
}
