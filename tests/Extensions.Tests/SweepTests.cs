using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenCV.Net;
using OpenRigs.Audio;
using OpenRigs.Devices.Zapit;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Runtime.CompilerServices;
using UclOpenCoenDomeNavigationTask;
using YamlDotNet.Serialization;

// The speakers' filters are loaded and checked by OpenRigs.Audio (its own tests); these cover the pips laid out on them.
[TestClass]
public class PipSetTests
{
    static readonly double[] Symmetric = { 0.01, -0.05, 0.2, 1.1, 0.2, -0.05, 0.01 };
    string rig;

    [TestInitialize]
    public void Write()
    {
        rig = Path.Combine(Path.GetTempPath(), "dome-navigation-pips-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(rig, "calibration"));
        File.WriteAllText(Path.Combine(rig, "calibration", "sym.json"),
            "{\"calibratedAt\":\"2026-11-04T00:00:00Z\",\"calibratedBy\":\"test\",\"machineName\":\"THIS-RIG\",\"notes\":\"\",\"sampleRate\":48000,\"taps\":[" +
            string.Join(",", Symmetric.Select(t => t.ToString("R", System.Globalization.CultureInfo.InvariantCulture))) + "]}");
        var speakers = "";
        foreach (var s in new[] { Tuple.Create("left", -90, 0), Tuple.Create("right", 90, 1) })
        {
            speakers += "    " + s.Item1 + ":\n      position: {azimuth: " + s.Item2 + ", elevation: 15.0, radius: 1.0}\n      channel: " + s.Item3 + "\n      filter: calibration/sym.json\n";
        }
        File.WriteAllText(Path.Combine(rig, "rig.yml"), "speakerArray:\n  device:\n    sampleRate: 48000\n    channelCount: 4\n  speakers:\n" + speakers +
            "  syncChannel: 3\n  penaltySoundChannel: 1\n  backgroundNoise: ../shared/sounds/background_noise.wav\n  penaltySound: ../shared/sounds/penalty_tone.wav\n");
    }

    [TestCleanup]
    public void Remove()
    {
        Directory.Delete(rig, true);
    }

    SpeakerSet Speakers() { return SpeakerSet.Load(Path.Combine(rig, "rig.yml"), "THIS-RIG"); }

    // The committed pips, a Mat a row, as the workflow's MatrixReader reads all_waveforms.bin.
    static IList<Mat> Pips()
    {
        var bytes = File.ReadAllBytes(Path.Combine(Fixture.Repo(), "src", "sounds", "all_waveforms.bin"));
        var samples = new float[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, samples, 0, bytes.Length);
        return Enumerable.Range(0, samples.Length / 2400).Select(i => Mat.FromArray(samples.Skip(i * 2400).Take(2400).ToArray(), 1, 2400, Depth.F32, 1)).ToList();
    }

    static double MaxDifference(Mat a, Mat b)
    {
        using (var diff = new Mat(a.Size, Depth.F32, 1))
        {
            CV.AbsDiff(a, b, diff);
            double min, max; Point minAt, maxAt;
            CV.MinMaxLoc(diff, out min, out max, out minAt, out maxAt);
            return max;
        }
    }

    [TestMethod]
    public void AmplitudeOneIsTheLoudnessThePipsPlayedAtBefore()
    {
        // Before the trial-type split the pips were SpeakerBuffer's at auditoryStimulus.levelDbfs -58.1 (SpeakerSet.Buffer); each
        // file is now stored at -58.1 dBFS RMS, so amplitude 1, the file as stored, plays the same samples on every channel,
        // the sync copy included.
        var speakers = Speakers();
        var pips = Pips();
        var set = PipSet.Create(speakers, pips, new LoudestSound { Amplitude = 1, Where = "test" });
        for (int i = 0; i < pips.Count; i++)
        {
            Assert.AreEqual(-58.1, SoundLevel.RmsDbfs(pips[i]), 1e-4, "pip " + i);
            using (var before = speakers.Buffer(pips[i], null, -58.1, true, true))
            {
                Assert.IsTrue(MaxDifference(before, set.Buffer(i, 1)) < 1e-9, "pip " + i);
            }
        }
    }

    [TestMethod]
    public void APipPlaysAtItsTrialsAmplitudeWithTheSyncCopyKept()
    {
        var speakers = Speakers();
        var set = PipSet.Create(speakers, Pips(), new LoudestSound { Amplitude = 2, Where = "test" });
        var one = set.Buffer(40, 1);
        var half = set.Buffer(40, 0.5);
        using (var left = one.GetRow(0)) using (var leftHalf = half.GetRow(0)) using (var expected = new Mat(left.Size, Depth.F32, 1))
        {
            CV.ConvertScale(left, expected, 0.5, 0);
            Assert.IsTrue(MaxDifference(expected, leftHalf) < 1e-9);
        }
        using (var sync = one.GetRow(3)) using (var syncHalf = half.GetRow(3)) Assert.AreEqual(0, MaxDifference(sync, syncHalf));
        Assert.AreEqual(0, CV.CountNonZero(set.Buffer(40, 0)));
        // On one speaker: that speaker's row, the other speakers silent, the sync copy kept.
        var right = set.Buffer(40, 0.5, 1);
        using (var r = right.GetRow(1)) using (var h = half.GetRow(1)) Assert.AreEqual(0, MaxDifference(r, h));
        using (var l = right.GetRow(0)) Assert.AreEqual(0, CV.CountNonZero(l));
        using (var sync = one.GetRow(3)) using (var s = right.GetRow(3)) Assert.AreEqual(0, MaxDifference(sync, s));
        Assert.AreEqual(0, MaxDifference(one, set.Buffer(40, 1, Trial.AllSpeakersChannel)));
        StringAssert.Contains(Assert.ThrowsException<InvalidOperationException>(() => set.Buffer(40, 1, 2)).Message, "no rig.yml speaker is on");
        StringAssert.Contains(Assert.ThrowsException<InvalidOperationException>(() => set.Buffer(40, 3)).Message, "above the 2 the pips were checked for at Go");
    }

    [TestMethod]
    public void AnAmplitudeThatWouldClipStopsAtGoNamingTheCondition()
    {
        var message = Assert.ThrowsException<InvalidOperationException>(() => PipSet.Create(Speakers(), Pips(), new LoudestSound { Amplitude = 2000, Where = "trialConditions[1] (auditory)" })).Message;
        StringAssert.Contains(message, "trialConditions[1] (auditory)");
        StringAssert.Contains(message, "amplitude 2000");
    }
}

[TestClass]
public class SweepTests
{
    [TestMethod]
    public void TheSweepIsTheToneFiles()
    {
        var sweep = Fixture.Sweep();
        Assert.AreEqual(2000, sweep.StartHz);
        Assert.AreEqual(25000, sweep.EndHz);
        Assert.AreEqual(100, sweep.Steps);
        Assert.AreEqual(48000, sweep.SampleRate);
        Assert.AreEqual(2400, sweep.PipSamples);  // one row of all_waveforms.bin
        Assert.AreEqual(0, sweep.BinOf(2000));
        Assert.AreEqual(99, sweep.BinOf(25000));
        Assert.AreEqual(99, sweep.BinOf(40000));
    }

