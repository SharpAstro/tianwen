using System;
using Shouldly;
using TianWen.Lib.Devices.DAL;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// How long a DAL video stream may go without a frame before it counts as stalled (<see cref="VideoSilenceWatch"/>): the
/// exposure's limit, held at the longer exposure across a change (review round 2 of #1605, question 1), and the stream's own
/// recent silences, capped and aging out, for a stream whose frame period is its readout (question 2).
/// </summary>
public class VideoSilenceWatchTests
{
    private static readonly TimeSpan FiveMs = TimeSpan.FromMilliseconds(5);

    private static VideoSilenceWatch Watch(TimeSpan exposure, TimeSpan silence, int frames)
    {
        var watch = new VideoSilenceWatch(exposure);
        for (var frame = 0; frame < frames; frame++)
        {
            watch.Frame(silence);
        }
        return watch;
    }

    [Fact]
    public void AFastStreamIsAllowedTwoSecondsAndALongExposureTwiceItselfPlusOne()
    {
        Watch(FiveMs, TimeSpan.Zero, 10).Limit.ShouldBe(TimeSpan.FromSeconds(2));
        Watch(TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(20), 3).Limit.ShouldBe(TimeSpan.FromSeconds(41));
    }

    [Fact]
    public void BeforeItsFirstFrameAStreamIsAllowedTheCeiling()
    {
        var watch = new VideoSilenceWatch(FiveMs);
        watch.Limit.ShouldBe(VideoSilenceWatch.IntervalCeiling, "nothing yet says how long its frames take");
        VideoSilenceWatch.IntervalCeiling.ShouldBe(TimeSpan.FromSeconds(15));

        watch.Frame(TimeSpan.Zero);
        watch.Limit.ShouldBe(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void ALoweredExposureIsHeldUntilTwoFramesHaveArrivedSince()
    {
        var watch = Watch(TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(20), 3);

        watch.ExposureChanged(FiveMs);
        watch.Limit.ShouldBe(TimeSpan.FromSeconds(41), "the frame in flight may come at the old exposure");
        watch.Frame(TimeSpan.FromSeconds(20));
        watch.Limit.ShouldBe(TimeSpan.FromSeconds(41), "one frame since the change could still be the old exposure's");
        watch.Frame(TimeSpan.FromMilliseconds(250));
        watch.Limit.ShouldBe(VideoSilenceWatch.IntervalCeiling, "the hold is over, and no frame of the new exposure is counted yet");
        watch.Frame(TimeSpan.Zero);
        watch.Limit.ShouldBe(TimeSpan.FromSeconds(2), "the new exposure alone, the old frames forgotten");
    }

    [Fact]
    public void AChangeWhileOneIsHeldKeepsTheLongestAndARaisedExposureNeedsNoHold()
    {
        var watch = Watch(TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(20), 3);
        watch.ExposureChanged(TimeSpan.FromSeconds(5));
        watch.ExposureChanged(FiveMs);
        watch.Limit.ShouldBe(TimeSpan.FromSeconds(41), "the 20 s frame may still be in flight");

        var raised = Watch(FiveMs, TimeSpan.Zero, 10);
        raised.ExposureChanged(TimeSpan.FromSeconds(10));
        raised.Limit.ShouldBe(TimeSpan.FromSeconds(21));
    }

    [Fact]
    public void AReadoutBoundStreamIsAllowedTwiceItsLongestRecentSilencePlusASecond()
        => Watch(FiveMs, TimeSpan.FromSeconds(3), 3).Limit.ShouldBe(TimeSpan.FromSeconds(7));

    [Fact]
    public void OneSlowFrameWidensTheLimitForTheNextFramesOnly()
    {
        var watch = Watch(FiveMs, TimeSpan.Zero, 20);
        watch.Frame(TimeSpan.FromSeconds(5));
        watch.Limit.ShouldBe(TimeSpan.FromSeconds(11));

        for (var frame = 1; frame < VideoSilenceWatch.RecentFrames; frame++)
        {
            watch.Frame(TimeSpan.Zero);
        }
        watch.Limit.ShouldBe(TimeSpan.FromSeconds(11), "still among the last eight");
        watch.Frame(TimeSpan.Zero);
        watch.Limit.ShouldBe(TimeSpan.FromSeconds(2), "aged out");
    }

    [Fact]
    public void TheFramesNeverWidenItPastTheCeiling()
        => Watch(FiveMs, TimeSpan.FromSeconds(10), 3).Limit.ShouldBe(VideoSilenceWatch.IntervalCeiling);

    [Fact]
    public void ANewSizeDepthOrReadoutModeForgetsTheFrames()
    {
        var watch = Watch(FiveMs, TimeSpan.FromSeconds(3), 3);

        watch.ModeChanged();
        watch.Limit.ShouldBe(VideoSilenceWatch.IntervalCeiling);
        watch.Frame(TimeSpan.Zero);
        watch.Limit.ShouldBe(TimeSpan.FromSeconds(2));
    }
}
