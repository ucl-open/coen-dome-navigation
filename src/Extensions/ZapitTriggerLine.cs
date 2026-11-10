using Bonsai;
using Harp.Behavior;
using System;
using System.ComponentModel;
using System.Reactive.Linq;

// The Zapit trigger line (rig.yml zapit.triggerLine) is held high from beep 0 until after the outcome
// rather than pulsed for rig.yml's fixed width, so the masking LEDs wired to it last as long as the laser
// (Tom Childs and Pip, 10 Nov 2026). Zapit's DAQ starts on the rising edge, which is unchanged. The board
// is told not to pulse that line (WithoutPulseLine); the line is cleared through ZapitTriggerClear.

[Combinator]
[Description("The Behavior board's DO line for a line number, 1 to 3 (rig.yml zapit.triggerLine), as the board's DigitalOutputs.")]
[WorkflowElementCategory(ElementCategory.Transform)]
public class DigitalOutputLine
{
    public IObservable<DigitalOutputs> Process(IObservable<int> source)
    {
        return source.Select(FromLine);
    }

    /// <summary>DO1, DO2 or DO3 for 1, 2 or 3; any other line is refused (the pulse controller drives only these).</summary>
    public static DigitalOutputs FromLine(int line)
    {
        if (line < 1 || line > 3)
        {
            throw new InvalidOperationException("zapit.triggerLine must be a Behavior board DO line, 1 to 3; got " + line + ".");
        }
        return (DigitalOutputs)Enum.Parse(typeof(DigitalOutputs), "DO" + line);
    }
}

[Combinator]
[Description("rig.yml's pulse-enabled lines without the Zapit trigger line, which this workflow holds high and clears itself. Input: the pulse-enabled lines and zapit.triggerLine.")]
[WorkflowElementCategory(ElementCategory.Transform)]
public class WithoutPulseLine
{
    public IObservable<DigitalOutputs> Process(IObservable<Tuple<DigitalOutputs, int>> source)
    {
        return source.Select(value => value.Item1 & ~DigitalOutputLine.FromLine(value.Item2));
    }
}
