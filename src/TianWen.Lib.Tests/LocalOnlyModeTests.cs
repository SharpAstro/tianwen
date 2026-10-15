using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using FC.SDK;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using TianWen.Hosting;
using TianWen.Hosting.Dto;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Alpaca;
using TianWen.Lib.Devices.Canon;
using TianWen.Lib.Devices.Discovery;
using TianWen.Lib.Devices.OnStep;
using TianWen.Lib.Devices.Skywatcher;
using TianWen.Lib.Extensions;
using TianWen.Lib.Sequencing;
using TianWen.RemoteClient;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The local-only mode (<see cref="LocalOnlyMode"/>, <c>TIANWEN_LOCAL_ONLY=1</c> or <c>tianwen-server --local-only</c>): what
/// it leaves out and what it keeps. Nothing here binds a socket: each network step is a seam, replaced by one that counts,
/// so these tests are also the negative control a run of a build WITHOUT the mode would have been (it would raise a
/// firewall prompt for the test binary's own path).
/// </summary>
public class LocalOnlyModeTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("1", true)]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData(" 1 ", true)]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("0", false)]
    [InlineData("false", false)]
    [InlineData("yes", false)]
    public void TheEnvironmentAsksForTheModeWithOneOrTrue(string? value, bool on)
    {
        var mode = LocalOnlyMode.FromEnvironmentValue(value);

        mode.IsOn.ShouldBe(on);
        mode.MayBindTheNetwork.ShouldBe(!on);
    }

    [Fact]
    public void TheServersCommandLineTurnsTheModeOnWhateverTheEnvironmentSays()
    {
        LocalOnlyMode.OfThisProcess(toldOnTheCommandLine: true).ShouldBe(LocalOnlyMode.On);
        // Without the switch it is the environment's, read once: the same answer every time it is asked.
        LocalOnlyMode.OfThisProcess().ShouldBeSameAs(LocalOnlyMode.OfThisProcess());
    }

    [Fact]
    public void AddExternalRegistersTheModeAndAHostsOwnRegisteredFirstWins()
    {
        using var fromTheEnvironment = new ServiceCollection().AddExternal().BuildServiceProvider();
        fromTheEnvironment.GetRequiredService<LocalOnlyMode>().ShouldBe(LocalOnlyMode.OfThisProcess());

        // The server: its command line counts too, so its decision is registered before AddExternal's.
        using var theServers = new ServiceCollection().AddLocalOnlyMode(LocalOnlyMode.On).AddExternal().BuildServiceProvider();
        theServers.GetRequiredService<LocalOnlyMode>().ShouldBe(LocalOnlyMode.On);
        using var notTheServers = new ServiceCollection().AddLocalOnlyMode(LocalOnlyMode.Off).AddExternal().BuildServiceProvider();
        notTheServers.GetRequiredService<LocalOnlyMode>().ShouldBe(LocalOnlyMode.Off);
    }

    [Theory]
    // Run by hand, a node is on the LAN unless local-only; shared or not, a local-only node is on its socket alone.
    [InlineData(false, false, false, true)]
    [InlineData(false, false, true, false)]
    [InlineData(true, true, false, true)]
    [InlineData(true, true, true, false)]
    public void ALocalOnlyNodeListensOnItsSocketAloneAndSaysSo(bool spawned, bool shared, bool localOnly, bool onTheLan)
    {
        var node = new NodeArguments(Path.Combine(Path.GetTempPath(), "node.sock"), NodeArguments.DefaultPort, localOnly, Keeper: false, spawned,
            FakeDevicesOnly: false, AfterCrashOf: null);

        var listening = NodeListeningDecision.For(node, new NodeSettings(shared));

        listening.IsShared.ShouldBe(onTheLan);
        listening.LocalOnly.ShouldBe(localOnly, "GET /api/v1/node reports it, and a client compares it with its own");
        node.ForTheNode().Contains(LocalOnlyMode.Argument).ShouldBe(localOnly, "a keeper hands the mode to the node it starts");
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 0)]
    public async Task CanonScansTheNetworkForCamerasOnlyWhenNotLocalOnly(bool localOnly, int scans)
    {
        var ct = TestContext.Current.CancellationToken;
        var asked = 0;
        var source = new CanonDeviceSource(NullLogger<CanonDeviceSource>.Instance, new OneWpdCamera(), new CanonBodyRegistry(), Mode(localOnly))
        {
            ScanMdnsAsync = (_, _) =>
            {
                asked++;
                return Task.FromResult<IReadOnlyList<(string InstanceName, string IpAddr)>>([]);
            },
        };

        (await source.DiscoverWifiAsync(ct)).ShouldBeEmpty();
        asked.ShouldBe(scans);

        // The source stays: a camera on the USB cable is listed either way.
        (await source.DiscoverWpdAsync(ct)).ShouldHaveSingleItem().IsWpd.ShouldBeTrue();
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 0)]
    public async Task OnStepScansTheNetworkOnlyWhenNotLocalOnlyAndKeepsItsSerialMounts(bool localOnly, int scans)
    {
        var asked = 0;
        var serial = new OnStepDevice(DeviceType.Mount, "OnStep_A1", "OnStep", "COM7");
        var source = new OnStepDeviceSource(NullLogger<OnStepDeviceSource>.Instance, new OneSerialMatch("OnStep", serial.DeviceUri), Mode(localOnly))
        {
            ScanMdnsAsync = (_, _) =>
            {
                asked++;
                return Task.FromResult<IReadOnlyList<(string InstanceName, string IpAddr)>>([]);
            },
        };

        await source.DiscoverAsync(TestContext.Current.CancellationToken);

        asked.ShouldBe(scans);
        source.RegisteredDevices(DeviceType.Mount).ShouldHaveSingleItem().DeviceUri.ShouldBe(serial.DeviceUri);
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 0)]
    public async Task SkywatcherBroadcastsOnlyWhenNotLocalOnlyAndKeepsItsSerialMounts(bool localOnly, int broadcasts)
    {
        var sent = 0;
        var serial = new SkywatcherDevice(DeviceType.Mount, "Skywatcher_EQ6R", "EQ6-R", "COM9");
        var source = new SkywatcherDeviceSource(new OneSerialMatch("Skywatcher", serial.DeviceUri), NullLogger<SkywatcherDeviceSource>.Instance, Mode(localOnly))
        {
            BroadcastWifiAsync = ct =>
            {
                sent++;
                return NoMounts(ct);
            },
        };

        await source.DiscoverAsync(TestContext.Current.CancellationToken);

        sent.ShouldBe(broadcasts);
        source.RegisteredDevices(DeviceType.Mount).ShouldHaveSingleItem().DeviceUri.ShouldBe(serial.DeviceUri);
    }

    [Fact]
    public async Task AlpacaBroadcastsWhenNotLocalOnlyAndAsksLoopbackFromLoopbackWhenItIs()
    {
        var ct = TestContext.Current.CancellationToken;
        using var http = new HttpClient();
        var client = new AlpacaClient(http);

        var asked = new List<(IPEndPoint? Bind, IPEndPoint Target)>();
        Task<List<(string Host, int Port)>> Record(IPEndPoint? bind, IPEndPoint target, CancellationToken _)
        {
            asked.Add((bind, target));
            return Task.FromResult(new List<(string Host, int Port)>());
        }

        await new AlpacaDeviceSource(client, NullLogger<AlpacaDeviceSource>.Instance, LocalOnlyMode.Off) { AskForServersAsync = Record }.DiscoverAsync(ct);
        await new AlpacaDeviceSource(client, NullLogger<AlpacaDeviceSource>.Instance, LocalOnlyMode.On) { AskForServersAsync = Record }.DiscoverAsync(ct);

        asked.Count.ShouldBe(2);
        asked[0].Bind.ShouldBeNull("the OS binds the broadcasting socket on every interface");
        asked[0].Target.ShouldBe(new IPEndPoint(IPAddress.Broadcast, AlpacaDeviceSource.AlpacaDiscoveryPort));
        asked[1].Bind.ShouldBe(new IPEndPoint(IPAddress.Loopback, 0), "bound to loopback, so nothing else is");
        asked[1].Target.ShouldBe(new IPEndPoint(IPAddress.Loopback, AlpacaDeviceSource.AlpacaDiscoveryPort), "a simulator on this machine is still found");
    }

    [Fact]
    public async Task ALoopbackAlpacaServerIsNamedByItsAddressNeverByReverseDns()
    {
        // What a local-only discovery finds answers from loopback, and its name goes into the device URI's host. A reverse
        // lookup of 127.0.0.1 answers whatever the hosts file maps it to: on the desk machine kubernetes.docker.internal
        // (Docker's entry), elsewhere the machine's name or "localhost", none of which names THIS machine's loopback for sure.
        (await AlpacaDeviceSource.HostOfAsync(IPAddress.Loopback)).ShouldBe("127.0.0.1");

        // The host is not the device's identity: its key is the server's UniqueID in the path, so a profile still matches it.
        var asDiscovered = new AlpacaDevice(DeviceType.Camera, "sim-camera-0", "127.0.0.1", 11111, 0, "Alpaca Camera Sim");
        var asSaved = new AlpacaDevice(DeviceType.Camera, "sim-camera-0", "kubernetes.docker.internal", 11111, 0, "Alpaca Camera Sim");
        DeviceBase.SameDevice(asDiscovered.DeviceUri, asSaved.DeviceUri).ShouldBeTrue();
        asDiscovered.DeviceId.ShouldBe(asSaved.DeviceId);
    }

    [Fact]
    public async Task ALocalOnlyProcessDoesNotOpenAWifiSkywatcherMountAndSaysWhy()
    {
        var ct = TestContext.Current.CancellationToken;
        var external = new FakeExternal(output);
        using var sp = new ServiceCollection()
            .AddSingleton<IExternal>(external)
            .AddSingleton<ITimeProvider>(SystemTimeProvider.Instance)
            .AddLogging()
            .AddLocalOnlyMode(LocalOnlyMode.On)
            .BuildServiceProvider();
        var wifi = new SkywatcherDevice(DeviceType.Mount, "Skywatcher_AzGTi", "AZ-GTi", "192.168.4.1");
        wifi.IsWifi.ShouldBeTrue();
        new SkywatcherDevice(DeviceType.Mount, "Skywatcher_EQ6R", "EQ6-R", "COM9").IsWifi.ShouldBeFalse();

        var driver = new Mount(wifi, sp).Driver;
        var refused = await Should.ThrowAsync<InvalidOperationException>(() => driver.ConnectAsync(ct).AsTask());

        refused.Message.ShouldContain("local-only");
        refused.Message.ShouldContain(LocalOnlyMode.EnvironmentVariable);
        driver.Connected.ShouldBeFalse();
    }

    [Fact]
    public void AKeeperIsToldLocalOnlyOnceWhicheverWayTheClientAskedForIt()
    {
        new LocalNodeOptions { LocalOnly = LocalOnlyMode.On }.KeeperArguments("node.sock")
            .ShouldBe(["--keeper", "--socket", "node.sock", LocalOnlyMode.Argument]);
        new LocalNodeOptions { LocalOnly = LocalOnlyMode.On, NodeArguments = [LocalOnlyMode.Argument, "--fake-devices"] }.KeeperArguments("node.sock")
            .ShouldBe(["--keeper", "--socket", "node.sock", LocalOnlyMode.Argument, "--fake-devices"]);
        new LocalNodeOptions { LocalOnly = LocalOnlyMode.Off }.KeeperArguments("node.sock")
            .ShouldBe(["--keeper", "--socket", "node.sock"]);

        new LocalNodeOptions { LocalOnly = LocalOnlyMode.Off, NodeArguments = [LocalOnlyMode.Argument] }.StartsLocalOnly
            .ShouldBeTrue("a test's node told --local-only is the node this client starts");
        new LocalNodeOptions { LocalOnly = LocalOnlyMode.Off }.StartsLocalOnly.ShouldBeFalse();
    }

    [Theory]
    // An idle node nobody is attached to, of the other mode, is replaced by one of the client's, either way round.
    [InlineData(true, false, false, 0, false, true)]
    [InlineData(false, true, false, 0, false, true)]
    // Of the same mode and build it is the one.
    [InlineData(true, true, false, 0, false, false)]
    [InlineData(false, false, false, 0, false, false)]
    // In use, or on a socket the client was pointed at, it is used as it is.
    [InlineData(true, false, true, 0, false, false)]
    [InlineData(true, false, false, 1, false, false)]
    [InlineData(true, false, false, 0, true, false)]
    public void ANodeOfTheOtherModeIsReplacedOnlyWhileIdleAndUnattended(bool nodeLocalOnly, bool clientLocalOnly, bool holdsHardware, int clientsAttached,
        bool named, bool replaced)
    {
        var node = Node(nodeLocalOnly, holdsHardware, clientsAttached);

        LocalNodeLauncher.Replaces(node, named, anotherBuild: false, clientLocalOnly).ShouldBe(replaced);
    }

    [Fact]
    public void ANodeSharedOnTheLanIsNeverReplacedForItsModeAlone()
    {
        // A node reachable from the LAN is a rig someone shares (a mini PC's, or this machine's on purpose): a local-only client
        // must not take it away for its mode alone. It is used as it is, and the note says why.
        var shared = Node(localOnly: false, isShared: true);

        LocalNodeLauncher.Replaces(shared, named: false, anotherBuild: false, clientLocalOnly: true)
            .ShouldBeFalse("a shared node is not replaced for its mode");
        LocalNodeLauncher.Replaces(shared, named: false, anotherBuild: true, clientLocalOnly: true)
            .ShouldBeFalse("nor for its build: the replacement would be local-only, taking the rig off the LAN (every worktree build is another build)");
        LocalNodeLauncher.Replaces(shared, named: false, anotherBuild: true, clientLocalOnly: false)
            .ShouldBeTrue("a client of its own mode replaces another build as it always did, and keeps it on the LAN");
        LocalNodeLauncher.Replaces(Node(localOnly: false), named: false, anotherBuild: true, clientLocalOnly: true)
            .ShouldBeTrue("an unshared node of another build is replaced as it always was");
        LocalNodeLauncher.ModeNote(shared, clientLocalOnly: true).ShouldNotBeNull().ShouldContain("shares the rig on the LAN");
    }

    [Fact]
    public void ANodeUsedInTheOtherModeIsNeverUsedSilently()
    {
        LocalNodeLauncher.ModeNote(Node(localOnly: true), clientLocalOnly: true).ShouldBeNull();
        LocalNodeLauncher.ModeNote(Node(localOnly: false), clientLocalOnly: false).ShouldBeNull();

        LocalNodeLauncher.ModeNote(Node(localOnly: true, holdsHardware: true), clientLocalOnly: false)
            .ShouldNotBeNull().ShouldContain("is local-only");
        var notLocal = LocalNodeLauncher.ModeNote(Node(localOnly: false, clientsAttached: 1), clientLocalOnly: true).ShouldNotBeNull();
        notLocal.ShouldContain("not local-only");
        notLocal.ShouldContain("another client is attached");
    }

    private static LocalOnlyMode Mode(bool on) => on ? LocalOnlyMode.On : LocalOnlyMode.Off;

    private static NodeInfoDto Node(bool localOnly, bool holdsHardware = false, int clientsAttached = 0, bool isShared = false) => new NodeInfoDto
    {
        NodeId = "node",
        Version = "10.0.0",
        LocalOnly = localOnly,
        HoldsHardware = holdsHardware,
        ClientsAttached = clientsAttached,
        IsShared = isShared,
    };

    private static async IAsyncEnumerable<SkywatcherDevice> NoMounts([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        cancellationToken.ThrowIfCancellationRequested();
        yield break;
    }

    /// <summary>One camera on the USB cable, as Windows lists it, which reports no serial.</summary>
    private sealed class OneWpdCamera : ICanonWpd
    {
        public IEnumerable<(string Id, string Name)> Enumerate() => [(@"\\?\usb#vid_04a9&pid_3250#6&fd1ea69&1&2", "Canon EOS 6D")];

        public Task<CanonBodyIdentity?> ReadIdentityAsync(string wpdId, CancellationToken cancellationToken) => Task.FromResult<CanonBodyIdentity?>(null);
    }

    /// <summary>A serial probe pass that matched one device for <paramref name="probe"/>.</summary>
    private sealed class OneSerialMatch(string probe, Uri device) : ISerialProbeService
    {
        public ValueTask ProbeAllAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public IReadOnlyList<SerialProbeMatch> ResultsFor(string probeName) => probeName == probe ? [new SerialProbeMatch("serial:COM7", device)] : [];
    }
}
