using Microsoft.Extensions.Logging;
using Shouldly;
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Connections;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Gemini;
using TianWen.Lib.Sequencing;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// <see cref="GeminiFocuserDriver"/> against an in-memory myFocuserPro2 (<see cref="FakeGeminiFocuserSerialDevice"/>).
/// A controller that found no temperature probe at boot still answers <c>:06#</c>, with a placeholder
/// (20.00 C stock, 18.00 on the Gemini build, both measured, #654) that reads like a room temperature; the
/// driver must report it as unavailable rather than pass it on.
/// </summary>
[Collection("Device")]
public class GeminiFocuserDriverTests(ITestOutputHelper output)
{
    /// <summary>A GeminiFocuserDevice that connects to an in-memory fake instead of a real COM port.</summary>
    private sealed record TestGeminiFocuserDevice(Uri DeviceUri, ISerialConnection Conn) : GeminiFocuserDevice(DeviceUri)
    {
        public override ValueTask<ISerialConnection?> ConnectSerialDeviceAsync(
            IExternal external, ILogger logger, ITimeProvider timeProvider,
            int baud = GeminiFocuserProtocol.Baud, Encoding? encoding = null, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<ISerialConnection?>(Conn);
    }

    private static readonly Uri DeviceUri = new("focuser://GeminiFocuserDevice/GeminiFocuser_COM9?port=serial:COM9#Gemini Focuser Pro");

    private IFocuserDriver CreateDriver(ISerialConnection conn)
    {
        var sp = new FakeExternal(output).BuildServiceProvider();
        new TestGeminiFocuserDevice(DeviceUri, conn).TryInstantiateDriver<IFocuserDriver>(sp, out var driver).ShouldBeTrue();
        return driver.ShouldNotBeNull();
    }

    [Fact(Timeout = 30_000)]
    public async Task AProbeFoundAtBootIsRead()
    {
        var ct = TestContext.Current.CancellationToken;
        var conn = new FakeGeminiFocuserSerialDevice(tempCompAvailable: true) { Temperature = 22.63 };
        await using var driver = CreateDriver(conn);

        await driver.ConnectAsync(ct);

        driver.TempCompAvailable.ShouldBeTrue();
        (await driver.GetTemperatureAsync(ct)).ShouldBe(22.63, 0.001);
    }

    [Fact(Timeout = 30_000)]
    public async Task NoProbeReadsAsUnavailableNotAsTheFirmwarePlaceholder()
    {
        var ct = TestContext.Current.CancellationToken;
        var conn = new FakeGeminiFocuserSerialDevice(tempCompAvailable: false) { Temperature = 22.63 };
        await using var driver = CreateDriver(conn);

        await driver.ConnectAsync(ct);

        // The precondition: the wire still answers a temperature, the placeholder, so the NaN below is the gate.
        (await GeminiFocuserProtocol.GetTemperatureAsync(conn, ct)).ShouldBe(20.0, 0.001);
        driver.TempCompAvailable.ShouldBeFalse();
        double.IsNaN(await driver.GetTemperatureAsync(ct)).ShouldBeTrue("no probe was found at boot, so there is no temperature to report");
    }

    [Fact(Timeout = 30_000)]
    public async Task AReadWithNoReplyThrowsRatherThanReportingNotMovingOrASentinel()
    {
        var ct = TestContext.Current.CancellationToken;
        var conn = new FakeGeminiFocuserSerialDevice { Moving = true };
        await using var driver = CreateDriver(conn);
        await driver.ConnectAsync(ct);

        conn.Dead = true;

        // #781: "not moving" here ended a move-wait while the focuser travelled on, and int.MinValue was a
        // position a caller had to know to distrust.
        await Should.ThrowAsync<IOException>(async () => await driver.GetIsMovingAsync(ct));
        await Should.ThrowAsync<IOException>(async () => await driver.GetPositionAsync(ct));
    }

    [Fact(Timeout = 30_000)]
    public async Task ADisconnectedFocuserStillReportsNoPositionAndNotMoving()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var driver = CreateDriver(new FakeGeminiFocuserSerialDevice { Moving = true });
        await driver.ConnectAsync(ct);
        await driver.DisconnectAsync(ct);

        (await driver.GetPositionAsync(ct)).ShouldBe(int.MinValue);
        (await driver.GetIsMovingAsync(ct)).ShouldBeFalse();
    }

    [Fact(Timeout = 30_000)]
    public async Task ATransientNoReplyIsRetriedByTheResilienceLayerNotReadAsNotMoving()
    {
        var ct = TestContext.Current.CancellationToken;
        var conn = new FakeGeminiFocuserSerialDevice { Moving = true };
        await using var driver = CreateDriver(conn);
        await driver.ConnectAsync(ct);

        conn.DropReplies = 1;
        var moving = await ResilientCall.InvokeAsync(driver, driver.GetIsMovingAsync, ResilientCallOptions.IdempotentRead, ct);

        moving.ShouldBeTrue("the one lost reply was retried, and the focuser is still moving");
        conn.WrittenCommands.Count(c => c == ":01#").ShouldBe(2, "one lost query, one retried");
    }
}
