using DotNext.Threading;
using Meziantou.Extensions.Logging.Xunit.v3;
using Microsoft.Extensions.Logging;
using Shouldly;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Connections;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Discovery;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Unit tests for <see cref="SerialProbeService"/>. Scenarios focus on scheduling
/// (per-port × per-baud), result publishing, timeout handling, and exclusivity —
/// not on real wire protocols (those are covered by source-specific probe tests).
/// </summary>
public class SerialProbeServiceTests(ITestOutputHelper output)
{
    [Fact]
    public async Task WithNoProbesRegistersIsANoOp()
    {
        var external = new ProbeTestExternal();
        var service = BuildService(external, output);

        await service.ProbeAllAsync(TestContext.Current.CancellationToken);

        external.OpenCalls.ShouldBeEmpty("no probes → service must never touch any port");
    }

    [Fact]
    public async Task WithOneMatchingProbePublishesResultAndOpensPortOnce()
    {
        var external = new ProbeTestExternal { Ports = ["serial:COM5"] };
        var probe = StubProbe.Sync("Skywatcher", baud: 115200, match: (port, _) => new SerialProbeMatch(port, new Uri("Mount://Skywatcher/cool")));
        var service = BuildService(external, output, probe);

        await service.ProbeAllAsync(TestContext.Current.CancellationToken);

        service.ResultsFor("Skywatcher").ShouldHaveSingleItem().DeviceUri.ShouldBe(new Uri("Mount://Skywatcher/cool"));
        external.OpenCalls.Count.ShouldBe(1, "one probe, one port → one open");
    }

    [Fact]
    public async Task WithTwoProbesSameBaudPortIsOpenedOnce()
    {
        var external = new ProbeTestExternal { Ports = ["serial:COM5"] };
        var a = StubProbe.Sync("OnStep", baud: 9600, match: (_, _) => null);
        var b = StubProbe.Sync("Meade", baud: 9600, match: (_, _) => null);
        var service = BuildService(external, output, a, b);

        await service.ProbeAllAsync(TestContext.Current.CancellationToken);

        external.OpenCalls.Count.ShouldBe(1, "two probes sharing baud 9600 must share one open handle");
        external.OpenCalls.First().Baud.ShouldBe(9600);
    }

    [Fact]
    public async Task WithTwoProbesDifferentBaudPortIsOpenedPerBaud()
    {
        var external = new ProbeTestExternal { Ports = ["serial:COM5"] };
        var a = StubProbe.Sync("Skywatcher115k", baud: 115200, match: (_, _) => null);
        var b = StubProbe.Sync("OnStep", baud: 9600, match: (_, _) => null);
        var service = BuildService(external, output, a, b);

        await service.ProbeAllAsync(TestContext.Current.CancellationToken);

        external.OpenCalls.Count.ShouldBe(2, "different bauds → port opened once per baud");
    }

    [Fact]
    public async Task WithProbeThatTimesOutLogsTimeoutAndReturnsNoMatch()
    {
        var external = new ProbeTestExternal { Ports = ["serial:COM5"] };
        var probe = new StubProbe("Slow", baud: 9600, budget: TimeSpan.FromMilliseconds(50),
            match: async (_, ct) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
                return (SerialProbeMatch?)null;
            });
        var service = BuildService(external, output, probe);

        await service.ProbeAllAsync(TestContext.Current.CancellationToken);

