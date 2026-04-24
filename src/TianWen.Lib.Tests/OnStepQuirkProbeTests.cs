using Meziantou.Extensions.Logging.Xunit.v3;
using Microsoft.Extensions.Logging;
using Shouldly;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Connections;
using TianWen.Lib.Devices.Discovery;
using TianWen.Lib.Devices.Meade;
using TianWen.Lib.Devices.OnStep;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// End-to-end probe test for the OnStep first-query quirk documented in INDI's
/// <c>lx200_OnStep.cpp</c>: the very first LX200 "get product" query returns a bare
/// <c>'0'</c> with no <c>'#'</c> terminator. Both probes in the 9600-baud group
/// send <c>:GVP#</c>, and framed-protocols-first sort ordering puts Meade ahead
/// of OnStep — so Meade is the probe that receives the quirky <c>'0'</c>. Meade's
/// <c>TryReadTerminatedAsync</c> never sees a <c>'#'</c>, times out, its local
/// message buffer is discarded on the way out, and — crucially — the stream is
/// now clean for the OnStep probe which runs next on the same shared handle and
/// sends its own <c>:GVP#</c>. That second query gets the proper <c>"On-Step#"</c>
/// response, so OnStep matches without ever needing to "know" about the quirk.
///
/// This models the pure synchronous case (device responds to :GVP# with "0"
/// immediately, then goes silent until the next query). The post-probe
/// <see cref="ISerialConnection.DiscardInBuffer"/> in <c>RunSingleProbeAsync</c>
/// runs too, but in this scenario it has nothing to drain — Meade already
/// consumed the stray byte. The drain only pays off in the orthogonal case of
/// late-arriving bytes, which this test does not cover.
///
/// Deterministic time: the <see cref="FakeTimeProviderWrapper"/> is plumbed into
/// <see cref="SerialProbeService"/>, so the per-probe budget CTS fires only when
/// the test pump advances fake time.
/// </summary>
public class OnStepQuirkProbeTests(ITestOutputHelper output)
{
    [Fact]
    public async Task FirstGvpReturnsUnterminatedZero_MeadeAbsorbsIt_OnStepMatchesOnSecondQuery()
    {
        var timeProvider = new FakeTimeProviderWrapper();
        var stub = new QuirkyOnStepStubConnection(Encoding.ASCII);
        var external = new SingleStubExternal(timeProvider, "serial:COM5", stub);

        var factory = LoggerFactory.Create(b => b.AddProvider(new XUnitLoggerProvider(output, false)));
        var logger = factory.CreateLogger<SerialProbeService>();

        var service = new SerialProbeService(
            timeProvider,
            external,
            logger,
            [new MeadeSerialProbe(), new OnStepSerialProbe()],
            pinnedPortsProvider: null,
            // pass 1 only — the test is about the single-pass handoff from
            // Meade (absorbs the quirk) to OnStep (clean :GVP#).
            passBudgetMultipliers: [1.0]);

        // Real-time safety cap: without this, a hung probe hangs the whole suite.
        using var realTimeCts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        realTimeCts.CancelAfter(TimeSpan.FromSeconds(20));

        var probeTask = service.ProbeAllAsync(realTimeCts.Token);

        // Cooperative time pump: advance fake time in 50ms steps, yielding real
        // time between steps so Parallel.ForEachAsync's pool task can observe
        // cancellation and progress the reads. Meade needs its full 500ms budget
        // to time out (the read hangs after consuming the '0'); OnStep finishes
        // synchronously from its enqueued response. Budget covers both with slack.
        var pumped = TimeSpan.Zero;
        var maxPump = TimeSpan.FromSeconds(5);
        while (!probeTask.IsCompleted && pumped < maxPump)
        {
            timeProvider.Advance(TimeSpan.FromMilliseconds(50));
            pumped += TimeSpan.FromMilliseconds(50);
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }

        probeTask.IsCompleted.ShouldBeTrue(
            $"probe did not complete within {maxPump} fake-time; pumped={pumped}");
        await probeTask;

        stub.GvpWriteCount.ShouldBe(2,
            "both Meade and OnStep send :GVP# on the shared handle — exactly once each");

        service.ResultsFor("Meade").ShouldBeEmpty(
            "Meade's :GVP# got a bare '0' with no '#' → read times out, no match (as designed)");

        var onStep = service.ResultsFor("OnStep").ShouldHaveSingleItem(
            "OnStep's :GVP# ran after Meade on the same clean stream, got 'On-Step#' → match");
        onStep.DeviceUri.Host.ShouldBe("onstepdevice");
    }

