using Shouldly;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Imaging;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>The rows a selected detected star's panel states (#708), with and without a plate solution.</summary>
public class ViewerStarSelectionPanelTests
{
    private static readonly ImagedStar Star = new(
        HFD: 3.456f, StarFWHM: 4.1f, SNR: 52.34f, Flux: 12345.6f, XCentroid: 32.25f, YCentroid: 24.5f,
        Ellipticity: 0.123f, LocalBackground: 456.78f);

    [Fact]
    public void WithoutAPlateSolutionTheRowsAreTheMeasurementAlone()
    {
        var lines = StarInfoPanel.Lines(in Star, wcs: null);

        lines.ShouldBe(
        [
            "Centroid  x 32.25   y 24.50",
            "HFD 3.46 px   FWHM 4.10 px",
            "SNR 52.3   Ellipticity 0.12",
            "Flux 12346   Background 456.8",
        ]);
    }

    [Fact]
    public void AWcsWithNoCdMatrixAddsNoSkyRow()
        => StarInfoPanel.Lines(in Star, new WCS(5.0, 20.0)).Length.ShouldBe(4);

    [Fact]
    public void WithAPlateSolutionTheLastRowIsTheCentroidsSkyPosition()
    {
        var wcs = new WCS(13.5, 47.0)
        {
            CRPix1 = 32.25,
            CRPix2 = 24.5,
            CD1_1 = -0.001,
            CD1_2 = 0.0,
            CD2_1 = 0.0,
            CD2_2 = -0.001,
        };

        var lines = StarInfoPanel.Lines(in Star, wcs);

        lines.Length.ShouldBe(5);
        // The centroid is the reference pixel, so the sky position is the reference value.
        lines[4].ShouldBe($"RA {CoordinateUtils.HoursToHMS(13.5)}   Dec {CoordinateUtils.DegreesToDMS(47.0)}");
    }
}
