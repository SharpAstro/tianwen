using Microsoft.Extensions.Logging;
using Shouldly;
using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Connections;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Gemini;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Every wait on a Gemini controller ends at a deadline on the injected clock: the FlatPanel's and the focuser's ack and
/// handshake. Each deadline is set to an hour, the controller takes every write and answers nothing, and the fake clock
/// is advanced by that hour ONCE per wait, once the wait has begun (the write is the signal, since the deadline is made
/// before it): a deadline on the wall clock would hold for the hour, and the test's own timeout would end it instead.
/// </summary>
[Collection("Device")]
public class GeminiDeadlineTests(ITestOutputHelper output)
{
    private static readonly TimeSpan Long = TimeSpan.FromHours(1);

    private static readonly Uri PanelUri = new("covercalibrator://GeminiDevice/Gemini_COM4?port=serial:COM4#Gemini FlatPanel Lite");
    private static readonly Uri FocuserUri = new("focuser://GeminiFocuserDevice/GeminiFocuser_COM9?port=serial:COM9#Gemini Focuser Pro");

    [Fact(Timeout = 30_000)]
    public async Task AFlatPanelThatNeverAcksALightCommandEndsTheWaitAtTheAckDeadline()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new FakeTimeProviderWrapper();
        var panel = new FakeGeminiFlatPanelSerialDevice { Silent = true };

        var send = GeminiFlatPanelProtocol.SetLightAsync(panel, on: true, clock, Long, ct).AsTask();
        await panel.WriteSeenAsync(ct);
        send.IsCompleted.ShouldBeFalse("nothing has acked and the clock has not moved");
        // The ack deadline is made after the write, so the write seen is not yet the deadline made: an advance taken on it
        // could pass the deadline before it exists, and then fire nothing.
        await clock.WaitForTimerDueAfterAsync(Long / 2, ct);
        clock.Advance(Long);

        await send;
        panel.WrittenCommands.ShouldContain(">L#");
    }

    [Fact(Timeout = 30_000)]
    public async Task AFocuserThatNeverAcksTheTempCompToggleEndsTheWaitAtTheAckDeadline()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new FakeTimeProviderWrapper();
        var focuser = new FakeGeminiFocuserSerialDevice { Silent = true };

        var send = GeminiFocuserProtocol.SetTempCompAsync(focuser, enabled: true, clock, Long, ct).AsTask();
        await focuser.WriteSeenAsync(ct);
        send.IsCompleted.ShouldBeFalse("nothing has acked and the clock has not moved");
        // As above: the ack deadline is made after the write.
        await clock.WaitForTimerDueAfterAsync(Long / 2, ct);
        clock.Advance(Long);

        await send;
    }

    [Fact(Timeout = 30_000)]
    public async Task AFlatPanelThatNeverAnswersItsIdentityFailsTheConnectAfterEveryAttemptsDeadline()
    {
        var ct = TestContext.Current.CancellationToken;
        var external = new FakeExternal(output);
        var panel = new FakeGeminiFlatPanelSerialDevice { Silent = true };
        var sp = external.BuildServiceProvider();
        new TestPanelDevice(PanelUri, panel).TryInstantiateDriver<ICoverDriver>(sp, out var driver).ShouldBeTrue();
        await using var _ = driver;
        driver.ShouldBeOfType<GeminiFlatPanelDriver>().HandshakeTimeout = Long;

        var connect = driver!.ConnectAsync(ct).AsTask();
        // One advance per attempt: each attempt's deadline is made when it starts, so one made after an advance never fires.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            await panel.WriteSeenAsync(ct);
            external.TimeProvider.Advance(Long);
        }

        await Should.ThrowAsync<InvalidOperationException>(async () => await connect);
        driver.Connected.ShouldBeFalse();
    }

    [Fact(Timeout = 30_000)]
    public async Task AFocuserThatNeverAnswersItsHandshakeFailsTheConnectAfterEveryAttemptsDeadline()
    {
        var ct = TestContext.Current.CancellationToken;
        var external = new FakeExternal(output);
        var focuser = new FakeGeminiFocuserSerialDevice { Silent = true };
        var sp = external.BuildServiceProvider();
        new TestFocuserDevice(FocuserUri, focuser).TryInstantiateDriver<IFocuserDriver>(sp, out var driver).ShouldBeTrue();
        await using var _ = driver;
        driver.ShouldBeOfType<GeminiFocuserDriver>().HandshakeTimeout = Long;

        var connect = driver!.ConnectAsync(ct).AsTask();
        for (var attempt = 0; attempt < 3; attempt++)
        {
            await focuser.WriteSeenAsync(ct);
            external.TimeProvider.Advance(Long);
        }

        await Should.ThrowAsync<InvalidOperationException>(async () => await connect);
        driver.Connected.ShouldBeFalse();
    }

    private sealed record TestPanelDevice(Uri DeviceUri, ISerialConnection Conn) : GeminiDevice(DeviceUri)
    {
        public override ValueTask<ISerialConnection?> ConnectSerialDeviceAsync(
            IExternal external, ILogger logger, ITimeProvider timeProvider,
            int baud = GeminiFlatPanelProtocol.Baud, Encoding? encoding = null, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<ISerialConnection?>(Conn);
    }

    private sealed record TestFocuserDevice(Uri DeviceUri, ISerialConnection Conn) : GeminiFocuserDevice(DeviceUri)
    {
        public override ValueTask<ISerialConnection?> ConnectSerialDeviceAsync(
            IExternal external, ILogger logger, ITimeProvider timeProvider,
            int baud = GeminiFocuserProtocol.Baud, Encoding? encoding = null, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<ISerialConnection?>(Conn);
    }
}