    // ---- Stub wiring -------------------------------------------------------

    /// <summary>
    /// <see cref="FakeExternal"/> subclass that hands out a single pre-built
    /// <see cref="ISerialConnection"/> stub, so the test controls exactly what the
    /// probes see on the wire.
    /// </summary>
    private sealed class SingleStubExternal : FakeExternal
    {
        private readonly string _port;
        private readonly ISerialConnection _conn;

        public SingleStubExternal(FakeTimeProviderWrapper timeProvider, string port, ISerialConnection conn)
            : base(NullTestOutputHelper.Instance, timeProvider)
        {
            _port = port;
            _conn = conn;
        }

        public override IReadOnlyList<string> EnumerateAvailableSerialPorts(ResourceLock _) => [_port];

        public override ValueTask<ISerialConnection> OpenSerialDeviceAsync(
            string address, int baud, Encoding encoding, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(_conn);
    }

    private sealed class NullTestOutputHelper : ITestOutputHelper
    {
        public static readonly NullTestOutputHelper Instance = new();
        public string Output => string.Empty;
        public void Write(string message) { }
        public void Write(string format, params object[] args) { }
        public void WriteLine(string message) { }
        public void WriteLine(string format, params object[] args) { }
    }

    /// <summary>
    /// Simulates an OnStep-like LX200 device with the INDI-documented first-query
    /// quirk. The stub answers <c>:GVP#</c>, <c>:GVN#</c>, and the four site-slot
    /// queries (<c>:GM#</c>..<c>:GO#</c>) well enough to satisfy
    /// <see cref="OnStepDeviceSource.TryGetMountInfo"/>.
    /// <para>
    /// Quirk model: on the FIRST <c>:GVP#</c> the device responds synchronously
    /// with the single byte <c>'0'</c> and no <c>'#'</c> terminator. The caller
    /// reads the <c>'0'</c>, finds no terminator, blocks for more bytes, and
    /// eventually times out. On the SECOND <c>:GVP#</c> (OnStep's own query on
    /// the same handle) the device responds normally with <c>"On-Step#"</c>.
    /// </para>
    /// </summary>
    private sealed class QuirkyOnStepStubConnection : ISerialConnection
    {
        private readonly Encoding _encoding;
        private readonly Queue<byte> _rxBuffer = new();
        private readonly SemaphoreSlim _sem = new(1, 1);
        private readonly object _rxLock = new();
        private int _gvpWriteCount;

        public QuirkyOnStepStubConnection(Encoding encoding)
        {
            _encoding = encoding;
        }

        public int GvpWriteCount => Volatile.Read(ref _gvpWriteCount);
        public int DiscardCalls { get; private set; }
        public List<int> DrainedByteCounts { get; } = [];

        public bool IsOpen { get; private set; } = true;
        public Encoding Encoding => _encoding;
        public bool LogVerbose { get; set; }
        public string? VerboseTag { get; set; }

        public ValueTask<ResourceLock> WaitAsync(CancellationToken cancellationToken)
            => _sem.AcquireLockAsync(cancellationToken);

        public bool TryClose()
        {
            IsOpen = false;
            return true;
        }

        public void Dispose() => TryClose();

