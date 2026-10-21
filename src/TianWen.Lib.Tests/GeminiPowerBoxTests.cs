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
using TianWen.Lib.Devices.Discovery;
using TianWen.Lib.Devices.Gemini;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The Gemini Power&amp;Data Hub Advanced 3: its codec read against the frame the bench box sent (2026-10-21), its probe, and
/// its driver against <see cref="FakeGeminiPowerBoxSerialDevice"/>. <see cref="GeminiPowerBoxHardwareProbe"/> is the same
/// against the real box.
/// </summary>
[Collection("Device")]
public class GeminiPowerBoxTests(ITestOutputHelper output)
{
    /// <summary>A status frame exactly as the bench box sent it, after <c>*G</c> and before <c>#</c>.</summary>
    private const string BenchFrame = "1111D111111U1A1T1A1M1B1M100C100C 24.81S 26.87T 56.00H 17.35D12.9V 0.16C  2.06P";

    private const int Dc1 = 0, Dc3 = 2, UsbE = 9, Dew6 = 11, Voltage = 13, Current = 14, Power = 15, Ambient = 16, Humidity = 17, DewPoint = 18, Lens = 19;

    [Fact]
    public void TheBenchFrameReadsAsTheBoxReportedIt()
    {
        var status = GeminiPowerBoxProtocol.ParseStatus(BenchFrame).ShouldNotBeNull();

        status.DcOn.ShouldBe([true, true, true, true]);
        status.UsbOn.ShouldBe([true, true, true, true, true, true]);
        status.HasAmbientSensor.ShouldBeTrue();
        status.HasLensProbe.ShouldBeTrue();
        status.Dew6Mode.ShouldBe(GeminiPowerBoxDewMode.Manual);
        status.Dew7Mode.ShouldBe(GeminiPowerBoxDewMode.Manual);
        status.Dew6Power.ShouldBe(100);
        status.LensTemperature.ShouldBe(24.81);
        status.AmbientTemperature.ShouldBe(26.87);
        status.Humidity.ShouldBe(56.00);
        status.DewPoint.ShouldBe(17.35);
        status.Voltage.ShouldBe(12.9);
        status.Current.ShouldBe(0.16);
        status.Power.ShouldBe(2.06);
    }

    [Theory]
    [InlineData("1111D111111U")] // cut short
    [InlineData("11D111111U1A1T1A1M1B1M100C100C 24.81S 26.87T 56.00H 17.35D12.9V 0.16C  2.06P")] // two DC bits, not four
    public void AFrameOfTheWrongShapeIsNoStatus(string payload) => GeminiPowerBoxProtocol.ParseStatus(payload).ShouldBeNull();

    [Theory]
    [InlineData("*HGeminiPowerBoxPlusAdv3", 'H', "GeminiPowerBoxPlusAdv3")]
    [InlineData("*V309#", 'V', "309")]
    [InlineData("0.16C  2.06P*V309", 'V', "309")] // a read that joined the stream mid-frame
    public void AFrameIsItsLetterAndPayload(string raw, char letter, string payload)
        => GeminiPowerBoxProtocol.ParseFrame(raw).ShouldBe((letter, payload));

    [Fact]
    public async Task TheProbeMatchesTheBoxAndReadsPastAStatusFrameAlreadyComing()
    {
        // Measured: the box was still pushing from the connection before when the probe opened the port.
        var box = new FakeGeminiPowerBoxSerialDevice();
        box.Push();

        var match = (await new GeminiPowerBoxSerialProbe().ProbeAsync("serial:COM5", box, TestContext.Current.CancellationToken)).ShouldNotBeNull();

        match.DeviceUri.Scheme.ShouldBe("switch");
        match.DeviceUri.Host.ShouldBe(nameof(GeminiPowerBoxDevice).ToLowerInvariant());
        match.DeviceUri.QueryValue(DeviceQueryKey.Port).ShouldBe("serial:COM5");
        match.Metadata.ShouldNotBeNull()["firmware"].ShouldBe("309");
    }

    [Fact]
    public async Task TheProbeLeavesAnotherDeviceAlone()
    {
        var other = new FakeGeminiPowerBoxSerialDevice(identity: "SomethingElse");

        (await new GeminiPowerBoxSerialProbe().ProbeAsync("serial:COM5", other, TestContext.Current.CancellationToken)).ShouldBeNull();
        other.WrittenCommands.ShouldBe([">H#"], "nothing but the identity query reaches a device that is not the box");
    }

