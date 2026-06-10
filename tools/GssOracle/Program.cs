using System;
using System.IO;
using System.Threading;
using GS.SkyWatcher;

namespace GssOracle
{
    /// <summary>
    /// Drives GSServer's GS.SkyWatcher classes (the de-facto reference client for the
    /// SkyWatcher motor protocol) against a scripted serial port and dumps the exact
    /// wire transcript per scenario. The JSON output is committed to TianWen.Lib.Tests
    /// as the protocol oracle - regenerate with: dotnet run --project tools/GssOracle.
    /// </summary>
    internal static class Program
    {
        // Sidereal rate in deg/s (15.041067"/s) - the unit AxisSlew/AxisPulse take.
        private const double SiderealDegSec = 15.041067 / 3600.0;

        private static ScriptedSerialPort _port;

        private static int Main(string[] args)
        {
            var outPath = args.Length > 0 ? args[0] : "gss-oracle-transcripts.json";
            _port = new ScriptedSerialPort();

            SkyQueue.Start(_port, new[] { 0, 0 }, new[] { 0.0, 0.0 });
            Run(new SkyLoadDefaultMountSettings(SkyQueue.NewId));

            foreach (var south in new[] { false, true })
            {
                var h = south ? "south" : "north";
                Run(new SkySetSouthernHemisphere(SkyQueue.NewId, south));

                // Tracking: GSS feeds EqS the negated rate (SkyServer.SetTracking).
                Scenario($"{h}-track-sidereal",
                    () => Run(new SkyAxisSlew(SkyQueue.NewId, AxisId.Axis1, south ? -SiderealDegSec : SiderealDegSec)));

                // RA pulses on top of the running tracking (f = 0.5 guide rate).
                // Positive guide rate = faster than tracking (west), negative = slower (east).
                Pulse($"{h}-pulse-ra-west-f05", AxisId.Axis1, 0.5 * SiderealDegSec, isRa: true);
                Pulse($"{h}-pulse-ra-east-f05", AxisId.Axis1, -0.5 * SiderealDegSec, isRa: true);
                // f = 1.0 east: combined rate ~0 - the SiderealRate/1000 edge case.
                Pulse($"{h}-pulse-ra-east-f10", AxisId.Axis1, -(south ? -SiderealDegSec : SiderealDegSec), isRa: true);

                Scenario($"{h}-stop-ra", () => Run(new SkyAxisStop(SkyQueue.NewId, AxisId.Axis1)));

                // Dec pulses, rate mode (DecPulseGoTo off).
                Run(new SkySetDecPulseToGoTo(SkyQueue.NewId, false));
                Pulse($"{h}-pulse-dec-north-f05", AxisId.Axis2, 0.5 * SiderealDegSec, isRa: false);
                Pulse($"{h}-pulse-dec-south-f05", AxisId.Axis2, -0.5 * SiderealDegSec, isRa: false);

                // Dec pulse as micro-GOTO (DecPulseGoTo on).
                Run(new SkySetDecPulseToGoTo(SkyQueue.NewId, true));
                Pulse($"{h}-pulse-dec-goto-north-f05", AxisId.Axis2, 0.5 * SiderealDegSec, isRa: false);
                Run(new SkySetDecPulseToGoTo(SkyQueue.NewId, false));

                // GOTO + constant slews.
                Scenario($"{h}-goto-axis1-45deg",
                    () => Run(new SkyAxisGoToTarget(SkyQueue.NewId, AxisId.Axis1, 45.0)));
                Scenario($"{h}-movesteps-axis1",
                    () => Run(new SkyAxisMoveSteps(SkyQueue.NewId, AxisId.Axis1, 50000)));
                Scenario($"{h}-slew-fast-3degsec",
                    () => Run(new SkyAxisSlew(SkyQueue.NewId, AxisId.Axis1, 3.0)));
                Scenario($"{h}-stop-final", () =>
                {
                    Run(new SkyAxisStop(SkyQueue.NewId, AxisId.Axis1));
                    Run(new SkyAxisStop(SkyQueue.NewId, AxisId.Axis2));
                });
            }

            SkyQueue.Stop();

            File.WriteAllText(outPath, _port.ToJson());
            Console.WriteLine($"Wrote {_port.Transcript.Count} transcript entries to {outPath}");
            return 0;
        }

        private static void Scenario(string name, Action action)
        {
            _port.Scenario = name;
            Console.WriteLine($"-- {name}");
            action();
        }

        private static void Pulse(string name, AxisId axis, double guideRateDegSec, bool isRa)
        {
            _port.Scenario = name;
            Console.WriteLine($"-- {name}");
            var started = new ManualResetEventSlim(false);
            Run(new SkyAxisPulse(SkyQueue.NewId, axis, guideRateDegSec, 500, 0, CancellationToken.None, started));
            started.Wait(TimeSpan.FromSeconds(5));
            // AxisPulse runs on its own task: wait for the restore phase to finish so the
            // transcript stays attributed to this scenario.
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 6000 && (isRa ? SkyQueue.IsPulseGuidingRa : SkyQueue.IsPulseGuidingDec))
            {
                Thread.Sleep(20);
            }
            Thread.Sleep(150); // settle: trailing logging/reads after the flag clears
        }

        private static void Run(ISkyCommand command)
        {
            var result = SkyQueue.GetCommandResult(command);
            if (!result.Successful && result.Exception != null)
            {
                Console.WriteLine($"   !! {command.GetType().Name}: {result.Exception.Message}");
            }
        }
    }
}
