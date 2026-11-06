using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Meziantou.Extensions.Logging.Xunit.v3;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shouldly;
using TianWen.Lib.Connections;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.IOptron;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The SkyGuider Pro's replies as the real mount gave them on the bench of 2026-11-05 (firmware 170518, over its own USB
/// socket), replayed against our probe and driver: a move start acknowledged twice, the mount then answering each command
/// with the reply it was HOLDING, across a reconnect too, and the RA servo still reporting motion after a stop.
/// </summary>
/// <remarks>
/// Each behaviour of <see cref="ScriptedSgp"/> is a line of the bench logs (<c>docs/architecture/sgp-protocol.md</c>,
/// "Replies out of step"). Before <see cref="SgpWire"/>, a one-reply-per-command reader failed every test here: the probe
/// read a held status as the firmware, the driver read a stop's acknowledgement as a status and called a moving mount
/// "not tracking". The live check is <c>SgpMountHardwareProbe</c>.
/// </remarks>
public class SgpOutOfStepReplayTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(":MRSVE#", ":RMRVE")]
    [InlineData(":MGAS#", ":HRAS")]
    [InlineData(":MSMR2#", ":HRMR")]
    [InlineData(":MSMS7#", ":HRMS")]
    [InlineData(":MGGR#", ":HRGR")]
    [InlineData(":MSGR5050#", ":HRGR")]
    [InlineData(":MGCS#", ":HRCS")]
    [InlineData(":MSCA10030005002#", ":HRCA")]
    [InlineData(":MSHE0#", ":HRHE")]
    [InlineData(":MSTR3#", ":HRTR")]
    public void EveryCommandKnowsTheReplyThatAnswersIt(string command, string prefix)
        => SgpWire.ReplyPrefixOf(command).ShouldBe(prefix);

    [Fact(Timeout = 30_000)]
    public async Task TheProbeFindsAMountAPreviousSessionLeftOutOfStep()
    {
        var mount = new ScriptedSgp { Lagging = true, Held = ScriptedSgp.StatusAtRest };
        var conn = mount.Connect();

        var match = await new IOptronSerialProbe().ProbeAsync("serial:COM99", conn, TestContext.Current.CancellationToken);

        match.ShouldNotBeNull("the held status is not the firmware's answer; the probe asks again");
        match.DeviceUri.ToString().ShouldContain("170518");
        output.WriteLine(string.Join(Environment.NewLine, mount.Traffic));
    }

    [Fact(Timeout = 30_000)]
    public async Task TheDriverFollowsAMoveAndAStopThroughRepliesOutOfStep()
    {
        var ct = TestContext.Current.CancellationToken;
        var mount = new ScriptedSgp();
        await using var sp = Services(mount);
        var driver = new SgpMountDriver(Device, sp);
        await driver.ConnectAsync(ct);
        try
        {
            driver.DriverInfo.ShouldNotBeNull().ShouldContain("170518");
            (await driver.IsSlewingAsync(ct)).ShouldBeFalse("at rest after connect");

            await driver.MoveAxisAsync(TelescopeAxis.Primary, -1.0, ct);
            for (var poll = 0; poll < 3; poll++)
            {
                (await driver.IsSlewingAsync(ct)).ShouldBeTrue($"moving, poll {poll}, though the move's second acknowledgement is on the wire");
                (await driver.IsTrackingAsync(ct)).ShouldBeTrue("an SGP always tracks");
            }

            await driver.MoveAxisAsync(TelescopeAxis.Primary, 0, ct);
            (await driver.IsSlewingAsync(ct)).ShouldBeTrue("the servo is still winding down");
            var polls = 0;
            while (await driver.IsSlewingAsync(ct))
            {
                (++polls).ShouldBeLessThan(5, "the servo comes to rest");
            }

            // A query of another kind after all that gets ITS reply, not a held status.
            var snap = (await driver.GetCameraSnapSettingsAsync(ct)).ShouldNotBeNull();
            snap.ShotCount.ShouldBe(100, "the mount's own camera settings (:HRCS0100120051...), not a default");
            (await driver.GetTrackingSpeedAsync(ct)).ShouldBe(TrackingSpeed.Sidereal);
        }
        finally
        {
            await driver.DisconnectAsync(CancellationToken.None);
            output.WriteLine(string.Join(Environment.NewLine, mount.Traffic));
        }

        mount.Lagging.ShouldBeTrue("the scripted mount really was out of step, or this test proved nothing");
    }

    [Fact(Timeout = 30_000)]
    public async Task ADriverConnectsToAMountItsPredecessorLeftOutOfStep()
    {
        var ct = TestContext.Current.CancellationToken;
        var mount = new ScriptedSgp { Lagging = true, Held = ":HRMR0#" };
        await using var sp = Services(mount);
        var driver = new SgpMountDriver(Device, sp);
        await driver.ConnectAsync(ct);
        try
        {
            driver.DriverInfo.ShouldNotBeNull().ShouldContain("170518", customMessage: "the firmware is read past the held stop acknowledgement");
            (await driver.IsSlewingAsync(ct)).ShouldBeFalse();
        }
        finally
        {
            await driver.DisconnectAsync(CancellationToken.None);
        }
    }

    private static IOptronDevice Device => new(DeviceType.Mount, "SkyGuider-Pro_test", "Scripted SkyGuider Pro", "serial:COM99");

    private ServiceProvider Services(ScriptedSgp mount) => new ServiceCollection()
        .AddSingleton<IExternal>(new ScriptedExternal(output, mount))
        .AddSingleton<ITimeProvider>(SystemTimeProvider.Instance)
        .AddLogging(logging => logging.AddProvider(new XUnitLoggerProvider(output)).SetMinimumLevel(LogLevel.Debug))
        .BuildServiceProvider();

    private sealed class ScriptedExternal(ITestOutputHelper output, ScriptedSgp mount) : FakeExternal(output)
    {
        public override ValueTask<ISerialConnection> OpenSerialDeviceAsync(string address, int baud, Encoding encoding, bool assertControlLines = false, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<ISerialConnection>(mount.Connect());
    }

    /// <summary>
    /// The mount, which outlives a connection (its held reply survives a port close, as on the bench). In step, a command
    /// gets its own reply. A move start is acknowledged at once AND again with the next command, and from then on the mount
    /// is out of step: each command gets the reply the mount was holding, and its own is held. An unknown command gets
    /// nothing. The status's first field is 2 while the axis moves and for <see cref="WindDownReads"/> status reads after a
    /// stop.
    /// </summary>
    private sealed class ScriptedSgp
    {
        internal const string StatusAtRest = ":HRAS013900105#";
        private const int WindDownReads = 2;

        private bool _moving;
        private int _windDown;
        private string? _extra;

        internal bool Lagging { get; set; }
        internal string? Held { get; set; }
        internal List<string> Traffic { get; } = [];

        internal Wire Connect() => new(this);

        internal string Receive(string command)
        {
            if (ReplyTo(command) is not { } reply)
            {
                Traffic.Add($">> {command}  << (nothing)");
                return "";
            }

            var sent = new StringBuilder();
            if (_extra is { } extra)
            {
                sent.Append(extra);
                _extra = null;
            }

            if (Lagging)
            {
                sent.Append(Held);
                Held = reply;
            }
            else
            {
                sent.Append(reply);
            }

            if (command is ":MSMR0#" or ":MSMR1#")
            {
                _extra = ":HRMR0#";
                Lagging = true;
            }

            Traffic.Add($">> {command}  << {sent}");
            return sent.ToString();
        }

        private string? ReplyTo(string command)
        {
            switch (command)
            {
                case ":MRSVE#":
                    return ":RMRVE12170518#";
                case ":MGAS#":
                    var moving = _moving || _windDown > 0;
                    if (!_moving && _windDown > 0)
                    {
                        _windDown--;
                    }
                    return moving ? ":HRAS023900105#" : StatusAtRest;
                case ":MSMR0#" or ":MSMR1#":
                    _moving = true;
                    return ":HRMR0#";
                case ":MSMR2#":
                    _moving = false;
                    _windDown = WindDownReads;
                    return ":HRMR0#";
                case ":MGCS#":
                    return ":HRCS0100120051000000000000#";
                case ":MGGR#":
                    return ":HRGR5050#";
                default:
                    return command.StartsWith(":MSMS", StringComparison.Ordinal) ? ":HRMS0#"
                        : command.StartsWith(":MSGR", StringComparison.Ordinal) ? ":HRGR0#"
                        : command.StartsWith(":MSTR", StringComparison.Ordinal) ? ":HRTR0#"
                        : command.StartsWith(":MSHE", StringComparison.Ordinal) ? ":HRHE0#"
                        : command.StartsWith(":MSCA", StringComparison.Ordinal) ? ":HRCA0#"
                        : null;
            }
        }
    }

    /// <summary>One connection to <see cref="ScriptedSgp"/>: what the mount sent waits here until read, or discarded.</summary>
    private sealed class Wire(ScriptedSgp mount) : ISerialConnection
    {
        private readonly SemaphoreSlim _semaphore = new(1, 1);
        private readonly StringBuilder _received = new();

        public bool IsOpen { get; private set; } = true;
        public TimeSpan ReplyTimeout { get; set; } = ISerialConnection.DefaultReplyTimeout;
        public Encoding Encoding => Encoding.ASCII;

        public ValueTask<ResourceLock> WaitAsync(CancellationToken cancellationToken) => _semaphore.AcquireLockAsync(cancellationToken);

        public void DiscardInBuffer() => _received.Clear();

        public ValueTask<bool> TryWriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
        {
            _received.Append(mount.Receive(Encoding.GetString(data.Span)));
            return ValueTask.FromResult(true);
        }

        /// <summary>The next reply without its terminator, or null at once when none is waiting (a read that would time out).</summary>
        public ValueTask<string?> TryReadTerminatedAsync(ReadOnlyMemory<byte> terminators, CancellationToken cancellationToken)
        {
            var text = _received.ToString();
            var end = text.IndexOf('#');
            if (end < 0)
            {
                return ValueTask.FromResult<string?>(null);
            }

            _received.Remove(0, end + 1);
            return ValueTask.FromResult<string?>(text[..end]);
        }

        public ValueTask<string?> TryReadExactlyAsync(int count, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<int> TryReadTerminatedRawAsync(Memory<byte> message, ReadOnlyMemory<byte> terminators, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<bool> TryReadExactlyRawAsync(Memory<byte> message, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<bool> TryCloseAsync()
        {
            IsOpen = false;
            return ValueTask.FromResult(true);
        }

        public ValueTask DisposeAsync()
        {
            IsOpen = false;
            return ValueTask.CompletedTask;
        }
    }
}
