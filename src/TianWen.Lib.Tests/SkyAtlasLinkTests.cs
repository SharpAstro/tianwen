using Shouldly;
using System;
using TianWen.Lib.Astrometry;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Pins the share-link vocabulary (P20): what the desktop viewer writes into a URL and what the web
/// Sky Atlas reads back out of one.
/// </summary>
/// <remarks>
/// The unit conversion is the whole reason this file exists. RA is in HOURS everywhere inside the
/// codebase and in DEGREES on the wire, and both readings of any given number are a legal RA -- so a
/// missing or doubled x15 points fifteen times away and produces a link that looks perfectly normal.
/// </remarks>
public class SkyAtlasLinkTests
{
    // M42, from SIMBAD: 05h 35m 17.3s / -05d 23' 28". In degrees that is 83.822, which is what a
    // reader pasting this link into Aladin or WWT would expect to find.
    private const double OrionRaHours = 5.588139;
    private const double OrionDecDeg = -5.391111;

    [Fact]
    public void RightAscensionIsWrittenInDegrees()
    {
        var link = SkyAtlasLink.For(OrionRaHours, OrionDecDeg);

        link.ShouldContain("ra=83.822085", customMessage: "hours x 15; the same number in hours would be a legal RA elsewhere in the sky");
        link.ShouldContain("dec=-5.391111");
    }

    /// <summary>
    /// The whole URL, once, exactly. Every other test here checks one part of the format; this one is
    /// the format, and it is what keeps <c>TianWen.UI.Web.E2E</c>'s <c>ShareLinkTests</c> in step --
    /// that project is deliberately reference-free (it needs a browser, not the UI stack), so it
    /// cannot call the writer and carries the same literal instead. If this assertion fails because
    /// the format changed on purpose, that literal is the other place to change.
    /// </summary>
    [Fact]
    public void TheWholeLinkIsThisExactly()
        => SkyAtlasLink.For(OrionRaHours, OrionDecDeg, 2.5,
                new DateTimeOffset(2026, 1, 18, 23, 26, 51, TimeSpan.Zero))
            .ShouldBe("https://sharpastro.github.io/tianwen/?view=sky&ra=83.822085&dec=-5.391111&fov=2.5000&t=2026-01-18T23:26:51Z");

    [Fact]
    public void ALinkOpensTheAtlas()
        => SkyAtlasLink.For(OrionRaHours, OrionDecDeg)
            .ShouldStartWith("https://sharpastro.github.io/tianwen/?view=sky&",
                customMessage: "a pointing means nothing on the planner view");

    [Fact]
    public void RightAscensionWrapsRatherThanRunningOffTheEnd()
    {
        // A solve near the RA seam legitimately answers just outside [0, 24).
        SkyAtlasLink.For(24.02, 0).ShouldContain("ra=0.300000");
        SkyAtlasLink.For(-0.02, 0).ShouldContain("ra=359.700000");
    }

    [Fact]
    public void DeclinationIsClampedToThePoles()
        => SkyAtlasLink.For(0, 91.4).ShouldContain("dec=90.000000");

    [Fact]
    public void TheCaptureTimeTravelsAsUtcSeconds()
    {
        // Written from an offset that is NOT UTC, so a link built in Australia and opened in Europe
        // draws the same sky rather than one eleven hours out.
        var captured = new DateTimeOffset(2026, 1, 19, 10, 26, 51, TimeSpan.FromHours(11));

        SkyAtlasLink.For(OrionRaHours, OrionDecDeg, capturedUtc: captured)
            .ShouldContain("&t=2026-01-18T23:26:51Z");
    }

    /// <summary>
    /// Both import paths use a SENTINEL rather than a null for "this frame does not say when it was
    /// taken" -- a FITS with no DATE-OBS parses to <see cref="DateTime.MinValue"/>, a CR2 with no EXIF
    /// capture time to the epoch. A link carrying either would draw the sky of the year 1.
    /// </summary>
    [Theory]
    [InlineData("0001-01-01T00:00:00Z")]
    [InlineData("1970-01-01T00:00:00Z")]
    public void AMissingCaptureTimeIsNotWrittenAtAll(string sentinel)
    {
        var time = DateTimeOffset.Parse(sentinel, System.Globalization.CultureInfo.InvariantCulture);

        SkyAtlasLink.IsKnownCaptureTime(time).ShouldBeFalse();
        SkyAtlasLink.For(OrionRaHours, OrionDecDeg, capturedUtc: time).ShouldNotContain("&t=");
    }

