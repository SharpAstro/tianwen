using System;
using System.Collections.Immutable;
using System.Linq;
using Shouldly;
using TianWen.Lib.Imaging.Planetary;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// A twin measured against its capture (<see cref="TwinComparison"/>, docs/plans/planetary-stacking.md, A1): the one routine
/// <c>planetary degrade</c> prints and <c>planetary twin</c>'s search minimises, so the mismatch must move with the fitted statistics
/// alone, cost a ratio high and low alike, and leave out what either capture could not measure.
/// </summary>
public class TwinComparisonTests
{
    private static FrameLimb Limb(int frame) => new FrameLimb(frame, 0.03 + (frame * 1e-5), 6.9 + (frame * 1e-4), new LimbFit(
        158.89 + (frame * 1e-3), 161.31, 119.34, 22.31, 0.989, 1.666, 0.671, 0.0062, 1, 0.0123, 39573,
        [.. Enumerable.Repeat(double.NaN, 13)], 12, true, 0.202, -0.474, 0.0022, 202.31, 0.4995, 6.53));

    // A real capture's statistics as CaptureStatisticsFileTests builds them; its outermost halo annulus is unmeasured (NaN).
    private static CaptureStatistics Real() => new CaptureStatistics(
        160, 29.9, 7, 158.6, 158.8, 114.6, [], [], [], [], 0.35, 0.01, 0.02, [], 0.023, 0.0026, 14.0, 13.1,
        null, null, [.. Enumerable.Range(0, 40).Select(i => Limb(i * 4))], 0.36, 1.1, 0.05, 0, [0.38, 0.16, 0.021, double.NaN],
        new WarpStatistics(163, 1.06, 3, WarpLengthBound.AtMost, double.NaN, []), [], [0.97, 0.99, 1, 1.02, 1.06], 0.84,
        [new BandNoise(1, 0.57, 4.87)], new CameraEstimate(255, 1.25, 0.65, 113, 1.09, 0.54, 1.09));

    [Fact]
    public void ATwinThatIsItsCaptureMatchesOnEveryStatistic()
    {
        var real = Real();
        var rows = TwinComparison.Compare(real, real);
        rows.Where(r => double.IsFinite(r.Ratio)).ShouldAllBe(r => r.Ratio == 1);
        var (mismatch, used) = TwinComparison.Mismatch(rows);
        mismatch.ShouldBe(0);
        // The limb's motion, the aligner's error, both edge widths, three halo annuli (the fourth unmeasured), both flux parts and
        // the one band's noise on the disk.
        used.ShouldBe(10);
    }

    [Theory]
    [InlineData(1.1)]
    [InlineData(1 / 1.1)]
    public void AFittedStatisticOffByARatioCostsItsSquaredLogWhicheverWay(double ratio)
    {
        var real = Real();
        var twin = real with { LimbWidthBest = real.LimbWidthBest * ratio };
        var (mismatch, used) = TwinComparison.Mismatch(TwinComparison.Compare(real, twin));
        mismatch.ShouldBe(Math.Log(1.1) * Math.Log(1.1), 1e-12);
        used.ShouldBe(10);
    }

    [Fact]
    public void AStatisticTheTwinCouldNotMeasureCostsMoreThanOneItMatchedBadly()
    {
        // #1413: a row either side read as not positive was dropped and the mean taken over the rest, so a trial whose twin pushed a halo
        // annulus (a level less the sky, near zero at its outer edge) below zero lost that row instead of paying for it, and scored better
        // than one that measured it twice too bright. The real capture alone decides the rows, the same for every trial; a twin that cannot
        // measure one of them is off by more than any it can.
        var real = Real();
        var twiceTooBright = real with { Halo = [real.Halo[0], real.Halo[1], 2 * real.Halo[2], double.NaN] };
        var belowTheSky = real with { Halo = [real.Halo[0], real.Halo[1], -0.001, double.NaN] };

        var (bright, brightUsed) = TwinComparison.Mismatch(TwinComparison.Compare(real, twiceTooBright));
        var (lost, lostUsed) = TwinComparison.Mismatch(TwinComparison.Compare(real, belowTheSky));

        lostUsed.ShouldBe(brightUsed, "the real capture decides which rows count");
        lost.ShouldBeGreaterThan(bright);
        PlanetaryTwinCalibration.MeanMismatch(TwinComparison.Compare(real, belowTheSky))
            .ShouldBeGreaterThan(PlanetaryTwinCalibration.MeanMismatch(TwinComparison.Compare(real, twiceTooBright)));
    }

    [Fact]
    public void AReportedStatisticNeverMovesTheMismatch()
    {
        // The aligner's shift RMS and the Laplacian's lag 1 are reported, not fitted: R2 found the latter to be noise on 8 bits.
        var real = Real();
        var twin = real with { SeeingRms = real.SeeingRms * 2, QualityLag1 = real.QualityLag1 / 3 };
        var rows = TwinComparison.Compare(real, twin);
        rows.Single(r => r.Name.StartsWith("shift RMS", StringComparison.Ordinal)).Within().ShouldBeFalse();
        TwinComparison.Mismatch(rows).Mismatch.ShouldBe(0);
    }

    [Fact]
    public void TheMapForACaptureIsTheNearestYearsFilterNearestItsWavelength()
    {
        static PlanetMap Map(float level) => new PlanetMap([.. Enumerable.Repeat(level, 8)], 4, 2);
        static OpalFilter Filter(string name, double pivotNm) => new OpalFilter(name, pivotNm, 0.9, 1);
        ImmutableArray<OpalApparition> apparitions =
        [
            new OpalApparition(2021, [Filter("F395N", 395), Filter("F631N", 631)], [[Map(1)], [Map(2)]]),
            new OpalApparition(2023, [Filter("F395N", 395), Filter("F502N", 502), Filter("F631N", 631)], [[Map(3)], [Map(4)], [Map(5)]]),
        ];

        var red = OpalMaps.ForCapture(apparitions, 2023, 610).ShouldNotBeNull();
        (red.Year, red.Filter.Name).ShouldBe((2023, "F631N"));
        // A year the maps do not cover takes the nearest apparition, the later on a tie (2022 is one from each).
        var tie = OpalMaps.ForCapture(apparitions, 2022, 530).ShouldNotBeNull();
        (tie.Year, tie.Filter.Name).ShouldBe((2023, "F502N"));
        (OpalMaps.ForCapture(apparitions, 2020, 400)?.Year).ShouldBe(2021);
        OpalMaps.ForCapture([], 2023, 610).ShouldBeNull();
    }
}
