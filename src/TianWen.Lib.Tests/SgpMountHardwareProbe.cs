using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Meziantou.Extensions.Logging.Xunit.v3;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shouldly;
using TianWen.Lib.Connections;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Discovery;
using TianWen.Lib.Devices.IOptron;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// A real iOptron SkyGuider Pro through our probe and our driver: the probe matches it, <see cref="SgpMountDriver"/>
/// connects, reads its status, moves RA east for a few seconds at full speed, stops, and reads its status until the
/// servo has come to rest. Every state the driver reports is printed against the clock, and the serial traffic with it.
/// </summary>
/// <remarks>
/// <para>Set <c>TIANWEN_SGP_PORT</c> to the mount's port (<c>COM12</c>) with nothing else holding it. It MOVES the RA
/// axis, so only with nothing on the mount that could hit anything. <c>TIANWEN_SGP_TRAFFIC=1</c> logs every byte.
/// Real clock.</para>
/// <para>Written on the bench of 2026-11-05, after a raw pyserial pass found the mount answering a move or stop
/// (<c>:MSMR</c>) with a STATUS first and its <c>:HRMR</c> acknowledgement only on the NEXT command (once twice), and the
/// status reading <c>:HRAS02...</c> while moving where <c>sgp-protocol.md</c> says the field is a fixed <c>01</c>. The
/// assertions at the end are what a correct driver reports through that.</para>
/// </remarks>
[Collection("Devices")]
public class SgpMountHardwareProbe(ITestOutputHelper output)
{
    /// <summary>Above 144x sidereal, so the driver picks its fastest speed.</summary>
    private const double EastDegPerSec = -1.0;

    private static readonly TimeSpan MoveFor = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan WatchAfterStop = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PollEvery = TimeSpan.FromMilliseconds(500);

    [Fact(Timeout = 120_000)]
    public async Task TheMountIsFoundMovedAndStoppedThroughTheDriver()
    {
        var port = Environment.GetEnvironmentVariable("TIANWEN_SGP_PORT");
        Assert.SkipWhen(string.IsNullOrEmpty(port), "TIANWEN_SGP_PORT is not set (this MOVES the attached SkyGuider Pro)");
        var ct = TestContext.Current.CancellationToken;
        var serialPort = $"{ISerialConnection.SerialProto}{port}";
        var external = new RealSerialExternal(output);

        ISerialProbe probe = new IOptronSerialProbe();
        await using (var probeConn = await external.OpenSerialDeviceAsync(serialPort, probe.BaudRate, probe.Encoding, probe.AssertControlLines, ct))
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            budget.CancelAfter(probe.Budget);
            var match = (await probe.ProbeAsync(serialPort, probeConn, budget.Token)).ShouldNotBeNull("the probe knows the mount");
            output.WriteLine($"probe: {match.DeviceUri}");
        }

        await using var sp = new ServiceCollection()
            .AddSingleton<IExternal>(external)
            .AddSingleton<ITimeProvider>(SystemTimeProvider.Instance)
            .AddLogging(logging => logging.AddProvider(new XUnitLoggerProvider(output, new XUnitLoggerOptions { TimestampFormat = "HH:mm:ss.fff" })).SetMinimumLevel(LogLevel.Debug))
            .BuildServiceProvider();
        var device = new IOptronDevice(DeviceType.Mount, $"SkyGuider-Pro_{port}", $"iOptron SkyGuider Pro on {port}", serialPort);
        var driver = (IMountDriver)new SgpMountDriver(device, sp);
        var clock = Stopwatch.StartNew();
        await driver.ConnectAsync(ct);
        output.WriteLine($"connect {clock.ElapsedMilliseconds} ms");

        var duringMove = new List<(bool Tracking, bool Slewing)>();
        var afterStop = new List<(bool Tracking, bool Slewing)>();
        try
        {
            var atRest = await Report(driver, clock, "at rest");

            clock.Restart();
            await driver.MoveAxisAsync(TelescopeAxis.Primary, EastDegPerSec, ct);
            output.WriteLine($"{clock.ElapsedMilliseconds,6} ms  MoveAxis east returned");
            while (clock.Elapsed < MoveFor)
            {
                await Task.Delay(PollEvery, ct);
                duringMove.Add(await Report(driver, clock, "moving"));
            }

            await driver.MoveAxisAsync(TelescopeAxis.Primary, 0, ct);
            output.WriteLine($"{clock.ElapsedMilliseconds,6} ms  MoveAxis stop returned");
            var stoppedAt = clock.Elapsed;
            while (clock.Elapsed - stoppedAt < WatchAfterStop)
            {
                await Task.Delay(PollEvery, ct);
                afterStop.Add(await Report(driver, clock, "after stop"));
            }

            atRest.Tracking.ShouldBeTrue("a connected SGP at rest is tracking");
        }
        finally
        {
            // Whatever failed above, the axis is told to stop before the port closes.
            await driver.MoveAxisAsync(TelescopeAxis.Primary, 0, CancellationToken.None);
            await driver.DisconnectAsync(CancellationToken.None);
        }

        duringMove.ShouldAllBe(s => s.Slewing, "the axis is moving throughout");
        duringMove.ShouldAllBe(s => s.Tracking, "a status read while moving still parses (the mount answers :HRAS02...)");
        afterStop[^1].Slewing.ShouldBeFalse("the servo has come to rest five seconds after the stop");
        afterStop.ShouldAllBe(s => s.Tracking, "every status read after the stop parses, the late :HRMR ack notwithstanding");
    }

    private async Task<(bool Tracking, bool Slewing)> Report(IMountDriver driver, Stopwatch clock, string when)
    {
        var ct = TestContext.Current.CancellationToken;
        var tracking = await driver.IsTrackingAsync(ct);
        var slewing = await driver.IsSlewingAsync(ct);
        var speed = await driver.GetTrackingSpeedAsync(ct);
        output.WriteLine($"{clock.ElapsedMilliseconds,6} ms  {when,-10} tracking={tracking} slewing={slewing} speed={speed}");
        return (tracking, slewing);
    }

    /// <summary>The test external, but opening a REAL serial port.</summary>
    private sealed class RealSerialExternal(ITestOutputHelper output) : FakeExternal(output)
    {
        private readonly ILogger _logger = LoggerFactory.Create(logging => logging
            .AddProvider(new XUnitLoggerProvider(output, new XUnitLoggerOptions { TimestampFormat = "HH:mm:ss.fff" }))
            .SetMinimumLevel(LogLevel.Trace)).CreateLogger("serial");

        public override async ValueTask<ISerialConnection> OpenSerialDeviceAsync(string address, int baud, Encoding encoding, bool assertControlLines = false, CancellationToken cancellationToken = default)
        {
            var conn = await SerialConnection.OpenAsync(address, baud, encoding, _logger, assertControlLines, new SystemTimeProvider(), cancellationToken);
            conn.LogVerbose = Environment.GetEnvironmentVariable("TIANWEN_SGP_TRAFFIC") == "1";
            return conn;
        }
    }
}
