using Bonsai;
using System;
using System.ComponentModel;
using System.IO;
using System.Reactive.Linq;

// The spout positions file to read at Start: this workflow's own (calibration\LickSpoutPositions\
// beside rig.yml, where SaveSpoutPositions writes) when the mouse has one, else production's
// (C:\RigConfigs\LickSpoutPositions, where auditory-vr-corridor reads and saves them), else the
// workflow's own path, which ReadPositions finds missing and skips as before. Production's files
// are only read, never written (rig.yml: "Production's files, read only"). A rig whose
// calibration\ folder was never made read no mouse's positions at all (Pip, 9 Nov 2026).
// C# 5: Bonsai compiles src\Extensions with it.
[Combinator]
[Description("The spout positions file to read: the workflow's own if it exists, else the same file name in FallbackFolder if that exists, else the workflow's own.")]
[WorkflowElementCategory(ElementCategory.Transform)]
public class SpoutPositionsToRead
{
    public SpoutPositionsToRead()
    {
        FallbackFolder = @"C:\RigConfigs\LickSpoutPositions";
    }

    [Description("Where production keeps each mouse's spout positions, read when the workflow's own file is missing.")]
    public string FallbackFolder { get; set; }

    public static string Choose(string own, string fallbackFolder)
    {
        if (string.IsNullOrEmpty(own) || File.Exists(own) || string.IsNullOrEmpty(fallbackFolder))
        {
            return own;
        }
        var fallback = Path.Combine(fallbackFolder, Path.GetFileName(own));
        return File.Exists(fallback) ? fallback : own;
    }

    public IObservable<string> Process(IObservable<string> source)
    {
        return source.Select(own => Choose(own, FallbackFolder));
    }
}
