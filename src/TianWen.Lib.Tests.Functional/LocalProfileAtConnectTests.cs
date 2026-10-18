using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Fake;
using TianWen.Lib.Sequencing;
using TianWen.RemoteClient;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests.Functional;

/// <summary>
/// The GUI starting on a node that runs a profile never says it has none (the owner, 2026-10-18: switching to the Equipment tab at
/// start "briefly showed that no profile is configured"). The per-frame profile poll could take the first read as soon as the
/// connection was published, before the connect had chosen and read the profile, so the connect's own read was skipped and the
/// profile was counted known while it was still null: "No equipment profile configured" until the poll's read landed.
/// </summary>
[Collection("NodeProcesses")]
public class LocalProfileAtConnectTests(ITestOutputHelper output)
{
    /// <summary>
    /// The window's frames run all through the connect, as they do at start; at no frame is the profile known and null, and once the
    /// connect is done it is the node's. Five connects, since the race needs a frame between the connection's publishing and its read.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task TheProfileIsNeverKnownAsNoneWhileTheFramesPoll()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var node = await NodeHarness.StartAsync(output, ct, onItsSocket: true);
        var socketPath = node.SocketPath ?? throw new InvalidOperationException("A node started on its own socket has one.");
        var profile = new Profile(Guid.NewGuid(), "This computer's rig", new ProfileData(
            Mount: new FakeDevice(DeviceType.Mount, 1).DeviceUri,
            Guider: new FakeDevice(DeviceType.Guider, 1).DeviceUri,
            OTAs: [new OTAData("Scope", 500, new FakeDevice(DeviceType.Camera, 1).DeviceUri, null, null, null, null, null)],
            SiteLatitude: 48.2,
            SiteLongitude: 16.3));
        await profile.SaveAsync(node.External, ct);
        await node.Node.SetActiveProfileAsync(profile.ProfileId, ct);

        var seenNone = 0;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var gui = GuiNodeHarness.ComposeClient(output);
            try
            {
                var connect = gui.Handler.ConnectLocalNodeAsync(
                    new LocalNodeOptions { NamedSocket = socketPath, AnotherAccountProbe = null, LocalOnly = LocalOnlyMode.Off },
                    requestedProfile: null, includeFake: false, ct);
                var frames = 0;
                while (!connect.IsCompleted)
                {
                    gui.Handler.PollPreviewTelemetry();
                    gui.Tracker.ProcessCompletions(NullLogger.Instance);
                    frames++;
                    if (gui.AppState is { LocalProfileKnown: true, ActiveProfile: null })
                    {
                        seenNone++;
                    }
                    await Task.Yield();
                }
                await connect;
                output.WriteLine($"connect {attempt}: {frames} frames, profile {gui.AppState.ActiveProfile?.ProfileId.ToString() ?? "none"}");
                gui.AppState.LocalProfileKnown.ShouldBeTrue();
                gui.AppState.ActiveProfile.ShouldNotBeNull("the connect is done and the node's profile is not read")
                    .ProfileId.ShouldBe(profile.ProfileId);
            }
            finally
            {
                await gui.Cts.CancelAsync();
                await gui.Tracker.DrainAsync();
                if (gui.AppState.LocalNode is { } local)
                {
                    await local.DisposeAsync();
                }
                await gui.Services.DisposeAsync();
            }
        }
        seenNone.ShouldBe(0, "frames saw the profile known and none while the node runs one");
    }
}
