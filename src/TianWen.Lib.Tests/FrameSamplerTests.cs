using System;
using Shouldly;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Pins <see cref="FrameSampler"/> (P5 part 5 of docs/plans/hardware-in-the-server.md, #934): at most one copy of a
/// borrowed video frame per interval, a copy that is the frame's and outlives it, and planes a steady stream recycles.
/// </summary>
public class FrameSamplerTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(100);

    private static readonly DateTimeOffset Arrived = new DateTimeOffset(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TheFirstFrameIsSampledAndTheNextOnlyOnceTheIntervalHasPassed()
    {
        var time = new FakeTimeProviderWrapper();
        var sampler = new FrameSampler(time, Interval, nameof(FrameSamplerTests));
        var frame = TestFrames.BufferedMono(out _);

        sampler.TrySample(frame, Arrived, out var first).ShouldBeTrue();
        time.Advance(Interval - TimeSpan.FromMilliseconds(1));
        sampler.TrySample(frame, Arrived, out _).ShouldBeFalse("a frame within the interval is not copied");
        time.Advance(TimeSpan.FromMilliseconds(1));
        sampler.TrySample(frame, Arrived, out var second).ShouldBeTrue();

        first.ShouldNotBeSameAs(second);
        first.Release();
        second.Release();
    }

    [Fact]
    public void ASampleIsTheFramesPixelsAndLabelsAndOutlivesIt()
    {
        var sampler = new FrameSampler(new FakeTimeProviderWrapper(), Interval, nameof(FrameSamplerTests));
        var frame = TestFrames.BufferedMono(out var buffer, clobberOnRecycle: true);
        var pixels = frame.GetChannelSpan(0).ToArray();
        var (width, height, max, min, depth, meta) = (frame.Width, frame.Height, frame.MaxValue, frame.MinValue, frame.BitDepth, frame.ImageMeta);

        sampler.TrySample(frame, Arrived, out var sample).ShouldBeTrue();
        frame.Release();

        buffer.IsReleased.ShouldBeTrue("the frame was borrowed, and goes back as it would have");
        (sample.Width, sample.Height, sample.MaxValue, sample.MinValue, sample.BitDepth, sample.ImageMeta)
            .ShouldBe((width, height, max, min, depth, meta with { ExposureStartTime = Arrived }), "an undated frame is dated when it arrived");
        sample.GetChannelSpan(0).ToArray().ShouldBe(pixels, "the sample is a copy, not a view of the camera's buffer");
        sample.Release();
    }

    [Fact]
    public void ASteadyStreamRecyclesItsPlanes()
    {
        var time = new FakeTimeProviderWrapper();
        var sampler = new FrameSampler(time, Interval, nameof(FrameSamplerTests));
        var frame = TestFrames.BufferedMono(out _);

        for (var i = 0; i < 10; i++)
        {
            sampler.TrySample(frame, Arrived, out var sample).ShouldBeTrue();
            sample.Release();
            time.Advance(Interval);
        }

        sampler.PlanesAllocated.ShouldBe(1, "each released sample's plane is the next one's");
    }

    [Fact]
    public void AnUndatedFrameIsDatedWhenItArrivedAndADatedOneKeepsItsOwn()
    {
        // No driver stamps a video frame, so without this a live view could not tell a fresh frame from a stale one.
        var sampler = new FrameSampler(new FakeTimeProviderWrapper(), TimeSpan.Zero, nameof(FrameSamplerTests));
        var undated = TestFrames.BufferedMono(out _);
        undated.ImageMeta.ExposureStartTime.ShouldBe(default);

        sampler.TrySample(undated, Arrived, out var stamped).ShouldBeTrue();
        stamped.ImageMeta.ExposureStartTime.ShouldBe(Arrived);
        stamped.ImageMeta.ShouldBe(undated.ImageMeta with { ExposureStartTime = Arrived }, "nothing else of the metadata changes");

        sampler.TrySample(stamped, Arrived.AddHours(1), out var kept).ShouldBeTrue();
        kept.ImageMeta.ExposureStartTime.ShouldBe(Arrived, "a frame's own start time is never replaced");
        stamped.Release();
        kept.Release();
        undated.Release();
    }
}
