using Meziantou.Extensions.Logging.Xunit.v3;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Gemini;
using TianWen.Lib.Extensions;
using Xunit;

namespace TianWen.Lib.Tests.Simulators;

/// <summary>
/// A BENCH PROCEDURE, not a pass/fail test: what the real driver does when the USB cable is pulled mid-move
/// and plugged back (#781, #780). Gated on <see cref="SimulatorGate.GeminiFocuserPortVar"/> AND
/// <c>TIANWEN_GEMINI_UNPLUG_BENCH</c> (a path for a live log, or <c>1</c> for test output only), because it
/// needs a person at the cable.
/// <para>Procedure: the board beeps as the driver connects; about 2.5 s later a move of up to 8000 steps starts. Pull the
/// USB cable while the motor runs, wait about 5 s, plug it back. For 60 s the harness polls position and
/// is-moving every 250 ms, logs what each read returns or throws, and on a failure retries
/// <see cref="IDeviceDriver.ConnectAsync"/> every 2 s the way the session's resilience layer does, then logs
/// the position the board reports once it answers again.</para>
/// </summary>
public class GeminiFocuserUnplugBench(ITestOutputHelper testOutputHelper)
{
    private const string BenchVar = "TIANWEN_GEMINI_UNPLUG_BENCH";

    [Fact]
    public async Task PullTheCableMidMoveAndPlugItBack()
    {
        if (SimulatorGate.GeminiFocuserPort is not { } port || Environment.GetEnvironmentVariable(BenchVar) is not { Length: > 0 } bench)
        {
            Assert.Skip($"A bench procedure: set {SimulatorGate.GeminiFocuserPortVar} and {BenchVar} (see the class remarks).");
            return;
        }

        var ct = TestContext.Current.CancellationToken;
        var logFile = bench == "1" ? null : bench;
        var clock = Stopwatch.StartNew();
        void Log(string line)
        {
            var stamped = $"{clock.Elapsed.TotalSeconds,6:0.00} s  {line}";
            testOutputHelper.WriteLine(stamped);
            if (logFile is not null)
            {
                File.AppendAllText(logFile, stamped + Environment.NewLine);
            }
        }

        await using var sp = new ServiceCollection()
            .AddExternal()
            .AddAstrometry()
            .AddLogging(b => b.AddProvider(new XUnitLoggerProvider(testOutputHelper, false)).SetMinimumLevel(LogLevel.Information))
            .BuildServiceProvider();

        var timeProvider = sp.GetRequiredService<ITimeProvider>();
        var device = new GeminiFocuserDevice("GeminiFocuser_bench", $"Gemini Focuser Pro on {port}", port);
        if (device.TryInstantiateDriver(sp, out IFocuserDriver? focuser) is not true)
        {
            Assert.Fail($"Could not instantiate Gemini Focuser driver for {port}");
            return;
        }

        await using (focuser)
        {
            await focuser.ConnectAsync(ct);
            var start = await focuser.GetPositionAsync(ct);
            // Long enough to reach the cable in time: 8000 steps is about 25 s on a fast-geared unit.
            var target = Math.Min(focuser.MaxStep, start + 8000);
            Log($"connected at {start}; moving to {target}: PULL THE CABLE WHILE IT RUNS");
            await focuser.BeginMoveAsync(target, ct);

            var lastReconnect = TimeSpan.MinValue;
            var faulted = false;
            while (clock.Elapsed < TimeSpan.FromSeconds(60))
            {
                try
                {
                    var pos = await focuser.GetPositionAsync(ct);
                    var moving = await focuser.GetIsMovingAsync(ct);
                    Log($"position {pos}, moving {moving}{(faulted ? "  <- answering again" : "")}");
                    faulted = false;
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    faulted = true;
                    Log($"read threw {ex.GetType().Name}: {ex.Message}");
                    if (clock.Elapsed - lastReconnect >= TimeSpan.FromSeconds(2))
                    {
                        lastReconnect = clock.Elapsed;
                        try
                        {
                            await focuser.ConnectAsync(ct);
                            Log("ConnectAsync succeeded");
                        }
                        catch (Exception cex) when (cex is not OperationCanceledException || !ct.IsCancellationRequested)
                        {
                            Log($"ConnectAsync threw {cex.GetType().Name}: {cex.Message}");
                        }
                    }
                }

                await timeProvider.SleepAsync(TimeSpan.FromMilliseconds(250), ct);
            }

            Log($"done; the move started at {start} and aimed at {target}");
            await focuser.DisconnectAsync(ct);
        }
    }
}
