using System;
using Shouldly;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Astrometry.VSOP87;
using TianWen.Lib.Imaging.Planetary;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// What a planetary find learns from its solve (#1684), on the owner's Saturn night: the frames solved at 0.2316"/px through
/// 2.0 um photosites, which is 1780 mm where the header said 2400, and the mount was 8.7 degrees off.
/// </summary>
public class PlanetaryFindResultTests
{
    private static readonly DateTimeOffset Night = new DateTimeOffset(2026, 10, 7, 11, 30, 0, TimeSpan.Zero);

    private static WCS SolvedAt(double raHours, double decDeg, double scaleArcsec)
        => new WCS(raHours, decDeg) { CRPix1 = 960, CRPix2 = 540, CD1_1 = -scaleArcsec / 3600, CD1_2 = 0, CD2_1 = 0, CD2_2 = -scaleArcsec / 3600 };

    [Fact]
    public void TheScalesFocalLengthIsOfferedOnlyWhenItIsOffTheProfilesByMoreThanTheTolerance()
    {
        var solved = SolvedAt(1, 2, 0.2316);
        var offered = PlanetaryFindResult.From(solved, double.NaN, double.NaN, 2.0, 2400, null, Night);
        offered.MeasuredFocalLengthMm.ShouldNotBeNull().ShouldBe(1781, 1);
        offered.FocalLengthToOffer.ShouldBe(1781);

        PlanetaryFindResult.From(solved, double.NaN, double.NaN, 2.0, 1790, null, Night).FocalLengthToOffer
            .ShouldBeNull("half a percent is the solve's own spread, not a barlow");
        PlanetaryFindResult.From(solved, double.NaN, double.NaN, 2.0, null, null, Night).FocalLengthToOffer
            .ShouldBe(1781, "a profile naming none is offered one");
        PlanetaryFindResult.From(solved, double.NaN, double.NaN, double.NaN, 2400, null, Night).FocalLengthToOffer
            .ShouldBeNull("no photosite, no focal length");
    }

    [Fact]
    public void TheMountsOffsetIsTheSeparationFromWhereItSaidItPointed()
    {
        var result = PlanetaryFindResult.From(SolvedAt(0, 0, 0.2316), 0, 1, 2.0, 2400, null, Night);

        result.MountOffsetDeg.ShouldNotBeNull().ShouldBe(1, 1e-9);
        PlanetaryFindResult.From(SolvedAt(0, 0, 0.2316), double.NaN, double.NaN, 2.0, 2400, null, Night).MountOffsetDeg.ShouldBeNull();
    }

    [Fact]
    public void APlanetAtTheSolvedCentreIsInTheFieldAndOneADegreeAwayIsNot()
    {
        VSOP87a.ReduceJ2000(CatalogIndex.Saturn, Night, out var ra, out var dec, out _).ShouldBeTrue();

        var onIt = PlanetaryFindResult.From(SolvedAt(ra, dec, 0.2316), double.NaN, double.NaN, 2.0, 2400, CatalogIndex.Saturn, Night);
        onIt.PlanetSeparationDeg.ShouldNotBeNull().ShouldBeLessThan(1e-6);
        onIt.PlanetInField(1920, 1080).ShouldBeTrue();

        var away = PlanetaryFindResult.From(SolvedAt(ra, dec + 1, 0.2316), double.NaN, double.NaN, 2.0, 2400, CatalogIndex.Saturn, Night);
        away.PlanetSeparationDeg.ShouldNotBeNull().ShouldBe(1, 1e-6);
        away.PlanetInField(1920, 1080).ShouldBeFalse("the field is 0.07 degrees from its centre to a corner");
        away.InWords().ShouldContain("Saturn is 1.00 deg away");
    }
}
