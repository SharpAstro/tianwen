using System.Linq;
using System.Threading.Tasks;
using Shouldly;
using TianWen.DAL;
using TianWen.Lib.Devices;
using TianWen.Lib.Sequencing;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests.Functional;

/// <summary>
/// This computer's planetary capture is its node's (P6 part 2 of docs/plans/hardware-in-the-server.md, #936): the node claims
/// the camera, streams and stacks, and this computer shows the masters it streams, sends the panel's controls to it, and
/// asks it to stop. A window that goes does not stop it. Through the GUI's real signal handler over a real node on its socket.
/// </summary>
[Collection("NodeProcesses")]
public class PlanetaryThroughTheNodeTests(ITestOutputHelper output)
{
    private static async Task<PlanetaryCaptureController> StartedAsync(GuiNodeHarness h)
    {
        var ct = TestContext.Current.CancellationToken;
        h.Post(new StartVideoCaptureSignal(RoiWidth: 320, RoiHeight: 200));
        await h.UntilSettledAsync(ct);
        h.AppState.Notifications.ShouldNotContain(static n => n.Severity >= NotificationSeverity.Warning,
            string.Join("; ", h.AppState.Notifications.Select(static n => n.Message)));
        var planetary = h.Planetary;
        planetary.IsCapturing.ShouldBeTrue("premise: the capture started");
        return planetary;
    }

    [Fact(Timeout = 90_000)]
    public async Task TheCaptureRunsOnTheNodeAndTheMastersItStacksReachThisComputersView()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await GuiNodeHarness.StartAsync(output, ct);

        var planetary = await StartedAsync(h);

        h.Claimed(h.CameraUri).ShouldBeTrue("the node's capture holds the camera");
        h.Claimed(h.MountUri).ShouldBeFalse("its own nudges drive the mount, and would refuse themselves");
        planetary.Capture.Roi.ShouldBe((320, 200), "the window the node snapped to the camera's rule");
        h.AppState.Notifications.ShouldContain(static n => n.Message == "Planetary capture started (320x200, 10 ms)");
        h.Contexts.Local.LiveSession.Mode.ShouldBe(LiveSessionMode.Planetary);
        h.AppState.ActiveTab.ShouldBe(GuiTab.LiveSession);

        // The render thread's drive, until a master the node stacked is on show.
        await h.UntilAsync(() =>
        {
            planetary.Tick();
            return planetary.HasMaster;
        }, ct);
        var master = planetary.CurrentMaster.ShouldNotBeNull();
        (master.Width, master.Height, master.ChannelCount).ShouldBe((320, 200, 3), "the node's colour master at the window's size");
        await h.UntilAsync(() => planetary.Capture.FramesReceived > 0, ct);

        h.Post(new StopVideoCaptureSignal());

        await h.UntilAsync(() => !planetary.IsCapturing && !h.Claimed(h.CameraUri), ct);
        (await h.Local.Client.GetPlanetaryAsync(ct)).Value?.Running.ShouldNotBe(true, "the node's capture ended");
    }

    /// <summary>
    /// RAW is the camera NOW: the live frame the node samples at display rate, read from planetary/live. The view used to
    /// read the stack alone, so RAW showed the stack too and a live view for focusing trailed the camera by seconds (the
    /// ZWO live check, 2026-09-28).
    /// </summary>
    [Fact(Timeout = 90_000)]
    public async Task RawShowsTheCamerasLiveFrameAndStackShowsTheStack()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await GuiNodeHarness.StartAsync(output, ct);
        var planetary = await StartedAsync(h);
        planetary.ViewerState.ShowStacked = false;

        await h.UntilAsync(() =>
        {
            planetary.Tick();
            return planetary.Source is LiveFramePreviewSource;
        }, ct);
        var raw = planetary.Source.ShouldBeOfType<LiveFramePreviewSource>();
        (raw.Width, raw.Height).ShouldBe((320, 200), "the camera's frame at the window's size, not the stack");

        planetary.ViewerState.ShowStacked = true;
        await h.UntilAsync(() =>
        {
            planetary.Tick();
            return planetary.HasMaster;
        }, ct);
        planetary.Source.ShouldBeOfType<LiveStackPreviewSource>("STACK shows the node's rolling stack");

        h.Post(new StopVideoCaptureSignal());
        await h.UntilAsync(() => !planetary.IsCapturing, ct);
    }

    [Fact(Timeout = 90_000)]
    public async Task AControlOnThePanelReachesTheNodesCapture()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await GuiNodeHarness.StartAsync(output, ct);
        var planetary = await StartedAsync(h);

        planetary.Capture.SetRoiSize(160, 100);

        await h.UntilAsync(() => planetary.Capture.Roi == (160, 100), ct);
        var onTheNode = (await h.Local.Client.GetPlanetaryAsync(ct)).Value.ShouldNotBeNull();
        (onTheNode.RoiWidth, onTheNode.RoiHeight).ShouldBe((160, 100), "the node's capture reads the new window");

        h.Post(new StopVideoCaptureSignal());
        await h.UntilAsync(() => !planetary.IsCapturing, ct);
    }

    [Fact(Timeout = 60_000)]
    public async Task AStartOnACameraARunHoldsIsRefusedInTheRunsName()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await GuiNodeHarness.StartAsync(output, ct);
        h.Hub.TryAcquireLease(h.CameraUri, "the imaging session", out var claim).ShouldBeTrue();
        using (claim)
        {
            h.Post(new StartVideoCaptureSignal(RoiWidth: 320, RoiHeight: 200));
            await h.UntilSettledAsync(ct);

            h.ShouldHaveRefused("the imaging session");
            h.Planetary.IsCapturing.ShouldBeFalse();
            h.Contexts.Local.LiveSession.Mode.ShouldNotBe(LiveSessionMode.Planetary);
        }
    }

    [Fact(Timeout = 90_000)]
    public async Task AWindowThatGoesLeavesTheCaptureRunningOnTheNode()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await GuiNodeHarness.StartAsync(output, ct);
        var planetary = await StartedAsync(h);
        await h.UntilAsync(() => planetary.Capture.FramesReceived > 0, ct);

        await planetary.DisposeAsync();

        planetary.IsCapturing.ShouldBeFalse("this view stopped watching");
        var onTheNode = (await h.Local.Client.GetPlanetaryAsync(ct)).Value.ShouldNotBeNull();
        onTheNode.Running.ShouldBeTrue("a window that closes is not a stop");
        h.Claimed(h.CameraUri).ShouldBeTrue();

        (await h.Local.Client.StopPlanetaryAsync(ct)).IsSuccess.ShouldBeTrue();
    }
}