    [Fact(Timeout = 30_000)]
    public async Task EveryChannelReadsFromTheBoxsFrame()
    {
        var ct = TestContext.Current.CancellationToken;
        var box = new FakeGeminiPowerBoxSerialDevice { Current = 1.25 };
        await using var driver = Connected(box, out _);
        await driver.ConnectAsync(ct);

        driver.Channels.Count.ShouldBe(20);
        driver.Channels.Count(c => c.Kind is SwitchChannelKind.Output).ShouldBe(11);
        driver.Channels.Count(c => c.Kind is SwitchChannelKind.Reading).ShouldBe(7);
        (await driver.GetValueAsync(Dc1, ct)).ShouldBe(1);
        (await driver.GetValueAsync(Voltage, ct)).ShouldBe(12.9);
        (await driver.GetValueAsync(Current, ct)).ShouldBe(1.25);
        (await driver.GetValueAsync(Power, ct)).ShouldBe(2.06);
        (await driver.GetValueAsync(Ambient, ct)).ShouldBe(26.87);
        (await driver.GetValueAsync(Humidity, ct)).ShouldBe(56.00);
        (await driver.GetValueAsync(DewPoint, ct)).ShouldBe(17.35);
        (await driver.GetValueAsync(Lens, ct)).ShouldBe(24.81);
        (await driver.GetValueAsync(Dew6, ct)).ShouldBe(100);
    }

