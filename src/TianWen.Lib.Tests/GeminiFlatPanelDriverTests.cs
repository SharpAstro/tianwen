using Microsoft.Extensions.Logging;
using Shouldly;
using System;
using System.Collections.Generic;
using System.Linq;
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

    /// <summary>A GeminiDevice whose connect hands out a sequence of connections (reconnect tests).</summary>
    private sealed record SequencedGeminiDevice(Uri DeviceUri, Queue<ISerialConnection> Connections) : GeminiDevice(DeviceUri)
    {
        public override ValueTask<ISerialConnection?> ConnectSerialDeviceAsync(
            IExternal external, ILogger logger, ITimeProvider timeProvider,
            int baud = GeminiFlatPanelProtocol.Baud, Encoding? encoding = null, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<ISerialConnection?>(Connections.Count > 0 ? Connections.Dequeue() : null);
    }

    private static readonly Uri DeviceUri = new("covercalibrator://GeminiDevice/Gemini_COM4?port=serial:COM4#Gemini FlatPanel Lite");

    private static ICoverDriver CreateDriver(ITestOutputHelper output, ISerialConnection conn)
        => CreateDriver(output, new TestGeminiDevice(DeviceUri, conn));

    private static ICoverDriver CreateDriver(ITestOutputHelper output, GeminiDevice device)
    {
        var sp = new FakeExternal(output).BuildServiceProvider();
        device.TryInstantiateDriver<ICoverDriver>(sp, out var driver).ShouldBeTrue();
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

    [Fact(Timeout = 30_000)]
    public async Task Reconnect_rebuilds_the_connection_when_a_nominally_open_port_stops_answering()
    {
        var ct = TestContext.Current.CancellationToken;
        var conn1 = new FakeGeminiFlatPanelSerialDevice();
        var conn2 = new FakeGeminiFlatPanelSerialDevice();
        var driver = CreateDriver(output, new SequencedGeminiDevice(DeviceUri, new Queue<ISerialConnection>([conn1, conn2])));

        await ((IDeviceDriver)driver).ConnectAsync(ct);
        driver.Connected.ShouldBeTrue();

        // The CH341 unplug case: the port stops answering while IsOpen still reads true, so a reconnect
        // (e.g. ResilientCall's fault callback) must NOT no-op on the stale handle.
        conn1.Dead = true;

        await ((IDeviceDriver)driver).ConnectAsync(ct);

        driver.Connected.ShouldBeTrue();
        conn1.IsOpen.ShouldBeFalse();               // stale connection closed (evicts it from the reuse cache)
        conn2.WrittenCommands.ShouldContain(">H#"); // rebuilt + identity-verified on the fresh connection

        // The driver now talks to the fresh connection.
        await driver.BeginCalibratorOn(64, ct);
        conn2.Brightness.ShouldBe(64);
        (await driver.GetCalibratorStateAsync(ct)).ShouldBe(CalibratorStatus.Ready);
    }

    [Fact(Timeout = 30_000)]
    public async Task Reconnect_on_a_live_connection_reverifies_without_rebuilding()
    {
        var ct = TestContext.Current.CancellationToken;
        var conn = new FakeGeminiFlatPanelSerialDevice();
        var driver = CreateDriver(output, conn);

        await ((IDeviceDriver)driver).ConnectAsync(ct);
        await ((IDeviceDriver)driver).ConnectAsync(ct);

        driver.Connected.ShouldBeTrue();
        conn.IsOpen.ShouldBeTrue();
        // First connect: identity + firmware; second connect: the cheap liveness re-verify only.
        conn.WrittenCommands.Count(c => c == ">H#").ShouldBe(2);
        conn.WrittenCommands.Count(c => c == ">V#").ShouldBe(1);
    }
}
