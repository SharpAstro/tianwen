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
/// Live-hardware test for the native (ASCOM-free) Gemini Focuser Pro serial driver. There is NO Gemini
/// simulator, so this drives a physically-connected focuser: opt in by setting
/// <see cref="SimulatorGate.GeminiFocuserPortVar"/> to its port (e.g. <c>serial:COM5</c>); it skips cleanly
/// when unset so a bare <c>dotnet test</c> stays green.
/// <para>
/// It exercises the <b>manual-assignment</b> path via a URI-addressed device -- the driver's own
/// <see cref="GeminiFocuserDriver.ConnectAsync"/> asserts DTR+RTS and waits out the Arduino boot, then reads
/// capabilities and performs a small, self-reversing move so the mechanism is only nudged. Uses the real
/// <see cref="IExternal"/> (real serial + <see cref="SystemTimeProvider"/>) -- never a fake clock, since the
/// connect boot delay and move-settle polling are genuine wall-clock waits.
/// </para>
/// </summary>
public class GeminiFocuserHardwareTests(ITestOutputHelper testOutputHelper)
{
    // A modest nudge so the test barely disturbs focus; clamped to the reported travel below.
    private const int MoveDelta = 500;
    private static readonly TimeSpan MoveTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    [Fact]
    public async Task GivenAConnectedGeminiFocuserWhenNudgedThenItMovesAndReportsPosition()
    {
        if (SimulatorGate.GeminiFocuserPort is not { } port)
        {
            Assert.Skip($"Set {SimulatorGate.GeminiFocuserPortVar} to a connected Gemini Focuser Pro port (e.g. serial:COM5)");
            return;
        }

        var ct = TestContext.Current.CancellationToken;

        await using var sp = new ServiceCollection()
            .AddExternal()
            .AddAstrometry() // External's ctor resolves ICelestialObjectDB; the Gemini driver never touches it.
            .AddLogging(b => b.AddProvider(new XUnitLoggerProvider(testOutputHelper, false)))
            .BuildServiceProvider();

        var timeProvider = sp.GetRequiredService<ITimeProvider>();

        // Manual assignment: Focuser://GeminiFocuserDevice/<id>?port=serial:COMx (the driver's connect asserts DTR).
        var device = new GeminiFocuserDevice("GeminiFocuser_hw", $"Gemini Focuser Pro on {port}", port);
        if (device.TryInstantiateDriver(sp, out IFocuserDriver? focuser) is not true)
        {
            Assert.Fail($"Could not instantiate Gemini Focuser driver for {port}");
            return;
        }

        await using (focuser)
        {
            await focuser.ConnectAsync(ct);
            focuser.Connected.ShouldBeTrue();
            focuser.Absolute.ShouldBeTrue();
            focuser.MaxStep.ShouldBeGreaterThan(0);

            var start = await focuser.GetPositionAsync(ct);
            var temp = await focuser.GetTemperatureAsync(ct);
            testOutputHelper.WriteLine(
                $"connected: pos={start}, maxStep={focuser.MaxStep}, stepSize={(focuser.CanGetStepSize ? focuser.StepSize.ToString("0.0") : "n/a")}, " +
                $"temp={(double.IsNaN(temp) ? "n/a" : temp.ToString("0.0"))}, tempCompAvailable={focuser.TempCompAvailable}");

            start.ShouldBeGreaterThanOrEqualTo(0);

            // Nudge inward or outward, whichever stays within travel, then reverse back to the start.
            var target = start + MoveDelta <= focuser.MaxStep ? start + MoveDelta : Math.Max(0, start - MoveDelta);

            await MoveAndSettleAsync(focuser, target, timeProvider, ct);
            var afterMove = await focuser.GetPositionAsync(ct);
            testOutputHelper.WriteLine($"moved -> target {target}, landed {afterMove}");
            Math.Abs(afterMove - target).ShouldBeLessThanOrEqualTo(MoveDelta, "focuser should land at (or very near) the commanded position");

            await MoveAndSettleAsync(focuser, start, timeProvider, ct);
            var restored = await focuser.GetPositionAsync(ct);
            testOutputHelper.WriteLine($"restored -> target {start}, landed {restored}");

            await focuser.DisconnectAsync(ct);
            focuser.Connected.ShouldBeFalse();
        }
    }

    private static async Task MoveAndSettleAsync(IFocuserDriver focuser, int target, ITimeProvider timeProvider, CancellationToken ct)
    {
        await focuser.BeginMoveAsync(target, ct);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(MoveTimeout);
        while (await focuser.GetIsMovingAsync(cts.Token))
        {
            await timeProvider.SleepAsync(PollInterval, cts.Token);
        }
    }
}
