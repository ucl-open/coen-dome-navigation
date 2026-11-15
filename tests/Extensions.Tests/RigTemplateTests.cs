using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenCV.Net;
using OpenRigs.Audio;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Reflection;
using OpenRigs.Devices.Rigs;

/// <summary>The speakers of the lab's rig template (open-rigs' rigs\rig-template.yml, in .venv), read as the workflow reads
/// rig.yml (OpenRigs.Devices.Rigs) and loaded by OpenRigs.Audio as the workflow loads them.</summary>
[TestClass]
public class RigTemplateTests
{
    static LabRig Deserialize(string text)
    {
        var process = typeof(DeserializeFromYaml).GetMethod("Process", BindingFlags.NonPublic | BindingFlags.Static).MakeGenericMethod(typeof(LabRig));
        var rigs = (IObservable<LabRig>)process.Invoke(null, new object[] { Observable.Return(text) });
        return rigs.Wait();
    }

    static string Template()
    {
        return File.ReadAllText(Path.Combine(Fixture.Repo(), ".venv", "Lib", "site-packages", "open_rigs", "rigs", "rig-template.yml"));
    }


    /// <summary>The template as this machine's rig.yml in a scratch folder, each speaker's filter a symmetric 3-tap
    /// SpeakerFilter measured here, as OpenRigs.Audio's LoadSpeakers wants.</summary>
    static string TemplateRig(string text)
    {
        var folder = Path.Combine(Path.GetTempPath(), "nav-rig-" + Guid.NewGuid().ToString("N"));
        foreach (var speaker in Deserialize(Template()).SpeakerArray.Speakers.Values)
        {
            var filter = Path.Combine(folder, speaker.Filter);
            Directory.CreateDirectory(Path.GetDirectoryName(filter));
            File.WriteAllText(filter, "{\"calibratedAt\": \"2026-11-14T00:00:00Z\", \"calibratedBy\": \"test\", \"machineName\": \"" + Environment.MachineName + "\", \"sampleRate\": 48000, \"taps\": [0.25, 0.5, 0.25]}");
        }
        var path = Path.Combine(folder, "rig.yml");
        File.WriteAllText(path, text);
        return path;
    }

    [TestMethod]
    public void EverySpeakerNeedsAFilter()
    {
        // Pip, 13 Nov 2026: no speaker plays unequalised; the workflow stops at Start naming it. From 14 Nov 2026
        // OpenRigs.Audio's LoadSpeakers checks it (and each filter) as it loads the speakers.
        var speakers = SpeakerSet.Load(TemplateRig(Template()));
        Assert.AreEqual(Deserialize(Template()).SpeakerArray.Speakers.Count, speakers.Speakers.Count);
        var text = Template().Replace("filter: calibration/speaker-right.json", "filter: null");
        Assert.AreNotEqual(Template(), text);
        var error = Assert.ThrowsException<InvalidOperationException>(() => SpeakerSet.Load(TemplateRig(text)));
        StringAssert.Contains(error.Message, "speaker 'right' has no filter");
    }

    [TestMethod]
    public void EveryBufferHasTheTemplatesChannelCountAndPlaysOnlyOnItsSpeakers()
    {
        // Audit, 14 Nov 2026: the mixer's buffers are rig.yml's channelCount rows (32, as the Tile 32 they replace);
        // a stimulus on every speaker, the penalty sound on penaltySoundChannel alone.
        var rig = Deserialize(Template());
        var speakers = SpeakerSet.Load(TemplateRig(Template()));
        Assert.AreEqual(rig.SpeakerArray.Device.ChannelCount, speakers.ChannelCount);
        Assert.AreEqual(32, speakers.ChannelCount);
        var sound = new Mat(1, 2400, Depth.F32, 1);
        sound.Set(Scalar.All(0.01));
        var pips = new SpeakerBuffer().Process(Observable.Return(Tuple.Create(speakers, (IList<Mat>)new List<Mat> { sound, sound }))).Wait();
        Assert.AreEqual(2, pips.Count);
        Assert.AreEqual(32, pips[0].Rows);
        var used = rig.SpeakerArray.Speakers.Values.Select(s => s.Channel).ToList();
        for (int channel = 0; channel < 32; channel++)
        {
            using (var row = pips[0].GetRow(channel))
            {
                Assert.AreEqual(used.Contains(channel), CV.Norm(row) != 0, "channel " + channel);
            }
        }
        var error = new PenaltySoundBuffer().Process(Observable.Return(speakers)).Wait();
        Assert.AreEqual(32, error.Rows);
        for (int channel = 0; channel < 32; channel++)
        {
            using (var row = error.GetRow(channel))
            {
                Assert.AreEqual(channel == rig.SpeakerArray.PenaltySoundChannel, CV.Norm(row) != 0, "channel " + channel);
            }
        }
    }
}
