using Microsoft.Extensions.Logging;
using Shouldly;
using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib;
using TianWen.Lib.Connections;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Gemini;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// End-to-end tests for <see cref="GeminiFlatPanelDriver"/> against an in-memory controller: the connect
/// handshake (identity verify), the flap-less cover mapping, and the calibrator on/off + brightness cycle.
/// The driver obtains its serial connection from the device, so the test device hands it a
/// <see cref="FakeGeminiFlatPanelSerialDevice"/>.
/// </summary>
[Collection("Device")]
public class GeminiFlatPanelDriverTests(ITestOutputHelper output)
{
    /// <summary>A GeminiDevice that connects to an in-memory fake instead of a real COM port.</summary>
    private sealed record TestGeminiDevice(Uri DeviceUri, ISerialConnection Conn) : GeminiDevice(DeviceUri)
    {
        public override ValueTask<ISerialConnection?> ConnectSerialDeviceAsync(
            IExternal external, ILogger logger, ITimeProvider timeProvider,
            int baud = GeminiFlatPanelProtocol.Baud, Encoding? encoding = null, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<ISerialConnection?>(Conn);
    }

    private static readonly Uri DeviceUri = new("covercalibrator://GeminiDevice/Gemini_COM4?port=serial:COM4#Gemini FlatPanel Lite");

    private static ICoverDriver CreateDriver(ITestOutputHelper output, ISerialConnection conn)
    {
        var sp = new FakeExternal(output).BuildServiceProvider();
        new TestGeminiDevice(DeviceUri, conn).TryInstantiateDriver<ICoverDriver>(sp, out var driver).ShouldBeTrue();
        return driver!;
    }

    [Fact(Timeout = 30_000)]
    public async Task Connect_verifies_identity_then_reports_flapless_cover_and_calibrator_cycle()
    {
        var ct = TestContext.Current.CancellationToken;
        var conn = new FakeGeminiFlatPanelSerialDevice(firmware: 205);
        var driver = CreateDriver(output, conn);

        await ((IDeviceDriver)driver).ConnectAsync(ct);
        driver.Connected.ShouldBeTrue();

        // Flat panel: no motorised flap.
        (await driver.GetCoverStateAsync(ct)).ShouldBe(CoverStatus.NotPresent);
        (await driver.GetCalibratorStateAsync(ct)).ShouldBe(CalibratorStatus.Off);

        await driver.BeginCalibratorOn(128, ct);
        (await driver.GetCalibratorStateAsync(ct)).ShouldBe(CalibratorStatus.Ready);
        (await driver.GetBrightnessAsync(ct)).ShouldBe(128);

        await driver.BeginCalibratorOff(ct);
        (await driver.GetCalibratorStateAsync(ct)).ShouldBe(CalibratorStatus.Off);
    }

    [Fact]
    public async Task Connect_rejects_a_non_Gemini_device()
    {
        var ct = TestContext.Current.CancellationToken;
        var conn = new FakeGeminiFlatPanelSerialDevice(identity: "NotAGeminiPanel");
        var driver = CreateDriver(output, conn);

        await Should.ThrowAsync<InvalidOperationException>(async () => await ((IDeviceDriver)driver).ConnectAsync(ct));
        driver.Connected.ShouldBeFalse();
    }
}
