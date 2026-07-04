using Meziantou.Extensions.Logging.Xunit.v3;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shouldly;
using System;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Gemini;
using TianWen.Lib.Extensions;
using Xunit;

namespace TianWen.Lib.Tests.Simulators;

/// <summary>
/// Live-hardware test for the native (ASCOM-free) Gemini FlatPanel Lite serial driver. Unlike the ASCOM /
/// Alpaca legs there is NO Gemini simulator, so this drives a physically-connected panel: opt in by setting
/// <see cref="SimulatorGate.GeminiFlatPanelPortVar"/> to its port (e.g. <c>serial:COM3</c>); it skips cleanly when
/// unset so a bare <c>dotnet test</c> stays green.
/// <para>
/// It exercises the <b>manual-assignment</b> path -- the only one that works on a CH34x USB bridge, whose
/// DTR-reset behaviour makes the panel invisible to auto-discovery (the probe deliberately does not assert
/// DTR; see <c>docs/todo/drivers.md</c>). The driver's own <see cref="GeminiFlatPanelDriver.ConnectAsync"/>
/// asserts DTR+RTS, so a URI-assigned device connects where discovery cannot. Uses the real
/// <see cref="IExternal"/> (real serial + <see cref="SystemTimeProvider"/>) -- never a fake clock, since the
/// driver's inter-command settle and the ramp cadence are genuine wall-clock waits.
/// </para>
/// </summary>
public class GeminiFlatPanelHardwareTests(ITestOutputHelper testOutputHelper)
{
    // Ramp stops (0..MaxBrightness). Each BeginCalibratorOn re-sends >L# + a 250ms settle + >B<n>#, so the
    // visible cadence is dominated by the driver's own settle; a short extra pause keeps steps distinct.
    private static readonly int[] RampUp = [0, 32, 64, 96, 128, 160, 192, 224, 255];
    private static readonly TimeSpan StepPause = TimeSpan.FromMilliseconds(120);

    [Fact]
    public async Task GivenAConnectedGeminiFlatPanelWhenBrightnessRampedThenItTracksAndReportsReady()
    {
        if (SimulatorGate.GeminiFlatPanelPort is not { } port)
        {
            Assert.Skip($"Set {SimulatorGate.GeminiFlatPanelPortVar} to a connected Gemini FlatPanel Lite port (e.g. serial:COM3)");
            return;
        }

        var ct = TestContext.Current.CancellationToken;

        // Real IExternal (real serial + SystemTimeProvider) + xUnit-routed logging so the connect handshake
        // and any driver warnings surface in the test output.
        await using var sp = new ServiceCollection()
            .AddExternal()
            .AddAstrometry() // External's ctor resolves ICelestialObjectDB; the Gemini driver never touches it.
            .AddLogging(b => b.AddProvider(new XUnitLoggerProvider(testOutputHelper, false)))
            .BuildServiceProvider();

        var timeProvider = sp.GetRequiredService<ITimeProvider>();

        // Manual assignment: CoverCalibrator://GeminiDevice/<id>?port=serial:COMx  (the driver's connect asserts DTR).
        var device = new GeminiDevice("Gemini_hw", $"Gemini FlatPanel Lite on {port}", port);
        if (device.TryInstantiateDriver(sp, out ICoverDriver? cover) is true)
        {
            await using (cover)
            {
                // Connect asserts DTR+RTS, verifies the >H# identity, and logs firmware -- throws if it is not a Gemini.
                await cover.ConnectAsync(ct);
                cover.Connected.ShouldBeTrue();
                cover.MaxBrightness.ShouldBe(GeminiFlatPanelProtocol.MaxBrightness);

                // No motorised flap on this panel.
                (await cover.GetCoverStateAsync(ct)).ShouldBe(CoverStatus.NotPresent);

                testOutputHelper.WriteLine($"connected: state={await cover.GetCalibratorStateAsync(ct)}, brightness={await cover.GetBrightnessAsync(ct)}");

                // Ramp up.
                foreach (var target in RampUp)
                {
                    await cover.BeginCalibratorOn(target, ct);
                    var readback = await cover.GetBrightnessAsync(ct);
                    testOutputHelper.WriteLine($"up   -> set {target,3}, readback {readback,3}");
                    await timeProvider.SleepAsync(StepPause, ct);
                }

                // At full brightness the calibrator reports Ready and the panel tracks the requested level.
                (await cover.GetCalibratorStateAsync(ct)).ShouldBe(CalibratorStatus.Ready);
                (await cover.GetBrightnessAsync(ct)).ShouldBe(GeminiFlatPanelProtocol.MaxBrightness);

                // Audible confirmation at the peak: a short beep pulse (>T1# .. >T0#). Diagnostic only —
                // the beeper is not part of ICoverDriver, so reach through the concrete driver.
                if (cover is GeminiFlatPanelDriver gemini)
                {
                    await gemini.SetBeeperAsync(on: true, ct);
                    await timeProvider.SleepAsync(TimeSpan.FromMilliseconds(250), ct);
                    await gemini.SetBeeperAsync(on: false, ct);
                }

                // Ramp down.
                for (var i = RampUp.Length - 1; i >= 0; i--)
                {
                    var target = RampUp[i];
                    await cover.BeginCalibratorOn(target, ct);
                    var readback = await cover.GetBrightnessAsync(ct);
                    testOutputHelper.WriteLine($"down -> set {target,3}, readback {readback,3}");
                    await timeProvider.SleepAsync(StepPause, ct);
                }

                // Off.
                await cover.BeginCalibratorOff(ct);
                (await cover.GetCalibratorStateAsync(ct)).ShouldBe(CalibratorStatus.Off);

                await cover.DisconnectAsync(ct);
                cover.Connected.ShouldBeFalse();
            }
        }
        else
        {
            Assert.Fail($"Could not instantiate Gemini FlatPanel driver for {port}");
        }
    }
}
