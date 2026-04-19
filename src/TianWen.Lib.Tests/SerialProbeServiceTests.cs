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
/// (per-port × per-baud), result publishing, timeout handling, exclusivity, and the
/// two-tier pinned-port verify-then-fall-through algorithm — not on real wire
/// protocols (those are covered by source-specific probe tests).
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

    // -- Two-tier (verify + general) pinned-port algorithm -------------------

    [Fact]
    public async Task WithPinnedPortAndMatchingIdentityStage1VerifiesAndSkipsStage2()
    {
        // Pinned OnStep is still on COM5 — Stage 1 confirms, Stage 2 skips the port.
        var external = new ProbeTestExternal { Ports = ["serial:COM5", "serial:COM6"] };
        var probe = StubProbe.Sync("OnStep", baud: 9600,
            match: (port, _) => new SerialProbeMatch(port, new Uri($"Mount://OnStepDevice/stable-id#OnStep on {port}")),
            matchesDeviceHosts: ["OnStepDevice"]);
        var pinned = new StubPinnedPortsProvider(
            new PinnedSerialPort("serial:COM5", new Uri("Mount://OnStepDevice/stable-id?port=COM5")));

        var service = BuildService(external, output, pinned, probe);
        await service.ProbeAllAsync(TestContext.Current.CancellationToken);

        // COM5 opened once for verification (Stage 1), COM6 opened once for general probing (Stage 2).
        external.OpenCalls.Count.ShouldBe(2);
        service.ResultsFor("OnStep").Count.ShouldBe(2, "both ports matched the OnStep probe");
        service.ResultsFor("OnStep").ShouldContain(m => m.Port == "serial:COM5");
        service.ResultsFor("OnStep").ShouldContain(m => m.Port == "serial:COM6");
    }

    [Fact]
    public async Task WithCableSwapBothDevicesStillDiscoveredViaStage2Fallback()
    {
        // Two pinned devices — OnStep@COM5, Meade@COM6. User swapped cables so OnStep
        // is now on COM6, Meade on COM5. Stage 1 verification fails on both pins
        // (identity mismatch) — ports fall through to Stage 2, which probes with every
        // registered probe and discovers the true current assignment.
        var external = new ProbeTestExternal { Ports = ["serial:COM5", "serial:COM6"] };

        var onStep = StubProbe.Sync("OnStep", baud: 9600,
            match: (port, _) => port == "serial:COM6"
                ? new SerialProbeMatch(port, new Uri("Mount://OnStepDevice/onstep-id"))
                : null,
            matchesDeviceHosts: ["OnStepDevice"]);
        var meade = StubProbe.Sync("Meade", baud: 9600,
            match: (port, _) => port == "serial:COM5"
                ? new SerialProbeMatch(port, new Uri("Mount://MeadeDevice/meade-id"))
                : null,
            matchesDeviceHosts: ["MeadeDevice"]);

        var pinned = new StubPinnedPortsProvider(
            new PinnedSerialPort("serial:COM5", new Uri("Mount://OnStepDevice/onstep-id?port=COM5")),
            new PinnedSerialPort("serial:COM6", new Uri("Mount://MeadeDevice/meade-id?port=COM6")));

        var service = BuildService(external, output, pinned, onStep, meade);
        await service.ProbeAllAsync(TestContext.Current.CancellationToken);

        service.ResultsFor("OnStep").ShouldHaveSingleItem().Port.ShouldBe("serial:COM6",
            "cable swap: OnStep found on the other port via Stage 2");
        service.ResultsFor("Meade").ShouldHaveSingleItem().Port.ShouldBe("serial:COM5",
            "cable swap: Meade found on the other port via Stage 2");
    }

    [Fact]
    public async Task WithPinnedPortAndNoMatchingProbeFallsThroughToStage2()
    {
        // Profile pins a device family whose probe isn't registered yet (pre-migration state).
        // Verification is impossible → port must still be probed in Stage 2 by whatever
        // probes are registered.
        var external = new ProbeTestExternal { Ports = ["serial:COM5"] };
        var probe = StubProbe.Sync("RegisteredProbe", baud: 9600,
            match: (port, _) => new SerialProbeMatch(port, new Uri("Focuser://OtherDevice/x")),
            matchesDeviceHosts: ["OtherDevice"]);
        var pinned = new StubPinnedPortsProvider(
            new PinnedSerialPort("serial:COM5", new Uri("Mount://UnknownDevice/lost-device?port=COM5")));

        var service = BuildService(external, output, pinned, probe);
        await service.ProbeAllAsync(TestContext.Current.CancellationToken);

        service.ResultsFor("RegisteredProbe").ShouldHaveSingleItem("Stage 2 ran on the pinned port because Stage 1 couldn't verify");
    }

    [Fact]
    public async Task WithPinnedPortAndIdentityMismatchProbeDoesNotPublishFromStage1()
    {
        // Pinned URI says OnStep/id-A at COM5 — but COM5 now reports OnStep/id-B.
        // Stage 1: probe matches the family (OnStep) but identity differs → no publish,
        // port falls through. Stage 2 probes COM5 again with the full probe set; the
        // OnStep probe now matches and publishes once — total of ONE published match.
        var external = new ProbeTestExternal { Ports = ["serial:COM5"] };
        var probe = StubProbe.Sync("OnStep", baud: 9600,
            match: (port, _) => new SerialProbeMatch(port, new Uri("Mount://OnStepDevice/id-B")),
            matchesDeviceHosts: ["OnStepDevice"]);
        var pinned = new StubPinnedPortsProvider(
            new PinnedSerialPort("serial:COM5", new Uri("Mount://OnStepDevice/id-A?port=COM5")));

        var service = BuildService(external, output, pinned, probe);
        await service.ProbeAllAsync(TestContext.Current.CancellationToken);

        var results = service.ResultsFor("OnStep");
        results.Count.ShouldBe(1, "Stage 1 identity mismatch discards, Stage 2 republishes exactly once");
        results[0].DeviceUri.AbsolutePath.ShouldBe("/id-B");
    }

    [Fact]
    public async Task WithPinnedPortNotEnumeratedSkipsVerificationAndProbesTheRest()
    {
        // Stale pin — COM99 doesn't exist in this enumeration. Verification ignores
        // missing ports and the real ports probe normally.
        var external = new ProbeTestExternal { Ports = ["serial:COM5", "serial:COM6"] };
        var probe = StubProbe.Sync("AllMatch", baud: 9600,
            match: (port, _) => new SerialProbeMatch(port, new Uri($"Mount://FakeDevice/{port.Replace(':', '_')}")),
            matchesDeviceHosts: ["FakeDevice"]);
        var pinned = new StubPinnedPortsProvider(
            new PinnedSerialPort("serial:COM99", new Uri("Mount://FakeDevice/ghost?port=COM99")));

        var service = BuildService(external, output, pinned, probe);
        await service.ProbeAllAsync(TestContext.Current.CancellationToken);

        external.OpenCalls.Count.ShouldBe(2, "stale pin ignored — COM5 + COM6 probed normally");
        service.ResultsFor("AllMatch").Count.ShouldBe(2);
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

    private sealed class StubPinnedPortsProvider(params PinnedSerialPort[] pinned) : IPinnedSerialPortsProvider
    {
        public static readonly StubPinnedPortsProvider Empty = new();
        public IReadOnlyList<PinnedSerialPort> GetPinnedPorts() => pinned;
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
        ProbeExclusivity exclusivity = ProbeExclusivity.Shared,
        IReadOnlyCollection<string>? matchesDeviceHosts = null) : ISerialProbe
    {
        public string Name => name;
        public int BaudRate => baud;
        public Encoding Encoding => Encoding.ASCII;
        public ProbeExclusivity Exclusivity => exclusivity;
        public TimeSpan Budget => budget ?? TimeSpan.FromSeconds(1);
        public int MaxAttempts => maxAttempts;
        public IReadOnlyCollection<string> MatchesDeviceHosts => matchesDeviceHosts ?? [];

        public ValueTask<SerialProbeMatch?> ProbeAsync(ISerialConnection conn, CancellationToken cancellationToken)
            => match(((StubSerialConnection)conn).Port, cancellationToken);

        /// <summary>Convenience for sync match callbacks — wraps the result in <see cref="ValueTask"/>.</summary>
        public static StubProbe Sync(string name, int baud, Func<string, CancellationToken, SerialProbeMatch?> match,
            TimeSpan? budget = null, int maxAttempts = 1, ProbeExclusivity exclusivity = ProbeExclusivity.Shared,
            IReadOnlyCollection<string>? matchesDeviceHosts = null)
            => new(name, baud, (p, ct) => ValueTask.FromResult(match(p, ct)), budget, maxAttempts, exclusivity, matchesDeviceHosts);
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
