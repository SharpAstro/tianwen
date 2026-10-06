using System;
using System.Linq;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Planetary;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// A capture whose frames are out of time order, as PIPP writes one sorted by quality (#1292): its span, its middle, the planet's turn
/// over it and the north its quarters agree on are read from its frames' TIMES, never from its first and last frames. The owner's
/// 2021-08-01 Saturn starts at 11:38:40 and ends at 11:35:38 of a run from 11:32:22 to 11:40:22, and its de-rotation took the limb
/// fit's north unread.
/// </summary>
public class PlanetaryFrameTimeOrderTests
{
    private static readonly DateTimeOffset Night = FrameDerotationCaptures.Night;
    private static readonly DiskPlacement Disk = FrameDerotationCaptures.Disk;

    private static float[][,] Blank(int frames) => [.. Enumerable.Range(0, frames).Select(_ => new float[4, 4])];

    // The capture's frames and times taken in `order`, as a capture sorted by quality holds them.
    private static (float[][,] Frames, DateTimeOffset[] Times) Reordered(float[][,] frames, DateTimeOffset[] times, int[] order)
        => ([.. order.Select(i => frames[i])], [.. order.Select(i => times[i])]);

    [Fact]
    public void ACapturesSpanAndMiddleAreItsTimesInWhateverOrderItsFramesCome()
    {
        DateTimeOffset[] times = [Night.AddMinutes(6), Night.AddMinutes(2), Night.AddMinutes(8), Night, Night.AddMinutes(5)];
        using var stream = new InMemoryFrameStream(Blank(times.Length), times);
        stream.CaptureSpan.ShouldBe((Night, Night.AddMinutes(8)));
        stream.MidCapture.ShouldBe(Night.AddMinutes(4), "not 5.5, halfway between its first frame and its last");

        using var untimed = new InMemoryFrameStream(Blank(2));
        untimed.CaptureSpan.ShouldBeNull();
        untimed.MidCapture.ShouldBeNull();
    }

    [Fact]
    public void ThePlanetsTurnIsReadOverTheCapturesSpanNotItsFirstAndLastFrames()
    {
        var capture = FrameDerotationCaptures.Capture(frames: 5, minutes: 16, seed: 3);
        var (frames, times) = Reordered(capture.Frames, capture.Times, [2, 4, 0, 3, 1]);
        using var ordered = new InMemoryFrameStream(capture.Frames, capture.Times);
        using var sorted = new InMemoryFrameStream(frames, times);
        var frame = Image.FromChannel(capture.Frames[0], 1f, 0f);

        FrameDerotator.TurnAtCentrePx(sorted, CatalogIndex.Jupiter, frame)
            .ShouldBe(FrameDerotator.TurnAtCentrePx(ordered, CatalogIndex.Jupiter, frame), 1e-9);
    }

    [Fact(Timeout = 300_000)]
    public async Task ACaptureSortedByQualityIsDerotatedToItsMiddleWithTheNorthItsQuartersAgreeOn()
    {
        // 16 minutes in a fixed shuffle: its first frame was taken at 4.8 minutes and its last at 12. The north is the capture's to
        // decide (the fixture's limb fit has it upside down, ACaptureStackedAcrossSixteenMinutesIsThePlanetAtItsMiddle), and taken
        // between the first and last frames the quarters held the wrong frames.
        var capture = FrameDerotationCaptures.Capture(frames: 21, minutes: 16, seed: 5);
        int[] order = [6, 13, 2, 19, 9, 0, 16, 4, 11, 20, 7, 1, 14, 18, 3, 10, 17, 5, 12, 8, 15];
        var (frames, times) = Reordered(capture.Frames, capture.Times, order);
        using var stream = new InMemoryFrameStream(frames, times);
        var options = new PlanetaryStackOptions
        {
            KeepFraction = 1, WhitenedCorrelation = false, Interpolation = WarpInterpolation.Lanczos3, ReferenceFrames = 0,
            Derotation = new PlanetaryDerotationOptions(CatalogIndex.Jupiter),
        };

        var result = await new LuckyImagingStacker().StackGlobalAsync(stream, options, TestContext.Current.CancellationToken);

        result.Epoch.ShouldBe(Night.AddMinutes(8));
        result.NorthUnread.ShouldBeFalse();
        var north = result.North.ShouldNotBeNull();
        double.IsNaN(north.AgreementAsFitted).ShouldBeFalse();
        double.IsNaN(north.AgreementTurnedOver).ShouldBeFalse();
        Math.IEEERemainder(north.NorthAngleDeg - Disk.NorthAngleDeg, 360).ShouldBe(0, 5);
        result.Master.ImageMeta.ExposureStartTime.ShouldBe(Night, "the master's DATE-OBS is the capture's earliest frame");
        result.Master.ImageMeta.ExposureDuration.ShouldBe(TimeSpan.FromMinutes(16));
    }

