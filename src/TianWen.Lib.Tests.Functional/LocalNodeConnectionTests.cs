using DIR.Lib;
using LAN.Lib;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Devices;
using TianWen.Lib.Sequencing;
using TianWen.RemoteClient;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests.Functional;

/// <summary>
/// This computer's view is its node (P6 part 2 of docs/plans/hardware-in-the-server.md, #936): the GUI and the TUI find or
/// start the machine's node and read it as they read a rig, its active profile being the app's. Over a real node on its own
/// socket, which is what a client of this machine reaches.
/// </summary>
[Collection("NodeProcesses")]
public class LocalNodeConnectionTests(ITestOutputHelper outputHelper) : IDisposable
{
    /// <summary>The temporary folders this test made, deleted after it (#1197).</summary>
    private readonly TempFolders _folders = new();

    public void Dispose() => _folders.Dispose();

    private string NewSocketPath() => Path.Combine(_folders.Create("tws").FullName, "node.sock");

    private sealed class Client : IAsyncDisposable
    {
        private readonly ServiceProvider _services;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();

        public Client(ITestOutputHelper output, IPeerTable? peers = null)
        {
            var catalog = Substitute.For<ICelestialObjectDB>();
            catalog.DeepSkyCoordinateGrid.Returns(new RaDecIndex());
            catalog.AllObjectIndices.Returns(new HashSet<CatalogIndex>());
            _services = new ServiceCollection()
                .AddSingleton<IExternal>(new FakeExternal(output))
                .AddSingleton<ITimeProvider>(new SystemTimeProvider())
                .AddLogging()
                .AddSingleton<ViewerState>()
                .AddSingleton<PlanetaryCaptureController>()
                .AddSingleton(catalog)
                .BuildServiceProvider();
            AppState = new GuiAppState { PeerTable = peers };
            Handler = new AppSignalHandler(_services, AppState, new PlannerState(), new SessionTabState(), new EquipmentTabState(),
                Contexts, new SkyMapState(), new SignalBus(), Tracker, _cts, _cts.Token, _services.GetRequiredService<IExternal>());
        }

        public GuiAppState AppState { get; }
        public ViewContexts Contexts { get; } = new ViewContexts();
        public BackgroundTaskTracker Tracker { get; } = new BackgroundTaskTracker();
        public AppSignalHandler Handler { get; }

