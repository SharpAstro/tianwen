using NSubstitute;
using Shouldly;
using TianWen.Hosting;
using TianWen.Hosting.Dto;
using TianWen.Lib.Imaging;
using TianWen.Lib.Sequencing;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Pins <see cref="NodeFrames"/> (P5 part 2 of docs/plans/hardware-in-the-server.md, #934): which frame a source shows,
/// whoever produced it, and the node's one token per source, which moves exactly when the frame on show changes.
/// </summary>
public class NodeFramesTests
{
    private static (NodeFrames Frames, IHostedSession Hosted) Build()
    {
        var hosted = Substitute.For<IHostedSession>();
        return (new NodeFrames(hosted), hosted);
    }

    private static ISession SessionShowing(params Image?[] frames)
    {
        var session = Substitute.For<ISession>();
        session.LastCapturedImages.Returns(frames);
        return session;
    }

    private static Image Frame() => TestFrames.BufferedMono(out _);

    [Fact]
    public void TheTokenMovesOnlyWhenTheFrameOnShowChanges()
    {
        var (frames, hosted) = Build();
        var a = Frame();
        var b = Frame();
        var session = SessionShowing(a);
        hosted.CurrentSession.Returns(session);
        hosted.RunningKind.Returns(NodeRunKind.Session);

        frames.Ota(0).ShouldBe(new NodeFrames.Shown(a, 1));
        frames.Ota(0).ShouldBe(new NodeFrames.Shown(a, 1), "the same frame keeps its token, so a client holding it gets a 204");

        session.LastCapturedImages.Returns([b]);
        frames.Ota(0).ShouldBe(new NodeFrames.Shown(b, 2));

        session.LastCapturedImages.Returns([null]);
        frames.Ota(0).ShouldBe(new NodeFrames.Shown(null, 3), "a frame gone is a change too");
    }

    [Fact]
    public void APreviewAfterASessionIsANewFrameWhateverTheSessionNumberedItsOwn()
    {
        // The session numbered its last frame 1, and a preview slot would number its first 1 too: a client holding the
        // session's frame would then be answered "unchanged" for the preview. The token is the node's, so it moves.
        var (frames, hosted) = Build();
        var sub = Frame();
        var session = SessionShowing(sub);
        hosted.CurrentSession.Returns(session);
        frames.Ota(0).ShouldBe(new NodeFrames.Shown(sub, 1));

        var preview = Frame();
        frames.PublishPreview(0, preview);

        frames.Ota(0).ShouldBe(new NodeFrames.Shown(preview, 2));
    }

    [Fact]
    public void ASessionGoingOnShowsItsOwnFrameAndAPreviewTakenBeforeItIsGivenBack()
    {
        var (frames, hosted) = Build();
        var preview = TestFrames.BufferedMono(out var previewBuffer);
        frames.PublishPreview(0, preview);
        frames.Ota(0).Frame.ShouldBeSameAs(preview);

        var sub = Frame();
        var session = SessionShowing(sub);
        hosted.CurrentSession.Returns(session);
        hosted.RunningKind.Returns(NodeRunKind.Session);
        frames.Ota(0).Frame.ShouldBeSameAs(sub);

        // The session has ended: its last frame is newer than the preview, which is stale and goes back.
        hosted.RunningKind.Returns((NodeRunKind?)null);
        frames.Ota(0).Frame.ShouldBeSameAs(sub);
        previewBuffer.IsReleased.ShouldBeTrue("a preview a run has superseded is given back, not kept");
    }

    [Fact]
    public void APreviewTakenSinceTheLastRunIsWhatTheOtaShows()
    {
        var (frames, hosted) = Build();
        var sub = Frame();
        var session = SessionShowing(sub);
        hosted.CurrentSession.Returns(session);

        var preview = Frame();
        frames.PublishPreview(0, preview);

        frames.Ota(0).Frame.ShouldBeSameAs(preview);
        frames.Ota(1).Frame.ShouldBeNull("an OTA with neither shows nothing");
        frames.OtaCount.ShouldBe(1);
    }

    [Fact]
    public void AReplacedPreviewIsGivenBack()
    {
        var (frames, _) = Build();
        var first = TestFrames.BufferedMono(out var firstBuffer);
        frames.PublishPreview(0, first);

        frames.PublishPreview(0, Frame());

        firstBuffer.IsReleased.ShouldBeTrue();
    }

    [Fact]
    public void ARunsOwnSourceShowsWhatItPublishedAndGivesBackWhatThatReplaced()
    {
        var (frames, hosted) = Build();
        var run = Substitute.For<INodeRun>();
        hosted.CurrentSession.Returns((ISession?)null);
        hosted.CurrentRun.Returns(run);
        var first = TestFrames.BufferedMono(out var firstBuffer);
        frames.Publish(FrameSources.PlanetaryLive, first);

        frames.Named(FrameSources.PlanetaryLive).ShouldBe(new NodeFrames.Shown(first, 1));
        frames.Named(FrameSources.PlanetaryMaster).Frame.ShouldBeNull("each source is its own");

        var second = Frame();
        frames.Publish(FrameSources.PlanetaryLive, second);

        frames.Named(FrameSources.PlanetaryLive).ShouldBe(new NodeFrames.Shown(second, 2));
        firstBuffer.IsReleased.ShouldBeTrue("a replaced frame is given back, not kept");
    }

    [Fact]
    public void ARunsOwnFrameIsGivenBackOnceTheNodesNextRunStarts()
    {
        var (frames, hosted) = Build();
        var capture = Substitute.For<INodeRun>();
        var next = Substitute.For<INodeRun>();
        // A run of another kind has no session (a substitute would make one up, and it would never change).
        hosted.CurrentSession.Returns((ISession?)null);
        hosted.CurrentRun.Returns(capture);
        var master = TestFrames.BufferedMono(out var masterBuffer);
        frames.Publish(FrameSources.PlanetaryMaster, master);
        frames.Named(FrameSources.PlanetaryMaster).Frame.ShouldBeSameAs(master, "shown after its run has ended too");

        hosted.CurrentRun.Returns(next);

        frames.Named(FrameSources.PlanetaryMaster).Frame.ShouldBeNull("the next run shows nothing of the last one's");
        masterBuffer.IsReleased.ShouldBeTrue();
    }

    [Fact]
    public void TheGuiderShowsTheSessionsGuideFrame()
    {
        var (frames, hosted) = Build();
        var guide = Frame();
        var session = SessionShowing();
        session.LastGuideFrame.Returns(guide);
        hosted.CurrentSession.Returns(session);

        frames.Guider().ShouldBe(new NodeFrames.Shown(guide, 1));
    }
}
