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
    [InlineData("1111D111111U1A1T1A1M1B1M100C100C 24.81S 26.87T 56.00H 17.35D12.9V 0.16C  2.06")] // the last tag missing
    [InlineData("1111D111111U1A1T1A1M1B1M100C100C 24.81S 26.87T 56.00H 17.35D12.9V 0.16C  2.06P 1.00P")] // a field too many
    public void AFrameOfTheWrongShapeIsNoStatus(string payload) => GeminiPowerBoxProtocol.ParseStatus(payload).ShouldBeNull();

    [Theory]
    [InlineData("*HGeminiPowerBoxPlusAdv3", 'H', "GeminiPowerBoxPlusAdv3")]
    [InlineData("*V309#", 'V', "309")]
    public void AFrameIsItsLetterAndPayload(string raw, char letter, string payload)
        => GeminiPowerBoxProtocol.ParseFrame(raw).ShouldBe((letter, payload));

    /// <summary>
    /// What a read that joined a status frame midway returns: its tail, up to the '#' that ends every Serial.Lib read, with
    /// no '*'. It is no frame, and a query reads past it.
    /// </summary>
    [Fact]
    public void AFramesTailIsNoFrame() => GeminiPowerBoxProtocol.ParseFrame(" 0.16C  2.06P").ShouldBeNull();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TheProbeMatchesTheBoxAndReadsPastAStatusFrameAlreadyComing(bool tailOnly)
    {
        // Measured: the box was still pushing from the connection before when the probe opened the port, and the probe
        // service discards the input buffer after its warm-up, which can leave only a frame's tail.
        var box = new FakeGeminiPowerBoxSerialDevice();
        if (tailOnly)
        {
            box.EnqueueRaw(" 56.00H 17.35D12.9V 0.16C  2.06P#");
        }
        else
        {
            box.Push();
        }

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
        await using var driver = Connected(out _, box);
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
        await using var driver = Connected(out _, box);
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
        await using var driver = Connected(out _, box);
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
        await using var driver = Connected(out var concrete, box);
        await driver.ConnectAsync(ct);
        box.IgnoreSets = true;
        concrete.ConfirmTimeout = TimeSpan.FromMilliseconds(300);

        await Should.ThrowAsync<IOException>(async () => await driver.SetValueAsync(Dc3, 0, ct));
    }

    [Fact(Timeout = 30_000)]
    public async Task AnOutputTheBoxKeepsOnAndAReadingCannotBeWritten()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var driver = Connected(out _, new FakeGeminiPowerBoxSerialDevice());
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
        await using var driver = Connected(out _, box);
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
        await using var driver = Connected(external, out var concrete, box);
        // Only the fake clock moves here: the reader's real-clock silence must not ask for a fresh frame mid-test.
        concrete.RequestAgainAfter = TimeSpan.FromHours(1);
        await driver.ConnectAsync(ct);

        external.TimeProvider.Advance(GeminiPowerBoxDriver.StatusStaleAfter + TimeSpan.FromSeconds(1));

        await Should.ThrowAsync<IOException>(async () => await driver.GetValueAsync(Voltage, ct));
    }

    [Fact(Timeout = 30_000)]
    public async Task APushedFrameIsTakenWithoutBeingAskedFor()
    {
        var ct = TestContext.Current.CancellationToken;
        var box = new FakeGeminiPowerBoxSerialDevice();
        await using var driver = Connected(out var concrete, box);
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
    public async Task ABoxThatNeverSendsStatusFailsTheConnectAndLeavesNoReader()
    {
        var ct = TestContext.Current.CancellationToken;
        var box = new FakeGeminiPowerBoxSerialDevice { NoStatus = true };
        await using var driver = Connected(out var concrete, box);
        concrete.ConfirmTimeout = TimeSpan.FromMilliseconds(300);
        // A reader left running would ask for status again within this, on the closed port.
        concrete.RequestAgainAfter = TimeSpan.FromMilliseconds(50);

        await Should.ThrowAsync<InvalidOperationException>(async () => await driver.ConnectAsync(ct));
        box.IsOpen.ShouldBeFalse("a failed connect closes its port");
        await Task.Delay(TimeSpan.FromMilliseconds(400), ct);
        box.WritesAfterClose.ShouldBe(0, "no reader outlives a failed connect");
        driver.Connected.ShouldBeFalse();
    }

    [Fact(Timeout = 30_000)]
    public async Task APortThatGoesUnderAConnectionEndsIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var box = new FakeGeminiPowerBoxSerialDevice();
        await using var driver = Connected(out var concrete, box);
        await driver.ConnectAsync(ct);
        var reader = concrete.Reader.ShouldNotBeNull();

        box.Unplug();

        // The reader ends once it has called the connection lost.
        await reader.WaitAsync(ct);
        driver.Connected.ShouldBeFalse();
        await Should.ThrowAsync<InvalidOperationException>(async () => await driver.GetValueAsync(Voltage, ct));
        await Should.ThrowAsync<InvalidOperationException>(async () => await driver.SetValueAsync(Dc3, 0, ct));
    }

    [Fact(Timeout = 30_000)]
    public async Task AReconnectWithoutADisconnectStartsCleanOnTheNewPort()
    {
        var ct = TestContext.Current.CancellationToken;
        var first = new FakeGeminiPowerBoxSerialDevice();
        var silent = new FakeGeminiPowerBoxSerialDevice { NoStatus = true };
        var third = new FakeGeminiPowerBoxSerialDevice { Voltage = 12.1 };
        await using var driver = Connected(out var concrete, first, silent, third);
        concrete.ConfirmTimeout = TimeSpan.FromMilliseconds(300);
        await driver.ConnectAsync(ct);
        first.Unplug();
        while (driver.Connected)
        {
            await Task.Delay(10, ct);
        }

        // The old connection's frame must not count as the new port's first.
        await Should.ThrowAsync<InvalidOperationException>(async () => await driver.ConnectAsync(ct));

        await driver.ConnectAsync(ct);
        (await driver.GetValueAsync(Voltage, ct)).ShouldBe(12.1);
        first.WritesAfterClose.ShouldBe(0, "the first connection's reader ended with its port");
        silent.WritesAfterClose.ShouldBe(0);
    }

    private ISwitchDriver Connected(out GeminiPowerBoxDriver concrete, params FakeGeminiPowerBoxSerialDevice[] ports)
        => Connected(new FakeExternal(output), out concrete, ports);

    private ISwitchDriver Connected(FakeExternal external, out GeminiPowerBoxDriver concrete, params FakeGeminiPowerBoxSerialDevice[] ports)
    {
        var sp = external.BuildServiceProvider();
        new TestPowerBoxDevice(new Uri("switch://GeminiPowerBoxDevice/GeminiPowerBox_COM5?port=serial:COM5#Gemini power box"), new PortQueue(ports))
            .TryInstantiateDriver<ISwitchDriver>(sp, out var driver).ShouldBeTrue();
        concrete = driver.ShouldBeOfType<GeminiPowerBoxDriver>();
        return driver;
    }

    /// <summary>The ports successive connects open, the last one again once the list runs out.</summary>
    private sealed class PortQueue(FakeGeminiPowerBoxSerialDevice[] ports)
    {
        private int _next;

        public ISerialConnection Open() => ports[Math.Min(Interlocked.Increment(ref _next) - 1, ports.Length - 1)];
    }

    /// <summary>A power box device that connects to the in-memory fakes instead of a real port.</summary>
    private sealed record TestPowerBoxDevice(Uri DeviceUri, PortQueue Ports) : GeminiPowerBoxDevice(DeviceUri)
    {
        public override ValueTask<ISerialConnection?> ConnectSerialDeviceAsync(
            IExternal external, ILogger logger, ITimeProvider timeProvider,
            int baud = GeminiPowerBoxProtocol.Baud, Encoding? encoding = null, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<ISerialConnection?>(Ports.Open());
    }
}
