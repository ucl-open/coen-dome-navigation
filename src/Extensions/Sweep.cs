using Bonsai;
using Bonsai.Harp;
using Newtonsoft.Json.Linq;
using OpenCV.Net;
using OpenRigs.Audio;
using OpenRigs.Trials;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reactive.Linq;

/// <summary>The sweep the committed tone files were made for, and the rate every waveform was made at
/// (src\sounds\sweep.json, ucl_open_coen_dome_navigation.waveforms).</summary>
public class Sweep
{
    public double StartHz;
    public double EndHz;
    public int Steps;
    public int SampleRate;

    /// <summary>One pip's samples, a row of all_waveforms.bin: a step at SampleRate.</summary>
    public int PipSamples { get { return (int)Math.Round(SampleRate * TrialRules.StepSeconds); } }

    public static Sweep Load(string path)
    {
        path = Path.GetFullPath(path);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("There is no sweep file at " + path + ": Bonsai's working folder must be the workflow's (src), or run `uv run generate-waveforms`.", path);
        }
        var json = JObject.Parse(File.ReadAllText(path));
        var sweep = new Sweep
        {
            // A missing key reads as NaN or 0, so the check below names the file rather than a bare null.
            StartHz = json.Value<double?>("sweepStartHz") ?? double.NaN,
            EndHz = json.Value<double?>("sweepEndHz") ?? double.NaN,
            Steps = json.Value<int?>("sweepSteps") ?? 0,
            SampleRate = json.Value<int?>("sampleRate") ?? 0
        };
        if (!(sweep.StartHz > 0 && sweep.EndHz > sweep.StartHz && sweep.Steps > 1 && sweep.SampleRate > 0))
        {
            throw new InvalidOperationException(path + " holds no usable sweep: it needs 0 < sweepStartHz < sweepEndHz, sweepSteps above 1 and sampleRate above 0. Run `uv run generate-waveforms`.");
        }
        return sweep;
    }

    /// <summary>Loads the sweep and throws unless its waveforms were made at the audio device's rate: nothing converts them,
    /// so they would play at the wrong pitch and speed (as OpenRigs.Audio's CheckWaveRate for a WAV).</summary>
    public static Sweep Load(string path, int deviceRate)
    {
        var sweep = Load(path);
        if (sweep.SampleRate != deviceRate)
        {
            throw new InvalidOperationException(Path.GetFullPath(path) + " is " + sweep.SampleRate + " Hz, but the audio device runs at " + deviceRate + " Hz: make the waveforms again at " + deviceRate + " Hz (waveforms.py SAMPLE_RATE, `uv run generate-waveforms`).");
        }
        return sweep;
    }

    /// <summary>The track's sweep ratio, sweepEndHz / sweepStartHz in whole numbers (25000 / 2000 = 12), as Tom Childs' workflow
    /// divided them: the tone runs 2 to 24 kHz along the track, which his mice learned. The track's progress
    /// use it; the tone files span the full sweep, so BinOf does not.</summary>
    public double TrackRatio { get { return Math.Floor(EndHz / StartHz); } }

    /// <summary>The highest tone the track plays: StartHz * TrackRatio (24 kHz).</summary>
    public double TrackEndHz { get { return StartHz * TrackRatio; } }

    /// <summary>The frequency a fraction (0 to 1, clamped) of the way along the track, on a log scale.</summary>
    public double FrequencyAt(double fraction)
    {
        return StartHz * Math.Pow(TrackRatio, Math.Max(0, Math.Min(1, fraction)));
    }

    /// <summary>How far along the track a frequency is, on a log scale.</summary>
    public double FractionOf(double hz)
    {
        return hz <= 0 ? 0 : Math.Log(hz / StartHz) / Math.Log(TrackRatio);
    }

    /// <summary>The tone file's row (0 to Steps - 1) nearest a frequency, over the files' full sweep (StartHz to EndHz).</summary>
    public int BinOf(double hz)
    {
        var fraction = hz <= 0 ? 0 : Math.Log(hz / StartHz) / Math.Log(EndHz / StartHz);
        return (int)Math.Max(0, Math.Min(Steps - 1, Math.Round(fraction * (Steps - 1))));
    }
}

/// <summary>A step's stimulus: its WorkflowData_StimulusState row and what the mixer and grating play.</summary>
public class StimulusFrame
{
    public StimulusStateRow Row;
    public int Bin;              // the tone file's row
    public double ToneGain;      // the trial's auditoryAmplitude, or 0 when switched off (StimulusStateRow.auditoryOn)
    public double GratingOpacity; // 1, or 0 when switched off (StimulusStateRow.visualOn) or at contrast 0
    public double GratingContrast; // the trial's visualContrast
}

/// <summary>The pips as the mixer plays them: each equalised for every rig.yml speaker, with its sync copy, at amplitude 1
/// (the files as stored, -58.1 dBFS RMS), checked at Go for the loudest amplitude the task's conditions can draw.</summary>
public class PipSet
{
    readonly IList<Mat> buffers;
    readonly int[] channels;

    /// <summary>The largest amplitude the set was checked for at Go.</summary>
    public double Largest { get; private set; }

