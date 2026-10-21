using System;
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
using TianWen.Lib.Devices.Gemini;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// A real Gemini Power&amp;Data Hub Advanced 3 through the probe and the driver: the probe matches it, the driver connects,
/// reads every channel from the frames the box pushes, and (only when asked) switches one output off and on again.
/// </summary>
/// <remarks>
/// Set <c>TIANWEN_GEMINI_POWERBOX_PORT</c> to the box's port (<c>COM5</c>) with nothing else holding it: that alone only
/// reads. <c>TIANWEN_GEMINI_POWERBOX_SWITCH=1</c> also switches USB F off and back on, so leave USB F free. Real clock.
/// </remarks>
[Collection("Devices")]
public class GeminiPowerBoxHardwareProbe(ITestOutputHelper output)
{
    /// <summary>USB F, the output the switching arm toggles.</summary>
    private const int UsbF = 10;

    [Fact(Timeout = 120_000)]
    public async Task TheBoxIsFoundReadAndSwitched()
    {
        var port = Environment.GetEnvironmentVariable("TIANWEN_GEMINI_POWERBOX_PORT");
        Assert.SkipWhen(string.IsNullOrEmpty(port), "TIANWEN_GEMINI_POWERBOX_PORT is not set (this drives the attached power box)");
        var ct = TestContext.Current.CancellationToken;
        var serialPort = $"{ISerialConnection.SerialProto}{port}";
        var external = new RealSerialExternal(output);

        // The probe, on its own handle with the control lines asserted, as the probe service's isolated pass opens it.
        var probe = new GeminiPowerBoxSerialProbe();
        await using (var probeConn = await external.OpenSerialDeviceAsync(serialPort, probe.BaudRate, probe.Encoding, probe.AssertControlLines, ct))
        {
            await Task.Delay(probe.Warmup, ct);
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            budget.CancelAfter(probe.Budget);
            var match = (await probe.ProbeAsync(serialPort, probeConn, budget.Token)).ShouldNotBeNull("the probe knows the box");
            output.WriteLine($"probe: {match.DeviceUri} firmware {match.Metadata?["firmware"]}");
        }

        await using var sp = new ServiceCollection()
            .AddSingleton<IExternal>(external)
            .AddSingleton<ITimeProvider>(SystemTimeProvider.Instance)
            .AddLogging(logging => logging.AddProvider(new XUnitLoggerProvider(output, new XUnitLoggerOptions { TimestampFormat = "HH:mm:ss.fff" })).SetMinimumLevel(LogLevel.Debug))
            .BuildServiceProvider();
        var device = new GeminiPowerBoxDevice($"GeminiPowerBox_{port}", $"Gemini power box on {port}", serialPort);
        var driver = (ISwitchDriver)new GeminiPowerBoxDriver(device, sp);
        var watch = Stopwatch.StartNew();
        await driver.ConnectAsync(ct);
        output.WriteLine($"connect {watch.ElapsedMilliseconds} ms");
        try
        {
            await Dump(driver, "after connect");

            // The frames keep coming: one at least 4 s later is the box pushing, not the connect's reply.
            var first = ((GeminiPowerBoxDriver)driver).LatestStatus.ShouldNotBeNull().At;
            await Task.Delay(TimeSpan.FromSeconds(4), ct);
            ((GeminiPowerBoxDriver)driver).LatestStatus.ShouldNotBeNull().At.ShouldBeGreaterThan(first, "the box pushes status unasked");

            if (Environment.GetEnvironmentVariable("TIANWEN_GEMINI_POWERBOX_SWITCH") == "1")
            {
                (await driver.GetValueAsync(UsbF, ct)).ShouldBe(1d, "USB F starts on");
                watch.Restart();
                await driver.SetValueAsync(UsbF, 0, ct);
                output.WriteLine($"USB F off, confirmed in {watch.ElapsedMilliseconds} ms");
                (await driver.GetValueAsync(UsbF, ct)).ShouldBe(0d);
                watch.Restart();
                await driver.SetValueAsync(UsbF, 1, ct);
                output.WriteLine($"USB F on, confirmed in {watch.ElapsedMilliseconds} ms");
                (await driver.GetValueAsync(UsbF, ct)).ShouldBe(1d);
            }
        }
        finally
        {
            await driver.DisconnectAsync(ct);
        }
    }

    private async Task Dump(ISwitchDriver driver, string when)
    {
        var ct = TestContext.Current.CancellationToken;
        var line = new StringBuilder(when).Append(':');
        foreach (var channel in driver.Channels)
        {
            line.Append($" {channel.Name}={await driver.GetValueAsync(channel.Index, ct)}{channel.Unit}");
        }

        output.WriteLine(line.ToString());
    }

    /// <summary>The test external, but opening a REAL serial port.</summary>
    private sealed class RealSerialExternal(ITestOutputHelper output) : FakeExternal(output)
    {
        private readonly ILogger _logger = LoggerFactory.Create(logging => logging
            .AddProvider(new XUnitLoggerProvider(output, new XUnitLoggerOptions { TimestampFormat = "HH:mm:ss.fff" }))
            .SetMinimumLevel(LogLevel.Trace)).CreateLogger("serial");

        public override async ValueTask<ISerialConnection> OpenSerialDeviceAsync(string address, int baud, Encoding encoding, bool assertControlLines = false, CancellationToken cancellationToken = default)
        {
            var conn = await SerialConnection.OpenAsync(address, baud, encoding, _logger, assertControlLines, cancellationToken);
            conn.LogVerbose = Environment.GetEnvironmentVariable("TIANWEN_GEMINI_POWERBOX_TRAFFIC") == "1";
            return conn;
        }
    }
}