    [TestMethod]
    public void TheTrackRunsTwoToTwentyFourKilohertzAsTomsWorkflowDid()
    {
        // Tom Childs' 'cm to Hz' divided sweepEndHz / sweepStartHz as whole numbers (25000 / 2000 = 12): his mice learned 2 to 24 kHz
        // along the track, so the reward zones sit where they did. The tone files still span 2 to 25 kHz.
        var sweep = Fixture.Sweep();
        Assert.AreEqual(12, sweep.TrackRatio);
        Assert.AreEqual(24000, sweep.TrackEndHz);
        Assert.AreEqual(2000, sweep.FrequencyAt(-1), 1e-9);
        Assert.AreEqual(24000, sweep.FrequencyAt(2), 1e-6);
        Assert.AreEqual(2000 * Math.Sqrt(12), sweep.FrequencyAt(0.5), 1e-6);
        Assert.AreEqual(0.817, Math.Round(sweep.FractionOf(15248), 3));  // stage 3-5 reward floor: 98.1 cm of a 120 cm track
        Assert.AreEqual(0.951, Math.Round(sweep.FractionOf(21248), 3));  // and its ceiling: 114.1 cm
        Assert.AreEqual(97, sweep.BinOf(sweep.FrequencyAt(1)));           // the track's top tone is file row 97, as Tom's
    }

    [TestMethod]
    public void ASweepFileMissingAKeyIsRefusedNamingIt()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "{\"sweepStartHz\": 2000, \"sweepSteps\": 100, \"pipLevelDbfs\": -58.1, \"sampleRate\": 48000}");
            StringAssert.Contains(Assert.ThrowsException<InvalidOperationException>(() => global::Sweep.Load(path)).Message, "sweepEndHz");
            File.WriteAllText(path, "{\"sweepStartHz\": 2000, \"sweepEndHz\": 25000, \"sweepSteps\": 100, \"pipLevelDbfs\": -58.1}");
            StringAssert.Contains(Assert.ThrowsException<InvalidOperationException>(() => global::Sweep.Load(path)).Message, "sampleRate");
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    public void WaveformsAtAnotherRateThanTheAudioDeviceAreRefusedNamingBoth()
    {
        // rig.yml's speakerArray.device.sampleRate; the committed waveforms are 48 kHz.
        var path = Path.Combine(Fixture.Repo(), "src", "sounds", "sweep.json");
        Assert.AreEqual(48000, global::Sweep.Load(path, 48000).SampleRate);
        var message = Assert.ThrowsException<InvalidOperationException>(() => global::Sweep.Load(path, 44100)).Message;
        StringAssert.Contains(message, "sweep.json is 48000 Hz, but the audio device runs at 44100 Hz");
        StringAssert.Contains(message, "uv run generate-waveforms");
        var load = new LoadSweep { SweepFile = path };
        Assert.AreEqual(48000, load.Process(Observable.Return(48000)).Wait().SampleRate);
        Assert.ThrowsException<InvalidOperationException>(() => load.Process(Observable.Return(96000)).Wait());
    }

    [TestMethod]
    public void AMissingSweepFileIsRefusedNamingItsFullPath()
    {
        // GREEN-DOME-2 test-0946: Bonsai's working folder was .bonsai, so the relative path named no file. Read at Start (LoadSweep).
        var folder = Path.Combine(Path.GetTempPath(), "nav-sweep-" + Guid.NewGuid().ToString("N"));
        var old = Directory.GetCurrentDirectory();
        Directory.CreateDirectory(folder);
        try
        {
            Directory.SetCurrentDirectory(folder);
            var message = Assert.ThrowsException<FileNotFoundException>(() => new LoadSweep().Process(Observable.Return(48000)).Wait()).Message;
            StringAssert.Contains(message, Path.Combine(folder, "sounds", "sweep.json"));
            StringAssert.Contains(message, "working folder must be the workflow's (src)");
        }
        finally
        {
            Directory.SetCurrentDirectory(old);
            Directory.Delete(folder, true);
        }
    }
}
