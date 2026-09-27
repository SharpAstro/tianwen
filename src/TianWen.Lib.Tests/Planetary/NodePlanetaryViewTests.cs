using System;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Devices;
using TianWen.Lib.Imaging;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// This computer's view of its node's planetary capture (P6 of docs/plans/hardware-in-the-server.md, #936), the two halves
/// with no node: the panel's controls reach the node only as they change (<see cref="NodePlanetaryCapture"/>), since the
/// panel pushes its recenter every frame and that would be a request a frame; and the masters the node streams are shown
/// through the same source a SER playback is, each one the latest (<see cref="NodeMasters"/>). Over a real node:
/// <c>PlanetaryThroughTheNodeTests</c>.
/// </summary>
public class NodePlanetaryViewTests(ITestOutputHelper output)
{
    [Fact]
    public void AControlReachesTheNodeOnlyWhenItChanges()
    {
        var capture = new NodePlanetaryCapture();
        capture.TakeChanges().ShouldBeNull("nothing staged");

        capture.ConfigureRecenter(auto: true, mountJog: false, deadbandPixels: 2, gain: 0.5);
        var first = capture.TakeChanges().ShouldNotBeNull().Recenter.ShouldNotBeNull();
        (first.Auto, first.MountJog, first.DeadbandPixels, first.Gain).ShouldBe((true, false, 2, 0.5));
        capture.TakeChanges().ShouldBeNull("a change is sent once");

        // The panel's every frame: the same settings stage nothing.
        for (var frame = 0; frame < 3; frame++)
        {
            capture.ConfigureRecenter(auto: true, mountJog: false, deadbandPixels: 2, gain: 0.5);
        }
        capture.TakeChanges().ShouldBeNull("an unchanged panel sends nothing");
        capture.RecenterForStart.ShouldBeSameAs(first, "a start carries the recenter the panel shows");

        capture.ConfigureRecenter(auto: true, mountJog: false, deadbandPixels: 2, gain: 0.7);
        capture.TakeChanges().ShouldNotBeNull().Recenter.ShouldNotBeNull().Gain.ShouldBe(0.7);
    }

    [Fact]
    public void TheControlsStagedBetweenTwoSendsGoAsOneAndJogsAddUp()
    {
        var capture = new NodePlanetaryCapture();

        capture.SetExposure(TimeSpan.FromMilliseconds(5));
        capture.SetGain(120);
        capture.SetRoiSize(320, 200);
        capture.JogRoi(2, 0);
        capture.JogRoi(4, -2);

        var sent = capture.TakeChanges().ShouldNotBeNull();
        (sent.ExposureMs, sent.Gain, sent.RoiWidth, sent.RoiHeight, sent.JogX, sent.JogY).ShouldBe((5.0, (short?)120, 320, 200, 6, -2));
        sent.Recenter.ShouldBeNull("the recenter did not change");

        capture.JogRoi(3, 3);
        capture.JogRoi(-3, -3);
        capture.TakeChanges().ShouldBeNull("a jog there and back moves nothing, so nothing is sent");
    }

    [Fact]
    public void AStartDropsWhatWasStagedBeforeIt()
    {
        var capture = new NodePlanetaryCapture();
        capture.SetGain(1);

        capture.Began();

        capture.IsCapturing.ShouldBeTrue();
        capture.TakeChanges().ShouldBeNull("the start carried what it had; a stale control is not replayed onto the run");
        capture.Ended();
        capture.IsCapturing.ShouldBeFalse();
    }

    private static Image Master(int width, int height)
    {
        var plane = new float[height, width];
        plane[height / 2, width / 2] = 0.5f;
        return Image.FromChannel(plane, 1f, 0f);
    }

    [Fact(Timeout = 30_000)]
    public async Task TheLatestMasterFromTheNodeIsShownAndTheOneItReplacesIsGivenBack()
    {
        var ct = TestContext.Current.CancellationToken;
        var masters = new NodeMasters();
        var source = new LiveStackPreviewSource(masters, "node://planetary/master", new SystemTimeProvider(), FakeExternal.CreateLogger(output));
        try
        {
            source.TryPublishMaster().ShouldBeFalse();
            source.RequestFollowLatest();
            source.HasMaster.ShouldBeFalse("no master before the node sends one");

            var first = Master(32, 16);
            masters.Push(first);
            await UntilShownAsync(source, 32, ct);

            var second = Master(48, 24);
            masters.Push(second);
            first.TryLease(out _).ShouldBeFalse("the master the next one replaces is given back");
            await UntilShownAsync(source, 48, ct);
            source.DisplayMaster.ShouldNotBeSameAs(second, "the source shows a copy, which the next master cannot take back");
        }
        finally
        {
            await source.DisposeAsync();
        }
        masters.FrameCount.ShouldBe(2);
    }

    // The render thread's drive, as the controller's Tick: publish what has been built, THEN follow the latest (a follow
    // first would kick a new stack over a finished one), until a master of that width is on show.
    private static async Task UntilShownAsync(LiveStackPreviewSource source, int width, CancellationToken ct)
    {
        while (source.DisplayMaster?.Width != width)
        {
            source.TryPublishMaster();
            source.RequestFollowLatest();
            await Task.Delay(10, ct);
        }
    }

    [Fact]
    public void NodeMastersGiveTheirLastMasterBackWhenDisposed()
    {
        var masters = new NodeMasters();
        var held = Master(16, 16);
        masters.Push(held);

        masters.Dispose();

        held.TryLease(out _).ShouldBeFalse();
        masters.MasterAtAsync(0, CancellationToken.None).IsFaulted.ShouldBeTrue("there is nothing left to show");
    }
}