        /// <summary>Connects to the node on <paramref name="socketPath"/>, and only connects: a named socket starts no node.</summary>
        public Task ConnectAsync(string socketPath, string? requestedProfile, CancellationToken ct) =>
            Handler.ConnectLocalNodeAsync(new LocalNodeOptions { NamedSocket = socketPath, AnotherAccountProbe = null }, requestedProfile, includeFake: false, ct);

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            if (AppState.LocalNode is { } local)
            {
                await local.DisposeAsync();
            }
            await _services.DisposeAsync();
            _cts.Dispose();
        }
    }

    private static ProfileData Siteless => new ProfileData(NoneDevice.Instance.DeviceUri, NoneDevice.Instance.DeviceUri, []);

    [Fact(Timeout = 30_000)]
    public async Task TheLocalViewReadsItsNodeAndRunsTheOnlyProfileThere()
    {
        var ct = TestContext.Current.CancellationToken;
        var socketPath = NewSocketPath();
        await using var node = await NodeHarness.StartAsync(outputHelper, ct, socketPath: socketPath);
        var only = new Profile(Guid.NewGuid(), "The only rig", Siteless);
        await only.SaveAsync(node.External, ct);
        await using var client = new Client(outputHelper);

        await client.ConnectAsync(socketPath, requestedProfile: null, ct);

        var local = client.AppState.LocalNode.ShouldNotBeNull(client.AppState.LocalNodeProblem);
        client.AppState.LocalNodeProblem.ShouldBeNull();
        var nodeInfo = (await local.Client.GetNodeAsync(ct)).Value.ShouldNotBeNull();
        client.Contexts.Local.NodeId.ShouldBe(nodeInfo.NodeId, "this computer's view takes its node's id");
        client.Contexts.Local.Mirror.ShouldBeSameAs(local.Mirror, "the local view reads its node as a rig's is read");
        client.Contexts.Local.LiveSession.ActiveSession.ShouldBeSameAs(local.Mirror);

        client.AppState.ActiveProfile.ShouldNotBeNull().ProfileId.ShouldBe(only.ProfileId, "the node ran none, so the only profile it has");
        (await local.Client.GetActiveProfileAsync(ct)).Value.ShouldNotBeNull().ProfileId.ShouldBe(only.ProfileId, "and the node runs it now");
        local.ProfileRevision.ShouldNotBeNull("an edit names the revision the profile was read at");
    }

    [Fact(Timeout = 30_000)]
    public async Task AProfileAskedForByNameIsMadeTheNodes()
    {
        var ct = TestContext.Current.CancellationToken;
        var socketPath = NewSocketPath();
        await using var node = await NodeHarness.StartAsync(outputHelper, ct, socketPath: socketPath);
        var first = new Profile(Guid.NewGuid(), "Refractor", Siteless);
        var second = new Profile(Guid.NewGuid(), "Newtonian", Siteless);
        await first.SaveAsync(node.External, ct);
        await second.SaveAsync(node.External, ct);
        (await new TianWenNodeClient(node.Client).SetActiveProfileAsync(first.ProfileId, ct)).IsSuccess.ShouldBeTrue();
        await using var client = new Client(outputHelper);

        await client.ConnectAsync(socketPath, requestedProfile: "newtonian", ct);

        client.AppState.ActiveProfile.ShouldNotBeNull().ProfileId.ShouldBe(second.ProfileId);
        (await new TianWenNodeClient(node.Client).GetActiveProfileAsync(ct)).Value.ShouldNotBeNull().ProfileId.ShouldBe(second.ProfileId);
    }

    [Fact(Timeout = 30_000)]
    public async Task ANodeRunningAProfileKeepsItWhenNoneIsAskedFor()
    {
        var ct = TestContext.Current.CancellationToken;
        var socketPath = NewSocketPath();
        await using var node = await NodeHarness.StartAsync(outputHelper, ct, socketPath: socketPath);
        var first = new Profile(Guid.NewGuid(), "Refractor", Siteless);
        var second = new Profile(Guid.NewGuid(), "Newtonian", Siteless);
        await first.SaveAsync(node.External, ct);
        await second.SaveAsync(node.External, ct);
        (await new TianWenNodeClient(node.Client).SetActiveProfileAsync(second.ProfileId, ct)).IsSuccess.ShouldBeTrue();
        await using var client = new Client(outputHelper);

        await client.ConnectAsync(socketPath, requestedProfile: null, ct);

        client.AppState.ActiveProfile.ShouldNotBeNull().ProfileId.ShouldBe(second.ProfileId, "the node's own, which another client may have chosen");
    }

    [Fact(Timeout = 30_000)]
    public async Task NoNodeToReachIsSaidPlainly()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var client = new Client(outputHelper);

        await client.ConnectAsync(NewSocketPath(), requestedProfile: null, ct);

        client.AppState.LocalNode.ShouldBeNull();
        var problem = client.AppState.LocalNodeProblem.ShouldNotBeNull();
        problem.ShouldContain("No node answers");
        client.AppState.Notifications.ShouldContain(n => n.Severity == NotificationSeverity.Error && n.Message.Contains(problem));
    }

    [Fact(Timeout = 30_000)]
    public async Task ThisComputersNodeIsNoRigOfItsOwn()
    {
        var ct = TestContext.Current.CancellationToken;
        var socketPath = NewSocketPath();
        await using var node = await NodeHarness.StartAsync(outputHelper, ct, socketPath: socketPath);
        var nodeId = (await new TianWenNodeClient(node.Client).GetNodeAsync(ct)).Value.ShouldNotBeNull().NodeId;
        // The node announcing itself on the LAN (its rig shared), beside another rig.
        var peers = Substitute.For<IPeerTable>();
        peers.PeersOf(RemoteRigConnection.NodeServiceName).Returns(
        [
            Peer(nodeId, "This computer"),
            Peer("observatory-node", "Observatory"),
        ]);
        await using var client = new Client(outputHelper, peers);

        await client.ConnectAsync(socketPath, requestedProfile: null, ct);

        RemoteRigActions.RigPeers(client.AppState).Select(static p => p.NodeId).ShouldBe(["observatory-node"],
            "the picker lists the other rig and never this computer's node");

        // Bound as a rig before its view became the local one: no card of its own, and no second connection.
        var ownBinding = new RemoteRigBinding { BindingId = Guid.NewGuid(), NodeId = nodeId, Alias = "This computer as a rig" };
        client.Handler.Rigs.SetBindings([ownBinding]);
        var cards = HomeBoard.BuildCards(client.Contexts, client.Handler.Rigs, client.AppState, DateTimeOffset.UtcNow);
        cards.ShouldHaveSingleItem().IsLocal.ShouldBeTrue();
        var sweep = await RemoteRigActions.ConnectAllAsync(client.Handler.Rigs, client.Contexts, client.AppState,
            new FakeExternal(outputHelper), new SystemTimeProvider(), Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, ct);
        sweep.DidAnything.ShouldBeFalse();
        client.Handler.Rigs.IsConnected(ownBinding.BindingId).ShouldBeFalse();
    }

    private static LanPeer Peer(string nodeId, string name) =>
        new LanPeer(Guid.NewGuid().ToString("N"), RemoteRigConnection.NodeServiceName, name, new IPEndPoint(IPAddress.Loopback, 1888),
            nodeId, Environment.MachineName, 1, new Dictionary<string, string>(), DateTimeOffset.UtcNow);
}
