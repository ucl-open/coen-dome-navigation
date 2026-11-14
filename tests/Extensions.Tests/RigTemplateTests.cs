using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenCV.Net;
using OpenRigs.Audio;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Reflection;
using UclOpenCoenDomeNavigation;

/// <summary>config\rig-template.yml read as the workflow reads rig.yml: the generated DeserializeFromYaml, whose objects
/// refuse a key the C# lacks, so a template the workflow would stop on at Start fails here.</summary>
[TestClass]
public class RigTemplateTests
{
    static DomeRig Deserialize(string text)
    {
        var process = typeof(DeserializeFromYaml).GetMethod("Process", BindingFlags.NonPublic | BindingFlags.Static).MakeGenericMethod(typeof(DomeRig));
        var rigs = (IObservable<DomeRig>)process.Invoke(null, new object[] { Observable.Return(text) });
        return rigs.Wait();
    }

    static string Template()
    {
        return File.ReadAllText(Path.Combine(Fixture.Repo(), "config", "rig-template.yml"));
    }

    [TestMethod]
    public void TheTemplateDeserializes()
    {
        var rig = Deserialize(Template());
        Assert.IsNotNull(rig.SpeakerArray);
        Assert.IsNotNull(rig.Zapit);
    }

    [TestMethod]
    public void AKeyTheCSharpLacksIsRefused()
    {
        Assert.ThrowsException<YamlDotNet.Core.YamlException>(() => Deserialize(Template() + "\nrewardValves: { left: DO1, right: DO2 }\n"));
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
        // a stimulus on every speaker, the error sound on errorSoundChannel alone.
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
        var error = new ErrorSoundBuffer().Process(Observable.Return(speakers)).Wait();
        Assert.AreEqual(32, error.Rows);
        for (int channel = 0; channel < 32; channel++)
        {
            using (var row = error.GetRow(channel))
            {
                Assert.AreEqual(channel == rig.SpeakerArray.ErrorSoundChannel, CV.Norm(row) != 0, "channel " + channel);
            }
        }
    }
}