        public ValueTask<bool> TryWriteAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken)
        {
            var cmd = _encoding.GetString(message.Span);

            if (cmd == ":GVP#")
            {
                var n = Interlocked.Increment(ref _gvpWriteCount);
                if (n == 1)
                {
                    // Quirk: first query returns a bare '0' with no '#'.
                    // The caller will read the '0', fail to find a terminator,
                    // and eventually time out on the subsequent ReadAtLeast call.
                    EnqueueAscii("0");
                }
                else
                {
                    // Subsequent queries behave normally.
                    EnqueueAscii("On-Step#");
                }
            }
            else if (cmd == ":GVN#")
            {
                EnqueueAscii("5.8#");
            }
            else if (cmd is ":GM#" or ":GL#" or ":GN#" or ":GO#")
            {
                // Every site slot reports "unused" — exercises the branch where
                // TryGetMountInfo writes a fresh UUID into the first empty slot.
                EnqueueAscii("<AN UNUSED SITE>#");
            }
            else if (cmd.StartsWith(":S", StringComparison.Ordinal) && cmd.EndsWith("#"))
            {
                // Set-site ack — one byte '1' for success.
                EnqueueAscii("1");
            }
            // Unknown commands are silently dropped — if a probe adds a new
            // exchange the test will time out on that read and fail loudly.

            return ValueTask.FromResult(true);
        }

        public async ValueTask<int> TryReadTerminatedRawAsync(
            Memory<byte> message, ReadOnlyMemory<byte> terminators, CancellationToken cancellationToken)
        {
            try
            {
                var read = 0;
                while (read < message.Length)
                {
                    if (TryDequeue() is byte b)
                    {
                        if (terminators.Span.IndexOf(b) >= 0)
                        {
                            // Match SerialConnectionBase semantics: terminator is
                            // logically "in" the buffer but the returned length
                            // excludes it. Callers read [0..read) for the payload.
                            return read;
                        }
                        message.Span[read++] = b;
                        continue;
                    }
                    // Buffer empty — wait for either a late enqueue from another
                    // thread (there are none in this test) or cancellation. 1ms
                    // real-time polling is fine because cancellation on the fake
                    // clock still fires through the linked ct.
                    await Task.Delay(1, cancellationToken);
                }
                return -1;
            }
            catch (OperationCanceledException)
            {
                return -1;
            }
        }

        public async ValueTask<bool> TryReadExactlyRawAsync(Memory<byte> message, CancellationToken cancellationToken)
        {
            try
            {
                var read = 0;
                while (read < message.Length)
                {
                    if (TryDequeue() is byte b)
                    {
                        message.Span[read++] = b;
                        continue;
                    }
                    await Task.Delay(1, cancellationToken);
                }
                return true;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        public async ValueTask<string?> TryReadTerminatedAsync(ReadOnlyMemory<byte> terminators, CancellationToken cancellationToken)
        {
            var buf = new byte[256];
            var len = await TryReadTerminatedRawAsync(buf.AsMemory(), terminators, cancellationToken);
            return len >= 0 ? _encoding.GetString(buf.AsSpan(0, len)) : null;
        }

        public async ValueTask<string?> TryReadExactlyAsync(int count, CancellationToken cancellationToken)
        {
            var buf = new byte[count];
            return await TryReadExactlyRawAsync(buf.AsMemory(), cancellationToken)
                ? _encoding.GetString(buf)
                : null;
        }

        public void DiscardInBuffer()
        {
            DiscardCalls++;
            lock (_rxLock)
            {
                DrainedByteCounts.Add(_rxBuffer.Count);
                _rxBuffer.Clear();
            }
        }

        private void EnqueueAscii(string s)
        {
            lock (_rxLock)
            {
                foreach (var ch in s)
                {
                    _rxBuffer.Enqueue((byte)ch);
                }
            }
        }

        private byte? TryDequeue()
        {
            lock (_rxLock)
            {
                return _rxBuffer.Count > 0 ? _rxBuffer.Dequeue() : null;
            }
        }
    }
}
