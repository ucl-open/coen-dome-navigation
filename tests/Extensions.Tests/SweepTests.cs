using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenRigs.Devices.Zapit;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Runtime.CompilerServices;
using UclOpenCoenDomeNavigationTask;
using YamlDotNet.Serialization;

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
        Assert.AreEqual(2000, sweep.FrequencyAt(-1), 1e-9);
        Assert.AreEqual(25000, sweep.FrequencyAt(2), 1e-6);
        Assert.AreEqual(0, sweep.BinOf(2000));
        Assert.AreEqual(99, sweep.BinOf(25000));
        Assert.AreEqual(99, sweep.BinOf(40000));
    }

    [TestMethod]
    public void ASweepFileMissingAKeyIsRefusedNamingIt()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "{\"sweepStartHz\": 2000, \"sweepSteps\": 100, \"pipPeak\": 0.0025, \"sampleRate\": 48000}");
            StringAssert.Contains(Assert.ThrowsException<InvalidOperationException>(() => global::Sweep.Load(path)).Message, "sweepEndHz");
            File.WriteAllText(path, "{\"sweepStartHz\": 2000, \"sweepEndHz\": 25000, \"sweepSteps\": 100, \"pipPeak\": 0.0025}");
            StringAssert.Contains(Assert.ThrowsException<InvalidOperationException>(() => global::Sweep.Load(path)).Message, "sampleRate");
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    public void WaveformsAtAnotherRateThanTheAudioDeviceAreRefusedNamingBoth()
    {
        // rig.yml's speakerArray.device.sampleRate; the committed waveforms are 48 kHz.
        var path = Path.Combine(Fixture.Repo(), "src", "waveforms", "sweep.json");
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
            StringAssert.Contains(message, Path.Combine(folder, "waveforms", "sweep.json"));
            StringAssert.Contains(message, "working folder must be the workflow's (src)");
        }
        finally
        {
            Directory.SetCurrentDirectory(old);
            Directory.Delete(folder, true);
        }
    }
}
