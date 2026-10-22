using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using TianWen.DAL;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// No call into a vendor's SDK runs on a camera the SDK has closed (#1758). On hardware such a call is a use-after-free
/// inside the vendor's library, an access violation no .NET handler catches, which ends the node with any cooler left as
/// it was. A close waits for the calls inside the SDK to come back, gives up rather than close under one that does not,
/// and a call after the close is refused at the gate without reaching the SDK.
/// </summary>
[Collection("Devices")]
public class DALCameraSdkGateTests(ITestOutputHelper output)
{
    [Fact(Timeout = 60_000)]
    public async Task ADisconnectNeverClosesUnderACallStillInsideTheSdk()
    {
        var ct = TestContext.Current.CancellationToken;
        var (camera, state, _) = await ScriptedDalCamera.ConnectAsync(output);
        using var inside = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        state.OnGetControl = control =>
        {
            if (control is CMOSControlType.TemperatureDeci)
            {
                inside.Set();
                release.Wait(ct);
            }
        };

        // A telemetry read held inside the SDK, as a poll from the GUI or the node would be.
        var read = Task.Run(async () => await camera.GetCCDTemperatureAsync(ct), ct);
        inside.Wait(ct);
        camera.SdkCallsInFlight.ShouldBe(1);

        // The disconnect waits for it, and (the fake clock running out its bound at once) gives up rather than close.
        await Should.ThrowAsync<System.InvalidOperationException>(async () => await camera.DisconnectAsync(ct));
        state.Closes.ShouldBe(0, "closing under a call inside the SDK is the crash");
        camera.Connected.ShouldBeFalse("a disconnect that could not close leaves the driver failed, never connected");

        // Once the call has left, a disconnect closes it, and nothing ever reached a closed camera.
        state.OnGetControl = null;
        release.Set();
        (await read).ShouldBe(20.0);
        camera.SdkCallsInFlight.ShouldBe(0);
        await camera.DisconnectAsync(ct);
        state.Closes.ShouldBe(1);
        state.CallsWhileClosed.ShouldBe(0);
    }

    [Fact(Timeout = 60_000)]
    public async Task ACallAfterTheCloseIsRefusedWithoutReachingTheSdk()
    {
        var ct = TestContext.Current.CancellationToken;
        var (camera, state, _) = await ScriptedDalCamera.ConnectAsync(output);
        await camera.DisconnectAsync(ct);
        state.Closes.ShouldBe(1);

        (await camera.GetCCDTemperatureAsync(ct)).ShouldBe(double.NaN, "a closed camera's temperature is unknown");
        (await camera.GetCoolerOnAsync(ct)).ShouldBeFalse();
        await Should.ThrowAsync<System.InvalidOperationException>(async () => await camera.GetCoolerPowerAsync(ct));
        state.CallsWhileClosed.ShouldBe(0);
        camera.SdkCallsInFlight.ShouldBe(0);
    }

    [Fact(Timeout = 60_000)]
    public async Task AReconnectOpensTheGateAgain()
    {
        var ct = TestContext.Current.CancellationToken;
        var (camera, _, _) = await ScriptedDalCamera.ConnectAsync(output);
        await camera.DisconnectAsync(ct);
        await camera.ConnectAsync(ct);

        (await camera.GetCCDTemperatureAsync(ct)).ShouldBe(20.0);
    }
}
