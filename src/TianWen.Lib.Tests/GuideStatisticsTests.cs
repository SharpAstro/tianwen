using System;
using System.Collections.Immutable;
using System.IO;
using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.Lib.Sequencing;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Pins <see cref="GuideStatistics.OverExposure"/> and the FITS round-trip of the cards it feeds.
/// </summary>
/// <remarks>
/// The value of a per-sub guide RMS is that a stacker can reject the smeared subs, so the failure that
/// matters is not "the arithmetic is off by a percent" -- it is a number that is confidently wrong about
/// WHICH frame it describes, or that silently reports perfect guiding for a frame nobody guided. Both are
/// pinned below.
/// </remarks>
public class GuideStatisticsTests
{
    private static readonly DateTimeOffset T0 = new(2026, 6, 15, 22, 0, 0, TimeSpan.Zero);

    private static GuideErrorSample At(double seconds, double ra, double dec, bool settling = false)
        => new(T0.AddSeconds(seconds), ra, dec, IsSettling: settling);

    [Fact]
    public void OverExposure_UsesOnlySamplesInsideTheFramesOwnWindow()
    {
        // Three quiet samples inside a 10 s exposure, and a wild one just after it. The late sample is
        // the next sub's problem; if the window leaked it would triple the reported RMS.
        var samples = ImmutableArray.Create(
            At(1, 1.0, 0.0),
            At(5, -1.0, 0.0),
            At(9, 1.0, 0.0),
            At(11, 30.0, 30.0));

        var stats = GuideStatistics.OverExposure(samples, T0, TimeSpan.FromSeconds(10)).ShouldNotBeNull();

        stats.SampleCount.ShouldBe(3);
        stats.RmsRa.ShouldBe(1.0f, 1e-5f);
        stats.RmsDec.ShouldBe(0f, 1e-5f);
        stats.RmsTotal.ShouldBe(1.0f, 1e-5f);
        stats.Peak.ShouldBe(1.0f, 1e-5f);
    }

    [Fact]
    public void OverExposure_TotalIsTheQuadratureSumOfTheAxes()
    {
        var samples = ImmutableArray.Create(At(1, 3.0, 4.0), At(2, -3.0, -4.0));

        var stats = GuideStatistics.OverExposure(samples, T0, TimeSpan.FromSeconds(10)).ShouldNotBeNull();

        stats.RmsRa.ShouldBe(3.0f, 1e-5f);
        stats.RmsDec.ShouldBe(4.0f, 1e-5f);
        stats.RmsTotal.ShouldBe(5.0f, 1e-5f);
        stats.Peak.ShouldBe(5.0f, 1e-5f);
    }

    [Fact]
    public void OverExposure_PeakSurvivesAveraging()
    {
        // The failure RMS is worst at describing: one gust in an otherwise clean sub. RMS barely moves,
        // the peak is the only thing that says the frame has a trailed star in it.
        var quiet = new[] { At(1, 0.1, 0.1), At(2, -0.1, 0.1), At(3, 0.1, -0.1), At(4, -0.1, -0.1) };
        var withGust = ImmutableArray.Create([.. quiet, At(5, 6.0, 8.0)]);

        var calm = GuideStatistics.OverExposure(ImmutableArray.Create(quiet), T0, TimeSpan.FromSeconds(10)).ShouldNotBeNull();
        var gusty = GuideStatistics.OverExposure(withGust, T0, TimeSpan.FromSeconds(10)).ShouldNotBeNull();

        calm.Peak.ShouldBeLessThan(0.2f);
        gusty.Peak.ShouldBe(10.0f, 1e-4f);
        gusty.RmsTotal.ShouldBeGreaterThan(calm.RmsTotal);
    }

    [Fact]
    public void OverExposure_CountsSettlingSamplesRatherThanHidingThem()
    {
        // A live guiding display excludes settling samples because a dither is a commanded move, not an
        // error. Stamping a SUB inverts that: if the guider had not settled while the shutter was open,
        // the frame is smeared and the header exists to say so.
        var samples = ImmutableArray.Create(At(1, 0.1, 0.1), At(2, 5.0, 5.0, settling: true));

        var stats = GuideStatistics.OverExposure(samples, T0, TimeSpan.FromSeconds(10)).ShouldNotBeNull();

        stats.SampleCount.ShouldBe(2);
        stats.RmsTotal.ShouldBeGreaterThan(3f, "a settling sample inside the exposure must not be filtered out");
    }