        service.ResultsFor("Slow").ShouldBeEmpty();
    }

    [Fact]
    public async Task WithRetryBudgetProbeIsRetriedOnTimeout()
    {
        var external = new ProbeTestExternal { Ports = ["serial:COM5"] };
        var attempts = 0;
        var probe = new StubProbe("Retrier", baud: 9600, budget: TimeSpan.FromMilliseconds(50), maxAttempts: 2,
            match: async (port, ct) =>
            {
                var attempt = Interlocked.Increment(ref attempts);
                if (attempt == 1)
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), ct);
                    return (SerialProbeMatch?)null;
                }
                return new SerialProbeMatch(port, new Uri("Focuser://FakeDevice/retried"));
            });
        var service = BuildService(external, output, probe);

        await service.ProbeAllAsync(TestContext.Current.CancellationToken);

        attempts.ShouldBe(2);
        service.ResultsFor("Retrier").ShouldHaveSingleItem();
    }

    [Fact]
    public async Task WithExclusiveBaudSiblingsInSameGroupAreSkipped()
    {
        var external = new ProbeTestExternal { Ports = ["serial:COM5"] };
        var siblingRan = 0;
        var exclusive = StubProbe.Sync("Exclusive", baud: 9600,
            match: (port, _) => new SerialProbeMatch(port, new Uri("Mount://Excl/x")),
            exclusivity: ProbeExclusivity.ExclusiveBaud);
        var sibling = StubProbe.Sync("Sibling", baud: 9600,
            match: (_, _) => { Interlocked.Increment(ref siblingRan); return null; });
        var service = BuildService(external, output, exclusive, sibling);

        await service.ProbeAllAsync(TestContext.Current.CancellationToken);

        siblingRan.ShouldBe(0, "exclusive probe in baud group must skip siblings");
        service.ResultsFor("Exclusive").ShouldHaveSingleItem();
    }

    [Fact]
    public async Task WithMultiplePortsEachIsProbedIndependently()
    {
        var external = new ProbeTestExternal { Ports = ["serial:COM5", "serial:COM6", "serial:COM7"] };
        var probe = StubProbe.Sync("AllMatch", baud: 9600,
            match: (port, _) => new SerialProbeMatch(port, new Uri($"Mount://FakeDevice/{port.Replace(':', '_')}")));
        var service = BuildService(external, output, probe);

        await service.ProbeAllAsync(TestContext.Current.CancellationToken);

        service.ResultsFor("AllMatch").Count.ShouldBe(3);
        external.OpenCalls.Count.ShouldBe(3);
    }

    [Fact]
    public async Task WithProbeThatThrowsNonCancellationExceptionIsTreatedAsNoMatch()
    {
        var external = new ProbeTestExternal { Ports = ["serial:COM5"] };
        var probe = StubProbe.Sync("Buggy", baud: 9600,
            match: (_, _) => throw new InvalidOperationException("test bug"));
        var service = BuildService(external, output, probe);

        // Must not propagate the throw — probe exceptions are caught per plan.
        await service.ProbeAllAsync(TestContext.Current.CancellationToken);

        service.ResultsFor("Buggy").ShouldBeEmpty();
    }

    [Fact]
    public async Task WithSecondCallResultsAreClearedFirst()
    {
        var external = new ProbeTestExternal { Ports = ["serial:COM5"] };
        var matching = true;
        var probe = StubProbe.Sync("Flicker", baud: 9600,
            match: (port, _) => matching ? new SerialProbeMatch(port, new Uri("Mount://Flicker/x")) : null);
        var service = BuildService(external, output, probe);

        await service.ProbeAllAsync(TestContext.Current.CancellationToken);
        service.ResultsFor("Flicker").ShouldHaveSingleItem();

        matching = false;
        await service.ProbeAllAsync(TestContext.Current.CancellationToken);

        service.ResultsFor("Flicker").ShouldBeEmpty("second pass must clear prior results");
    }

    [Fact]
    public async Task OuterCancellationPropagatesAndStopsFurtherProbing()
    {
        var external = new ProbeTestExternal { Ports = ["serial:COM5"] };
        using var cts = new CancellationTokenSource();
        var probe = new StubProbe("Canceller", baud: 9600, budget: TimeSpan.FromSeconds(30),
            match: async (_, ct) =>
            {
                cts.Cancel();
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
                return (SerialProbeMatch?)null;
            });
        var service = BuildService(external, output, probe);

        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await service.ProbeAllAsync(cts.Token));
    }

    [Fact]
    public async Task WithPinnedPortTheProbeServiceSkipsItEntirely()
    {
        var external = new ProbeTestExternal { Ports = ["serial:COM5", "serial:COM6"] };
        var probe = StubProbe.Sync("AllMatch", baud: 9600,
            match: (port, _) => new SerialProbeMatch(port, new Uri($"Mount://FakeDevice/{port.Replace(':', '_')}")));
        var pinned = new StubPinnedPortsProvider("serial:COM5");

        var service = BuildService(external, output, pinned, probe);
        await service.ProbeAllAsync(TestContext.Current.CancellationToken);

        external.OpenCalls.Count.ShouldBe(1, "pinned COM5 must be skipped — only COM6 opened");
        external.OpenCalls.First().Port.ShouldBe("serial:COM6");
        service.ResultsFor("AllMatch").ShouldHaveSingleItem();
    }

    [Fact]
    public async Task WithPinnedPortMatchingIsCaseInsensitive()
    {
        var external = new ProbeTestExternal { Ports = ["serial:COM5"] };
        var probe = StubProbe.Sync("AllMatch", baud: 9600,
            match: (port, _) => new SerialProbeMatch(port, new Uri("Mount://FakeDevice/cool")));
        // Pinned set uses lowercase — must still match the mixed-case enumerated form.
        var pinned = new StubPinnedPortsProvider("serial:com5");

        var service = BuildService(external, output, pinned, probe);
        await service.ProbeAllAsync(TestContext.Current.CancellationToken);

        external.OpenCalls.ShouldBeEmpty("case-insensitive match must skip the only port");
    }

    [Fact]
    public async Task WithPinnedPortNotEnumeratedOtherPortsStillProbed()
    {
        // User moved the cable — pinned port no longer exists. Other ports must still probe.
        var external = new ProbeTestExternal { Ports = ["serial:COM5", "serial:COM6"] };
        var probe = StubProbe.Sync("AllMatch", baud: 9600,
            match: (port, _) => new SerialProbeMatch(port, new Uri($"Mount://FakeDevice/{port.Replace(':', '_')}")));
        var pinned = new StubPinnedPortsProvider("serial:COM99");

        var service = BuildService(external, output, pinned, probe);
        await service.ProbeAllAsync(TestContext.Current.CancellationToken);

        external.OpenCalls.Count.ShouldBe(2, "stale pin — still probe everything available");
    }

    // ---- helpers -----------------------------------------------------------

    private static SerialProbeService BuildService(ProbeTestExternal external, ITestOutputHelper output, params ISerialProbe[] probes)
        => BuildService(external, output, StubPinnedPortsProvider.Empty, probes);

    private static SerialProbeService BuildService(ProbeTestExternal external, ITestOutputHelper output, IPinnedSerialPortsProvider pinnedProvider, params ISerialProbe[] probes)
    {
        var factory = LoggerFactory.Create(b => b.AddProvider(new XUnitLoggerProvider(output, false)));
        var logger = factory.CreateLogger<SerialProbeService>();
        return new SerialProbeService(external, logger, probes, pinnedProvider);
    }

    private sealed class StubPinnedPortsProvider(params string[] pinned) : IPinnedSerialPortsProvider
    {
        public static readonly StubPinnedPortsProvider Empty = new();
        public IReadOnlySet<string> GetPinnedPorts() => new HashSet<string>(pinned, StringComparer.OrdinalIgnoreCase);
    }

    private sealed class ProbeTestExternal : FakeExternal
    {
        public ProbeTestExternal(ITestOutputHelper? output = null) : base(output ?? NullTestOutputHelper.Instance) { }

        public List<string> Ports { get; set; } = [];
        public ConcurrentBag<(string Port, int Baud)> OpenCalls { get; } = [];

        public override ValueTask<ISerialConnection> OpenSerialDeviceAsync(string address, int baud, Encoding encoding, CancellationToken cancellationToken = default)
        {
            OpenCalls.Add((address, baud));
            ISerialConnection conn = new StubSerialConnection(address, baud, encoding);
            return ValueTask.FromResult(conn);
        }

        public override IReadOnlyList<string> EnumerateAvailableSerialPorts(ResourceLock _) => Ports;
    }

    /// <summary>Minimal <see cref="ITestOutputHelper"/> for the zero-arg FakeExternal path.</summary>
    private sealed class NullTestOutputHelper : ITestOutputHelper
    {
        public static readonly NullTestOutputHelper Instance = new();
        public string Output => string.Empty;
        public void Write(string message) { }
        public void Write(string format, params object[] args) { }
        public void WriteLine(string message) { }
        public void WriteLine(string format, params object[] args) { }
    }

    private sealed class StubProbe(
        string name,
        int baud,
        Func<string, CancellationToken, ValueTask<SerialProbeMatch?>> match,
        TimeSpan? budget = null,
        int maxAttempts = 1,
        ProbeExclusivity exclusivity = ProbeExclusivity.Shared) : ISerialProbe
    {
        public string Name => name;
        public int BaudRate => baud;
        public Encoding Encoding => Encoding.ASCII;
        public ProbeExclusivity Exclusivity => exclusivity;
        public TimeSpan Budget => budget ?? TimeSpan.FromSeconds(1);
        public int MaxAttempts => maxAttempts;

        public ValueTask<SerialProbeMatch?> ProbeAsync(ISerialConnection conn, CancellationToken cancellationToken)
            => match(((StubSerialConnection)conn).Port, cancellationToken);

        /// <summary>Convenience for sync match callbacks — wraps the result in <see cref="ValueTask"/>.</summary>
        public static StubProbe Sync(string name, int baud, Func<string, CancellationToken, SerialProbeMatch?> match,
            TimeSpan? budget = null, int maxAttempts = 1, ProbeExclusivity exclusivity = ProbeExclusivity.Shared)
            => new(name, baud, (p, ct) => ValueTask.FromResult(match(p, ct)), budget, maxAttempts, exclusivity);
    }

    private sealed class StubSerialConnection(string port, int baud, Encoding encoding) : ISerialConnection
    {
        public string Port => port;
        public int Baud => baud;
        public bool IsOpen { get; private set; } = true;
        public Encoding Encoding => encoding;

        public bool TryClose() { IsOpen = false; return true; }
        public void Dispose() => TryClose();

        private readonly SemaphoreSlim _sem = new(1, 1);
        public ValueTask<ResourceLock> WaitAsync(CancellationToken cancellationToken) => _sem.AcquireLockAsync(cancellationToken);

        // Probes in these tests never call the IO methods (StubProbe decides match purely from port),
        // so throwing if hit keeps test setup honest.
        public ValueTask<bool> TryWriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<string?> TryReadTerminatedAsync(ReadOnlyMemory<byte> terminators, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<string?> TryReadExactlyAsync(int count, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<int> TryReadTerminatedRawAsync(Memory<byte> message, ReadOnlyMemory<byte> terminators, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<bool> TryReadExactlyRawAsync(Memory<byte> message, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
