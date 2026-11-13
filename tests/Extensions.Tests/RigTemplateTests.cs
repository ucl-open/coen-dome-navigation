using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
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
}