    [Fact]
    public void OverExposure_IsNullWhenNothingWasMeasured_NotZero()
    {
        // Zero would claim perfect guiding. Absence must stay absence: unguided rig, empty history, and
        // a window the ring buffer no longer covers all land here.
        GuideStatistics.OverExposure([], T0, TimeSpan.FromSeconds(10)).ShouldBeNull();
        GuideStatistics.OverExposure(default, T0, TimeSpan.FromSeconds(10)).ShouldBeNull();
        GuideStatistics.OverExposure(
            ImmutableArray.Create(At(-500, 0.5, 0.5)), T0, TimeSpan.FromSeconds(10)).ShouldBeNull();
    }

    [Fact]
    public void OverExposure_SkipsNonFiniteSamples()
    {
        // A guider that failed to measure reports no error, not an error of zero. One NaN reaching the
        // sum makes the whole frame's RMS NaN, and every downstream comparison against it reads false.
        var samples = ImmutableArray.Create(
            At(1, 2.0, 0.0),
            At(2, double.NaN, 0.0),
            At(3, 0.0, 2.0));

        var stats = GuideStatistics.OverExposure(samples, T0, TimeSpan.FromSeconds(10)).ShouldNotBeNull();

        stats.SampleCount.ShouldBe(2);
        float.IsFinite(stats.RmsTotal).ShouldBeTrue();
        stats.RmsTotal.ShouldBe(2.0f, 1e-5f);
    }

    [Fact]
    public void GuidingStatsSurviveAFitsRoundTrip()
    {
        var dir = SharedTestData.CreateTempTestOutputDir(nameof(GuidingStatsSurviveAFitsRoundTrip));
        var path = Path.Combine(dir, "guided.fits");

        var guiding = new GuidingStats(RmsTotal: 0.62f, RmsRa: 0.41f, RmsDec: 0.46f, Peak: 1.73f, SampleCount: 42);
        var meta = new ImageMeta { FrameType = FrameType.Light, SensorType = SensorType.Monochrome, Guiding = guiding };
        var plane = new float[4, 4];
        var image = new Image([plane], BitDepth.Float32, maxValue: 1f, minValue: 0f, pedestal: 0f, imageMeta: meta);

        image.WriteToFitsFile(path);

        // Read back through the HEADER-ONLY path, which is what a calibration/ledger scan walks.
        Image.TryReadFitsHeader(path, out var info).ShouldBeTrue();
        var readBack = info.Meta.Guiding.ShouldNotBeNull();
        readBack.RmsTotal.ShouldBe(guiding.RmsTotal, 1e-4f);
        readBack.RmsRa.ShouldBe(guiding.RmsRa, 1e-4f);
        readBack.RmsDec.ShouldBe(guiding.RmsDec, 1e-4f);
        readBack.Peak.ShouldBe(guiding.Peak, 1e-4f);
        readBack.SampleCount.ShouldBe(42);
    }

    [Fact]
    public void AnUnguidedFrameWritesNoGuidingCardsAtAll()
    {
        // The absent card IS the "not known" signal, so it must really be absent -- a defaulted
        // GUIDERMS = 0 would read back as a perfectly guided frame.
        var dir = SharedTestData.CreateTempTestOutputDir(nameof(AnUnguidedFrameWritesNoGuidingCardsAtAll));
        var path = Path.Combine(dir, "unguided.fits");

        var meta = new ImageMeta { FrameType = FrameType.Light, SensorType = SensorType.Monochrome };
        var image = new Image([new float[4, 4]], BitDepth.Float32, maxValue: 1f, minValue: 0f, pedestal: 0f, imageMeta: meta);

        image.WriteToFitsFile(path);

        Image.TryReadFitsHeader(path, out var info).ShouldBeTrue();
        info.Meta.Guiding.ShouldBeNull();
    }
}