    [Fact]
    public void ARealCaptureTimeIsKnown()
        => SkyAtlasLink.IsKnownCaptureTime(new DateTimeOffset(2026, 1, 18, 23, 26, 51, TimeSpan.Zero)).ShouldBeTrue();

    [Theory]
    [InlineData(0d)]
    [InlineData(-1.5d)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void AFieldOfViewThatIsNotAMeasurementIsOmitted(double fov)
        => SkyAtlasLink.For(OrionRaHours, OrionDecDeg, fovDeg: fov).ShouldNotContain("&fov=");

    [Fact]
    public void AFieldOfViewTravelsWhenItIsOne()
        => SkyAtlasLink.For(OrionRaHours, OrionDecDeg, fovDeg: 1.234567).ShouldContain("&fov=1.2346");

    // --- FieldOfViewDeg ---

    /// <summary>
    /// A WCS whose CD matrix works out to <paramref name="arcsecPerPixel"/>. The reference PIXEL is
    /// part of it: HasCDMatrix wants CRPix too, and without those two the scale reads NaN however
    /// complete the matrix looks.
    /// </summary>
    private static WCS Solved(double arcsecPerPixel)
    {
        var degPerPx = arcsecPerPixel / 3600.0;
        return new WCS(0, 0)
        {
            CD1_1 = -degPerPx, CD1_2 = 0, CD2_1 = 0, CD2_2 = degPerPx,
            CRPix1 = 1, CRPix2 = 1,
        };
    }

    [Fact]
    public void TheFieldOfViewIsThePlateScaleAcrossTheLongAxis()
    {
        // The Vela crop this was checked against live: 1310 x 1291 solved at 5.97"/px.
        var wcs = Solved(5.97);
        wcs.PixelScaleArcsec.ShouldBe(5.97, 1e-9, "the fixture has to be the scale it claims");

        SkyAtlasLink.FieldOfViewDeg(wcs, 1310, 1291).ShouldNotBeNull().ShouldBe(2.1724, 1e-4);
        SkyAtlasLink.FieldOfViewDeg(wcs, 1291, 1310).ShouldNotBeNull().ShouldBe(2.1724, 1e-4,
            "the LONG axis whichever way round the sensor is, so the atlas frames the whole image");
    }

    [Fact]
    public void AFrameWithNoWcsHasNoFieldOfView()
        => SkyAtlasLink.FieldOfViewDeg(null, 1310, 1291).ShouldBeNull();

    [Fact]
    public void AWcsWithNoCdMatrixHasNoFieldOfView()
        // PixelScaleArcsec is NaN there, and NaN * anything would have travelled as "fov=NaN".
        => SkyAtlasLink.FieldOfViewDeg(new WCS(1.0, 2.0), 1310, 1291).ShouldBeNull();

    [Fact]
    public void ADegenerateCdMatrixHasNoFieldOfView()
        // A zero determinant is a solve that collapsed; the scale reads 0 rather than NaN.
        => SkyAtlasLink.FieldOfViewDeg(
            new WCS(0, 0) { CD1_1 = 0, CD1_2 = 0, CD2_1 = 0, CD2_2 = 0, CRPix1 = 1, CRPix2 = 1 },
            1310, 1291).ShouldBeNull();

    [Fact]
    public void AZeroSizedFrameHasNoFieldOfView()
        => SkyAtlasLink.FieldOfViewDeg(Solved(5.97), 0, 0).ShouldBeNull();

    [Fact]
    public void EveryNumberIsInvariantCulture()
    {
        // The build runs under whatever culture the machine has; a comma decimal separator would make
        // "ra=83,822085" two query values, and the atlas would read a truncated RA rather than fail.
        var link = SkyAtlasLink.For(OrionRaHours, OrionDecDeg, fovDeg: 1.5);

        link.ShouldNotContain(",");
    }
}