    [Fact(Timeout = 300_000)]
    public async Task ARunTooShortToTellItsNorthIsStackedAsTakenUnlessADerotationIsAskedForWhateverTheTurn()
    {
        // One minute turns Jupiter 0.6 degrees, under the degree its quarters need to tell its north, yet moves the 24 px disk's
        // middle 0.25 px: worth de-rotating at a least turn of 0.1 px, and not to be done on the limb fit's north alone.
        var capture = FrameDerotationCaptures.Capture(frames: 5, minutes: 1, seed: 4);
        using var stream = new InMemoryFrameStream(capture.Frames, capture.Times);
        var stacker = new LuckyImagingStacker();
        var options = new PlanetaryStackOptions { KeepFraction = 1, ReferenceFrames = 0 };

        var unread = await stacker.StackGlobalAsync(stream,
            options with { Derotation = new PlanetaryDerotationOptions(CatalogIndex.Jupiter) { MinimumTurnPx = 0.1 } }, TestContext.Current.CancellationToken);
        unread.NorthUnread.ShouldBeTrue();
        unread.Epoch.ShouldBeNull();
        unread.North.ShouldBeNull();
        unread.TurnPx.ShouldNotBeNull().ShouldBeGreaterThan(0.1);

        // Asked for whatever the turn, the de-rotation keeps the limb fit's north and says the quarters could not read it.
        var asked = await stacker.StackGlobalAsync(stream,
            options with { Derotation = new PlanetaryDerotationOptions(CatalogIndex.Jupiter) { MinimumTurnPx = 0 } }, TestContext.Current.CancellationToken);
        asked.NorthUnread.ShouldBeFalse();
        asked.Epoch.ShouldBe(Night.AddMinutes(0.5));
        double.IsNaN(asked.North.ShouldNotBeNull().AgreementAsFitted).ShouldBeTrue();
    }

    [Fact]
    public void ASequenceRefusesCapturesSortedByQualityWhoseSpansOverlap()
    {
        // Read by first and last frames, these two seemed apart (the later's first frame at 3 minutes, the earlier's last at 1);
        // their spans overlap from 1.5 to 2 minutes.
        using var earlier = new InMemoryFrameStream(Blank(3), [Night.AddMinutes(2), Night, Night.AddMinutes(1)]);
        using var later = new InMemoryFrameStream(Blank(3), [Night.AddMinutes(3), Night.AddMinutes(1.5), Night.AddMinutes(2.5)]);
        Should.Throw<ArgumentException>(() => new PlanetaryFrameSequence([later, earlier]));

        using var apart = new InMemoryFrameStream(Blank(2), [Night.AddMinutes(4), Night.AddMinutes(3.5)]);
        using var sequence = new PlanetaryFrameSequence([apart, earlier]);
        sequence.CaptureSpan.ShouldBe((Night, Night.AddMinutes(4)));
        sequence.TimestampOf(0).ShouldBe(Night.AddMinutes(2), "the earlier capture first, its frames as it holds them");
    }

    [Fact]
    public void TheCaptureStatisticsRefuseFramesOutOfTimeOrder()
    {
        using var ordered = new InMemoryFrameStream(Blank(3), [Night, Night.AddSeconds(1), Night.AddSeconds(2)]);
        PlanetaryCaptureStatistics.Seconds(ordered, 3, null).ShouldBe([0.0, 1.0, 2.0]);

        using var sorted = new InMemoryFrameStream(Blank(3), [Night.AddSeconds(2), Night, Night.AddSeconds(1)]);
        Should.Throw<InvalidOperationException>(() => PlanetaryCaptureStatistics.Seconds(sorted, 3, null));
    }
}