    [Fact(Timeout = 30_000)]
    public async Task AnAbsentSensorReadsAsNaNNotAsAValue()
    {
        var ct = TestContext.Current.CancellationToken;
        var box = new FakeGeminiPowerBoxSerialDevice { HasAmbientSensor = false, HasLensProbe = false };
        await using var driver = Connected(box, out _);
        await driver.ConnectAsync(ct);

        foreach (var reading in new[] { Ambient, Humidity, DewPoint, Lens })
        {
            double.IsNaN(await driver.GetValueAsync(reading, ct)).ShouldBeTrue(driver.Channels[reading].Name);
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task ASetIsWrittenAndConfirmedByTheNextFrame()
    {
        var ct = TestContext.Current.CancellationToken;
        var box = new FakeGeminiPowerBoxSerialDevice();
        await using var driver = Connected(box, out _);
        await driver.ConnectAsync(ct);

        await driver.SetValueAsync(Dc3, 0, ct);
        await driver.SetValueAsync(UsbE, 0, ct);

        box.DcOn[1].ShouldBeFalse();
        box.UsbOn[4].ShouldBeFalse();
        box.WrittenCommands.ShouldContain(">C3#");
        box.WrittenCommands.ShouldContain(">C10#", "USB E is output 10, two digits");
        (await driver.GetValueAsync(Dc3, ct)).ShouldBe(0);
        (await driver.GetValueAsync(UsbE, ct)).ShouldBe(0);
        box.WrittenCommands.ShouldNotContain(c => c.StartsWith(">M") || c.StartsWith(">Z"), "the dew heaters' modes are the box's, never written");
    }

    [Fact(Timeout = 30_000)]
    public async Task ASetNoFrameShowsFailsRatherThanPretending()
    {
        var ct = TestContext.Current.CancellationToken;
        var box = new FakeGeminiPowerBoxSerialDevice();
        await using var driver = Connected(box, out var concrete);
        await driver.ConnectAsync(ct);
        box.IgnoreSets = true;
        concrete.ConfirmTimeout = TimeSpan.FromMilliseconds(300);

        await Should.ThrowAsync<IOException>(async () => await driver.SetValueAsync(Dc3, 0, ct));
    }

    [Fact(Timeout = 30_000)]
    public async Task AnOutputTheBoxKeepsOnAndAReadingCannotBeWritten()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var driver = Connected(new FakeGeminiPowerBoxSerialDevice(), out _);
        await driver.ConnectAsync(ct);

        await Should.ThrowAsync<InvalidOperationException>(async () => await driver.SetValueAsync(Dc1, 0, ct));
        await Should.ThrowAsync<InvalidOperationException>(async () => await driver.SetValueAsync(Voltage, 12, ct));
        await Should.ThrowAsync<ArgumentOutOfRangeException>(async () => await driver.SetValueAsync(Dew6, 150, ct));
    }

    [Fact(Timeout = 30_000)]
    public async Task ADewHeatersPowerIsSetOnlyInTheBoxsManualMode()
    {
        var ct = TestContext.Current.CancellationToken;
        var box = new FakeGeminiPowerBoxSerialDevice { Dew7Mode = 0 };
        await using var driver = Connected(box, out _);
        await driver.ConnectAsync(ct);

        await driver.SetValueAsync(Dew6, 40, ct);
        box.Dew6Power.ShouldBe(40);
        box.WrittenCommands.ShouldContain(">X40#");

        await Should.ThrowAsync<InvalidOperationException>(async () => await driver.SetValueAsync(Dew6 + 1, 40, ct));
        double.IsNaN(await driver.GetValueAsync(Dew6 + 1, ct)).ShouldBeTrue("in Auto the box does not say what power it chose");
    }

    [Fact(Timeout = 30_000)]
    public async Task AStatusTooOldIsNotAnsweredFrom()
    {
        var ct = TestContext.Current.CancellationToken;
        var external = new FakeExternal(output);
        var box = new FakeGeminiPowerBoxSerialDevice();
        await using var driver = Connected(box, external, out _);
        await driver.ConnectAsync(ct);

        external.TimeProvider.Advance(GeminiPowerBoxDriver.StatusStaleAfter + TimeSpan.FromSeconds(1));

        await Should.ThrowAsync<IOException>(async () => await driver.GetValueAsync(Voltage, ct));
    }

    [Fact(Timeout = 30_000)]
    public async Task APushedFrameIsTakenWithoutBeingAskedFor()
    {
        var ct = TestContext.Current.CancellationToken;
        var box = new FakeGeminiPowerBoxSerialDevice();
        await using var driver = Connected(box, out var concrete);
        await driver.ConnectAsync(ct);
        var before = concrete.LatestStatus.ShouldNotBeNull().Status;

        box.Voltage = 12.4;
        box.Push();
        while (concrete.LatestStatus?.Status.Voltage != 12.4)
        {
            await Task.Delay(10, ct);
        }

        before.Voltage.ShouldBe(12.9);
        (await driver.GetValueAsync(Voltage, ct)).ShouldBe(12.4);
    }

    [Fact(Timeout = 30_000)]
    public async Task ABoxThatNeverSendsStatusFailsTheConnect()
    {
        var ct = TestContext.Current.CancellationToken;
        var box = new FakeGeminiPowerBoxSerialDevice { NoStatus = true };
        await using var driver = Connected(box, out var concrete);
        concrete.ConfirmTimeout = TimeSpan.FromMilliseconds(300);

        await Should.ThrowAsync<InvalidOperationException>(async () => await driver.ConnectAsync(ct));
        box.IsOpen.ShouldBeFalse("a failed connect closes its port");
    }

    private ISwitchDriver Connected(FakeGeminiPowerBoxSerialDevice box, out GeminiPowerBoxDriver concrete)
        => Connected(box, new FakeExternal(output), out concrete);

    private ISwitchDriver Connected(FakeGeminiPowerBoxSerialDevice box, FakeExternal external, out GeminiPowerBoxDriver concrete)
    {
        var sp = external.BuildServiceProvider();
        new TestPowerBoxDevice(new Uri("switch://GeminiPowerBoxDevice/GeminiPowerBox_COM5?port=serial:COM5#Gemini power box"), box)
            .TryInstantiateDriver<ISwitchDriver>(sp, out var driver).ShouldBeTrue();
        concrete = driver.ShouldBeOfType<GeminiPowerBoxDriver>();
        return driver;
    }

    /// <summary>A power box device that connects to the in-memory fake instead of a real port.</summary>
    private sealed record TestPowerBoxDevice(Uri DeviceUri, ISerialConnection Conn) : GeminiPowerBoxDevice(DeviceUri)
    {
        public override ValueTask<ISerialConnection?> ConnectSerialDeviceAsync(
            IExternal external, ILogger logger, ITimeProvider timeProvider,
            int baud = GeminiPowerBoxProtocol.Baud, Encoding? encoding = null, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<ISerialConnection?>(Conn);
    }
}
