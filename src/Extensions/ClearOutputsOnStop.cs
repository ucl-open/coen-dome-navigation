using Bonsai;
using Bonsai.Harp;
using Harp.Behavior;
using System;
using System.ComponentModel;
using System.IO;
using System.IO.Ports;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Threading;

// Tom Childs, 10 Nov 2026: a Stop mid-trial left the Zapit trigger line (DO3) high, so the masking LEDs on it stayed
// lit until Bonsai next started. Every way this workflow stops (Stop, an error, ExperimentControl's stop, which is an
// error) disposes the graph rather than completing it, so a Concat on completion never runs, and a message sent into
// BehaviorBoard-Main.Commands from a Finally is dropped: Rx detaches each observer before disposing its source (the
// editor wraps every node so), and the board may already have unsubscribed. This node sits on the Behavior board
// include's output instead, so disposing it disposes the board first (Bonsai.Harp writes Standby and closes the port);
// it then opens the port itself and writes the clear.

[Combinator]
[Description("Passes the Behavior board's events through. When the workflow stops, however it stops, it lets the board close its port, then opens the port and writes OutputClear for Outputs (DO3: the Zapit trigger line and the masking LEDs on it). Best effort: a port that cannot be opened is passed over.")]
[WorkflowElementCategory(ElementCategory.Sink)]
public class ClearOutputsOnStop
{
    const int OpenAttempts = 5;
    const int RetryMs = 100;

    public ClearOutputsOnStop()
    {
        Outputs = DigitalOutputs.DO3;
    }

    [Description("The Behavior board's serial port (rig.yml's, through the group's PortName).")]
    public string PortName { get; set; }

    [Description("The lines cleared when the workflow stops.")]
    public DigitalOutputs Outputs { get; set; }

    public IObservable<HarpMessage> Process(IObservable<HarpMessage> source)
    {
        return Observable.Create<HarpMessage>(observer =>
        {
            var portName = PortName;
            var outputs = Outputs;
            var subscription = source.SubscribeSafe(observer);
            return Disposable.Create(() =>
            {
                subscription.Dispose();
                Clear(portName, outputs);
            });
        });
    }

    /// <summary>Writes OutputClear for the lines on a port nobody holds; true if it was written.</summary>
    public static bool Clear(string portName, DigitalOutputs outputs)
    {
        if (string.IsNullOrEmpty(portName)) return false;
        var message = OutputClear.FromPayload(MessageType.Write, outputs).MessageBytes;
        for (var attempt = 1; attempt <= OpenAttempts; attempt++)
        {
            try
            {
                // Bonsai.Harp's SerialTransport settings.
                using (var port = new SerialPort(portName, 1000000, Parity.None, 8, StopBits.One))
                {
                    port.Handshake = Handshake.RequestToSend;
                    port.DtrEnable = true;
                    port.WriteTimeout = 500;
                    port.Open();
                    port.Write(message, 0, message.Length);
                    return true;
                }
            }
            catch (UnauthorizedAccessException)
            {
                // The board's port is released a moment after its close returns.
                Thread.Sleep(RetryMs);
            }
            catch (IOException)
            {
                // No such port (unplugged, or a test rig.yml's COM97): nothing to clear.
                return false;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
            catch (TimeoutException)
            {
                return false;
            }
        }
        return false;
    }
}