    PipSet(IList<Mat> buffers, int[] channels, double largest)
    {
        this.buffers = buffers;
        this.channels = channels;
        Largest = largest;
    }

    /// <summary>The pips laid out on the speakers at amplitude 1; stops, naming the pip, channel and condition, if any would peak
    /// at -1 dBFS or more after equalisation at the loudest amplitude the conditions can draw.</summary>
    public static PipSet Create(SpeakerSet speakers, IList<Mat> pips, LoudestSound loudest)
    {
        var buffers = pips.Select(pip => speakers.Layout(pip, null, 1, true, true)).ToList();
        var set = new PipSet(buffers, speakers.SpeakerChannels, loudest.Amplitude);
        for (int i = 0; i < buffers.Count && loudest.Amplitude > 0; i++)
        {
            using (var played = set.Scale(buffers[i], loudest.Amplitude))
            {
                SoundLevel.CheckPeak(played, "Pip " + i + " at amplitude " + loudest.Amplitude.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + " (auditoryStimulus.amplitude, " + loudest.Where + ")");
            }
        }
        return set;
    }

    /// <summary>The number of pips.</summary>
    public int Count { get { return buffers.Count; } }

    /// <summary>A pip's buffer at an amplitude: its speakers' rows scaled, the sync copy kept; silent at 0.</summary>
    public Mat Buffer(int bin, double amplitude)
    {
        var buffer = buffers[bin];
        if (amplitude == 0)
        {
            var silent = new Mat(buffer.Size, buffer.Depth, buffer.Channels);
            silent.SetZero();
            return silent;
        }
        if (!(amplitude <= Largest)) throw new InvalidOperationException("A pip asks for amplitude " + amplitude + ", above the " + Largest + " the pips were checked for at Go (LayOutPips).");
        return amplitude == 1 ? buffer : Scale(buffer, amplitude);
    }

    Mat Scale(Mat buffer, double amplitude)
    {
        var scaled = buffer.Clone();
        foreach (var channel in channels)
        {
            using (var row = scaled.GetRow(channel)) CV.ConvertScale(row, row, amplitude, 0);
        }
        return scaled;
    }
}

/// <summary>A WorkflowData_StimulusState row, one per pip while a trial runs.</summary>
public class StimulusStateRow
{
    public int trialNumber;
    public double positionCm;
    public double auditoryFrequencyHz;
    public bool auditoryOn;          // false once a stimulus-off trial has switched the tone off
    public double visualSpatialFrequencyCpd;
    public bool visualOn;            // false once a stimulus-off trial has switched the grating off
}

[Combinator]
[Description("Reads sweep.json at Start: the sweep the tone files were made for and the rate every waveform was made at. Input: the audio device's rate (rig.yml's speakerArray.device.sampleRate). Stops the workflow naming the file when it is missing, and naming both rates when they differ.")]
[WorkflowElementCategory(ElementCategory.Transform)]
public class LoadSweep
{
    public LoadSweep()
    {
        SweepFile = @"sounds\sweep.json";
    }

    [Description("sweep.json, relative to the workflow's folder (src).")]
    public string SweepFile { get; set; }

    public IObservable<Sweep> Process(IObservable<int> source)
    {
        return source.Select(rate => Sweep.Load(SweepFile, rate));
    }
}

[Combinator]
[Description("A pip's WorkflowData_StimulusState row (an Update event of TrialEvents), stamped with its Harp time.")]
[WorkflowElementCategory(ElementCategory.Transform)]
public class CreateStimulusStateRow
{
    public IObservable<Timestamped<StimulusStateRow>> Process(IObservable<TrialEvent<Trial, StimulusFrame, TrialOutcome>> source)
    {
        return source.Where(e => e.Kind == TrialEventKind.Update).Select(e => Timestamped.Create(e.Update.Row, e.Seconds));
    }
}

[Combinator]
[Description("The pips laid out at Go (PipSet): each tone file row equalised for every rig.yml speaker with its sync copy, at amplitude 1 (the files as stored, -58.1 dBFS RMS). Input: the loudest tone the conditions can draw (TrialRules.Loudest), the speakers and the tone rows. Stops the workflow, naming the pip, channel and condition, if any would peak at -1 dBFS or more after equalisation at that amplitude.")]
[WorkflowElementCategory(ElementCategory.Transform)]
public class LayOutPips
{
    public IObservable<PipSet> Process(IObservable<Tuple<LoudestSound, Tuple<SpeakerSet, IList<Mat>>>> source)
    {
        return source.Select(x => PipSet.Create(x.Item2.Item1, x.Item2.Item2, x.Item1));
    }
}

[Combinator]
[Description("A pip's buffer for the mixer: the laid-out pip for the pip's bin at its ToneGain, the trial's auditoryAmplitude (input: the pip's Update event and the PipSet); silent when ToneGain is 0.")]
[WorkflowElementCategory(ElementCategory.Transform)]
public class PipBuffer
{
    public IObservable<Mat> Process(IObservable<Tuple<TrialEvent<Trial, StimulusFrame, TrialOutcome>, PipSet>> source)
    {
        return source.Select(x => x.Item2.Buffer(x.Item1.Update.Bin, x.Item1.Update.ToneGain));
    }
}
