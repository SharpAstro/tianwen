using DotNext.Threading;
using Shouldly;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Connections;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Discovery;
using TianWen.Lib.Devices.Skywatcher;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Exercises <see cref="SkywatcherSerialProbeBase"/> against an in-memory scripted
/// serial connection. Verifies that a valid <c>:e1</c> response produces a
/// <see cref="SerialProbeMatch"/> whose URI carries the stable identity and the
/// probe's baud rate — no dependency on real hardware. The protocol parsing itself
/// is covered by <see cref="SkywatcherProtocolTests"/>; this test covers the glue
/// between the probe, the connection, and the URI.
/// </summary>
public class SkywatcherSerialProbeTests
{
    [Theory]
    [InlineData(SkywatcherProtocol.DEFAULT_USB_BAUD)]
    [InlineData(SkywatcherProtocol.DEFAULT_LEGACY_BAUD)]
    public async Task ValidFirmwareResponseProducesMatchWithPortAndBaudInUri(int baud)
    {
        // EQ6-R firmware blob: model 0x23 (Eq6R), minor 0x28 (40), major 0x04 — "=23", "2804" payload.
        // Skywatcher responds with '=' prefix + hex firmware + '\r'. See SkywatcherProtocol.TryParseResponse.
        var scriptedConn = new ScriptedSerialConnection(
            expectedWrite: SkywatcherProtocol.BuildCommand('e', '1'),
            reply: Encoding.ASCII.GetBytes("=232804\r"));

        var probe = baud == SkywatcherProtocol.DEFAULT_USB_BAUD
            ? (SkywatcherSerialProbeBase)new SkywatcherSerialProbeUsb()
            : new SkywatcherSerialProbeLegacy();

        var match = await probe.ProbeAsync("serial:COM5", scriptedConn, TestContext.Current.CancellationToken);

        match.ShouldNotBeNull();
        match.Port.ShouldBe("serial:COM5");
        // System.Uri lower-cases both scheme and host — SerialProbeService.IdentityMatches
        // uses OrdinalIgnoreCase, so this is expected and harmless.
        match.DeviceUri.Scheme.ShouldBe("mount");
        match.DeviceUri.Host.ShouldBe(nameof(SkywatcherDevice).ToLowerInvariant());
        match.DeviceUri.QueryValue(DeviceQueryKey.Port).ShouldBe("serial:COM5");
        match.DeviceUri.QueryValue(DeviceQueryKey.Baud).ShouldBe(baud.ToString());
    }

    [Fact]
    public async Task GarbageResponseReturnsNullMatch()
    {
        var scriptedConn = new ScriptedSerialConnection(
            expectedWrite: SkywatcherProtocol.BuildCommand('e', '1'),
            reply: Encoding.ASCII.GetBytes("NOT A SKYWATCHER\r"));

        var probe = new SkywatcherSerialProbeLegacy();
        var match = await probe.ProbeAsync("serial:COM5", scriptedConn, TestContext.Current.CancellationToken);

        match.ShouldBeNull();
    }

    [Fact]
    public void ProbeAdvertisesSkywatcherDeviceHost()
    {
        // Stage 1 verification relies on MatchesDeviceHosts advertising the right host.
        new SkywatcherSerialProbeUsb().MatchesDeviceHosts.ShouldContain(nameof(SkywatcherDevice));
        new SkywatcherSerialProbeLegacy().MatchesDeviceHosts.ShouldContain(nameof(SkywatcherDevice));
    }

    /// <summary>
    /// In-memory serial connection scripted with a single expected write + canned reply.
    /// Assertion on the written bytes catches probe regressions that change the command
    /// bytes; the reply is read back via the standard <c>TryReadTerminatedAsync</c> path.
    /// </summary>
    private sealed class ScriptedSerialConnection(byte[] expectedWrite, byte[] reply) : ISerialConnection
    {
        private readonly SemaphoreSlim _sem = new(1, 1);
        private readonly Queue<byte> _readBuffer = new(reply);

        public bool IsOpen { get; private set; } = true;
        public Encoding Encoding => Encoding.ASCII;
        public bool TryClose() { IsOpen = false; return true; }
        public void Dispose() => TryClose();

        public ValueTask<ResourceLock> WaitAsync(CancellationToken cancellationToken) => _sem.AcquireLockAsync(cancellationToken);

        public ValueTask<bool> TryWriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
        {
            data.Span.SequenceEqual(expectedWrite).ShouldBeTrue(
                $"probe wrote unexpected bytes: expected={Encoding.GetString(expectedWrite)}, got={Encoding.GetString(data.Span)}");
            return ValueTask.FromResult(true);
        }

        public ValueTask<string?> TryReadTerminatedAsync(ReadOnlyMemory<byte> terminators, CancellationToken cancellationToken)
        {
            var sb = new StringBuilder();
            while (_readBuffer.Count > 0)
            {
                var b = _readBuffer.Dequeue();
                foreach (var t in terminators.Span)
                {
                    if (b == t) return ValueTask.FromResult<string?>(sb.ToString());
                }
                sb.Append((char)b);
            }
            return ValueTask.FromResult<string?>(null);
        }

        public ValueTask<string?> TryReadExactlyAsync(int count, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<int> TryReadTerminatedRawAsync(Memory<byte> message, ReadOnlyMemory<byte> terminators, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<bool> TryReadExactlyRawAsync(Memory<byte> message, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
