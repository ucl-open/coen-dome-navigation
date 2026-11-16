using Bonsai;
using Bonsai.Harp;
using Newtonsoft.Json.Linq;
using OpenCV.Net;
using OpenRigs.Devices;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Reactive.Linq;

/// <summary>The sweep the committed tone files were made for, and the rate every waveform was made at
/// (src\waveforms\sweep.json, ucl_open_coen_dome_navigation.waveforms).</summary>
public class Sweep
{
    public double StartHz;
    public double EndHz;
    public int Steps;
    public int SampleRate;

    /// <summary>One pip's samples, a row of all_waveforms.bin: a beep at SampleRate.</summary>
    public int PipSamples { get { return (int)Math.Round(SampleRate * TrialRules.BeepSeconds); } }

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
    /// divided them: the tone runs 2 to 24 kHz along the track, which his mice learned (Pip, 16 Nov 2026). Position, drift and
    /// truncation use it; the tone files span the full sweep, so BinOf does not.</summary>
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

/// <summary>A beep's stimulus: its BonsaiData_StimulusState row and what the mixer and grating play.</summary>
public class StimulusFrame
{
    public StimulusStateRow Row;
    public int Bin;              // the tone file's row
    public double ToneGain;      // 1, or 0 when truncated or not auditory
    public double GratingOpacity; // 1, or 0 when truncated or not visual
}

/// <summary>A BonsaiData_StimulusState row, one per pip while a trial runs.</summary>
public class StimulusStateRow
{
    public int trialNumber;
    public double positionCm;
    public double toneFrequencyHz;
    public bool stimulusActive;
    public double visualSpatialFrequencyCpd;
}

[Combinator]
[Description("Reads sweep.json at Start: the sweep the tone files were made for and the rate every waveform was made at. Input: the audio device's rate (rig.yml's speakerArray.device.sampleRate). Stops the workflow naming the file when it is missing, and naming both rates when they differ.")]
[WorkflowElementCategory(ElementCategory.Transform)]
public class LoadSweep
{
    public LoadSweep()
    {
        SweepFile = @"waveforms\sweep.json";
    }

    [Description("sweep.json, relative to the workflow's folder (src).")]
    public string SweepFile { get; set; }

    public IObservable<Sweep> Process(IObservable<int> source)
    {
        return source.Select(rate => Sweep.Load(SweepFile, rate));
    }
}

[Combinator]
[Description("A pip's BonsaiData_StimulusState row (an Update event of TrialEvents), stamped with its Harp time.")]
[WorkflowElementCategory(ElementCategory.Transform)]
public class CreateStimulusRow
{
    public IObservable<Timestamped<StimulusStateRow>> Process(IObservable<TrialEvent<Trial, StimulusFrame, TrialOutcome>> source)
    {
        return source.Where(e => e.Kind == TrialEventKind.Update).Select(e => Timestamped.Create(e.Update.Row, e.Seconds));
    }
}

[Combinator]
[Description("A pip's buffer for the mixer: the tone file's row for the pip's bin (input: the pip's Update event and the tone rows), silent when the pip's ToneGain is 0.")]
[WorkflowElementCategory(ElementCategory.Transform)]
public class PipBuffer
{
    public IObservable<Mat> Process(IObservable<Tuple<TrialEvent<Trial, StimulusFrame, TrialOutcome>, IList<Mat>>> source)
    {
        return source.Select(x =>
        {
            var tone = x.Item2[x.Item1.Update.Bin];
            if (x.Item1.Update.ToneGain > 0) return tone;
            var silent = new Mat(tone.Size, tone.Depth, tone.Channels);
            silent.SetZero();
            return silent;
        });
    }
}
