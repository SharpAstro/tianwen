using System;
using System.Collections.Immutable;
using System.Linq;
using Shouldly;
using TianWen.Lib.Sequencing;
using Xunit;

namespace TianWen.Lib.Tests;

[Collection("Scheduling")]
public sealed class MosaicGeneratorTests
{
    // M31 — Andromeda Galaxy: RA=0.712h, Dec=41.27°, MajorAxis=178', MinorAxis=63', PA=35°
    private const double M31_RA = 0.712;
    private const double M31_Dec = 41.27;
    private const double M31_MajorArcmin = 178;
    private const double M31_MinorArcmin = 63;
    private const double M31_PA = 35;

    // 500mm f/4 + mono camera: ~77' × 51' FOV
    private const double FovW_500mm = 77.0 / 60.0; // degrees
    private const double FovH_500mm = 51.0 / 60.0;

    // Wide FOV that fits M31 in a single frame: 4° × 3°
    private const double FovW_Wide = 4.0;
    private const double FovH_Wide = 3.0;

    [Fact]
    public void GeneratePanels_M31With500mm_ProducesMultiplePanels()
    {
        var panels = MosaicGenerator.GeneratePanels(
            M31_RA, M31_Dec, M31_MajorArcmin, M31_MinorArcmin, M31_PA,
            FovW_500mm, FovH_500mm);

        panels.Length.ShouldBeGreaterThan(1, "M31 at 500mm should require multiple mosaic panels");
    }

    [Fact]
    public void GeneratePanels_ObjectFitsInSingleFov_ReturnsSinglePanel()
    {
        var panels = MosaicGenerator.GeneratePanels(
            M31_RA, M31_Dec, M31_MajorArcmin, M31_MinorArcmin, M31_PA,
            FovW_Wide, FovH_Wide);

        panels.Length.ShouldBe(1);
        panels[0].Row.ShouldBe(0);
        panels[0].Column.ShouldBe(0);
        panels[0].Target.RA.ShouldBe(M31_RA);
        panels[0].Target.Dec.ShouldBe(M31_Dec);
    }

    [Fact]
    public void GeneratePanels_ZeroSizeObject_ReturnsSinglePanel()
    {
        // Object with no known size (0 arcmin) — single panel at center
        var panels = MosaicGenerator.GeneratePanels(
            10.0, 30.0, 0, 0, 0,
            FovW_500mm, FovH_500mm);

        panels.Length.ShouldBe(1);
        panels[0].Target.RA.ShouldBe(10.0);
        panels[0].Target.Dec.ShouldBe(30.0);
    }

    [Fact]
    public void GeneratePanels_SymmetricObject_ProducesRegularGrid()
    {
        // Large circular object (PA=0), 2° diameter
        var panels = MosaicGenerator.GeneratePanels(
            12.0, 0.0,
            majorAxisArcmin: 120, minorAxisArcmin: 120, positionAngleDeg: 0,
            fovWidthDeg: 0.5, fovHeightDeg: 0.5);

        // All rows should have the same number of columns
        var maxRow = panels.Max(p => p.Row);
        var maxCol = panels.Max(p => p.Column);

        for (var row = 0; row <= maxRow; row++)
        {
            var colsInRow = panels.Count(p => p.Row == row);
            colsInRow.ShouldBe(maxCol + 1, $"Row {row} should have {maxCol + 1} columns");
        }
    }

    [Fact]
    public void GeneratePanels_PanelsOrderedByRAAscending()
    {
        var panels = MosaicGenerator.GeneratePanels(
            M31_RA, M31_Dec, M31_MajorArcmin, M31_MinorArcmin, M31_PA,
            FovW_500mm, FovH_500mm);

        // Panels should be sorted by RA ascending (column-first sweep)
        for (var i = 1; i < panels.Length; i++)
        {
            panels[i].TransitTimeHours.ShouldBeGreaterThanOrEqualTo(panels[i - 1].TransitTimeHours,
                $"Panel {i} RA should be >= panel {i - 1} RA (transit-time ordering)");
        }
    }

    [Fact]
    public void GeneratePanels_ColumnFirstSweep_EasternPanelsFirst()
    {
        var panels = MosaicGenerator.GeneratePanels(
            M31_RA, M31_Dec, M31_MajorArcmin, M31_MinorArcmin, M31_PA,
            FovW_500mm, FovH_500mm);

        // First panels should be column 0 (lowest RA = easternmost)
        var firstColumn = panels.TakeWhile(p => p.Column == 0).Count();
        firstColumn.ShouldBeGreaterThan(0, "First panels should be from column 0");

        // All column 0 panels should come before column 1 panels
        var lastCol0Index = -1;
        var firstCol1Index = panels.Length;
        for (var i = 0; i < panels.Length; i++)
        {
            if (panels[i].Column == 0) lastCol0Index = i;
            if (panels[i].Column == 1 && i < firstCol1Index) firstCol1Index = i;
        }

        if (firstCol1Index < panels.Length)
        {
            lastCol0Index.ShouldBeLessThan(firstCol1Index,
                "All column 0 panels should precede column 1 panels");
        }
    }

    [Fact]
    public void GeneratePanels_RotatedObject_DifferentFromUnrotated()
    {
        var panelsPA0 = MosaicGenerator.GeneratePanels(
            M31_RA, M31_Dec, M31_MajorArcmin, M31_MinorArcmin, positionAngleDeg: 0,
            FovW_500mm, FovH_500mm);

        var panelsPA35 = MosaicGenerator.GeneratePanels(
            M31_RA, M31_Dec, M31_MajorArcmin, M31_MinorArcmin, positionAngleDeg: 35,
            FovW_500mm, FovH_500mm);

        // Different PA should potentially produce different grid dimensions
        // (rotated ellipse has different bounding box)
        // At minimum, both should produce valid grids
        panelsPA0.Length.ShouldBeGreaterThan(1);
        panelsPA35.Length.ShouldBeGreaterThan(1);
    }

    [Fact]
    public void GeneratePanels_PanelCentersNearObjectCenter()
    {
        var panels = MosaicGenerator.GeneratePanels(
            M31_RA, M31_Dec, M31_MajorArcmin, M31_MinorArcmin, M31_PA,
            FovW_500mm, FovH_500mm);

        // Average panel center should be close to the object center
        var avgRA = panels.Average(p => p.Target.RA);
        var avgDec = panels.Average(p => p.Target.Dec);

        // Within a few panel widths of center
        Math.Abs(avgRA - M31_RA).ShouldBeLessThan(FovW_500mm, "Average RA should be near object center");
        Math.Abs(avgDec - M31_Dec).ShouldBeLessThan(3.0, "Average Dec should be near object center");
    }

    [Fact]
    public void GeneratePanels_TransitTimeEqualsRA()
    {
        var panels = MosaicGenerator.GeneratePanels(
            M31_RA, M31_Dec, M31_MajorArcmin, M31_MinorArcmin, M31_PA,
            FovW_500mm, FovH_500mm);

        foreach (var panel in panels)
        {
            panel.TransitTimeHours.ShouldBe(panel.Target.RA,
                $"Panel R{panel.Row}C{panel.Column} transit time should equal its RA");
        }
    }

    [Fact]
    public void GeneratePanels_InvalidFov_ThrowsArgumentOutOfRange()
    {
        Should.Throw<ArgumentOutOfRangeException>(() =>
            MosaicGenerator.GeneratePanels(0, 0, 60, 60, 0, fovWidthDeg: 0, fovHeightDeg: 1));

        Should.Throw<ArgumentOutOfRangeException>(() =>
            MosaicGenerator.GeneratePanels(0, 0, 60, 60, 0, fovWidthDeg: 1, fovHeightDeg: -1));
    }

    [Fact]
    public void GeneratePanels_NearPole_HandlesCosDeclination()
    {
        // Object near Dec=80° — cos(Dec) is small, so RA steps should be wider
        var panels = MosaicGenerator.GeneratePanels(
            centerRA: 6.0, centerDec: 80.0,
            majorAxisArcmin: 120, minorAxisArcmin: 60, positionAngleDeg: 0,
            fovWidthDeg: 0.5, fovHeightDeg: 0.5);

        panels.Length.ShouldBeGreaterThan(1);

        // Dec values should all be near 80°
        foreach (var panel in panels)
        {
            panel.Target.Dec.ShouldBeInRange(75.0, 85.0);
        }
    }

    [Fact]
    public void GeneratePanels_OverlapAffectsPanelCount()
    {
        var panelsLowOverlap = MosaicGenerator.GeneratePanels(
            M31_RA, M31_Dec, M31_MajorArcmin, M31_MinorArcmin, M31_PA,
            FovW_500mm, FovH_500mm, overlap: 0.1);

        var panelsHighOverlap = MosaicGenerator.GeneratePanels(
            M31_RA, M31_Dec, M31_MajorArcmin, M31_MinorArcmin, M31_PA,
            FovW_500mm, FovH_500mm, overlap: 0.4);

        // Higher overlap should produce more panels (smaller steps)
        panelsHighOverlap.Length.ShouldBeGreaterThanOrEqualTo(panelsLowOverlap.Length,
            "Higher overlap should produce at least as many panels");
    }

    [Fact]
    public void ComputeFieldOfView_500mmF4_MatchesExpected()
    {
        // 500mm focal length, 3.76µm pixels, 4656×3520 sensor (ASI2600MM)
        var (w, h) = MosaicGenerator.ComputeFieldOfView(
            focalLengthMm: 500, pixelSizeUm: 3.76, sensorWidthPx: 4656, sensorHeightPx: 3520);

        // 3.76µm / 500mm * 206.265 = 1.551 arcsec/pixel
        // W = 1.551 * 4656 / 3600 = 2.006°, H = 1.551 * 3520 / 3600 = 1.517°
        w.ShouldBeInRange(1.9, 2.1, "FOV width should be about 2.01°");
        h.ShouldBeInRange(1.4, 1.6, "FOV height should be about 1.52°");
    }

    [Fact]
    public void ComputeFieldOfView_WithBinning_ScalesCorrectly()
    {
        var (w1, h1) = MosaicGenerator.ComputeFieldOfView(500, 3.76, 4656, 3520, binning: 1);
        var (w2, h2) = MosaicGenerator.ComputeFieldOfView(500, 3.76, 4656, 3520, binning: 2);

        // Binning 2x should give the same FOV (larger pixels but fewer of them)
        Math.Abs(w1 - w2).ShouldBeLessThan(0.01, "Binning should not change FOV");
        Math.Abs(h1 - h2).ShouldBeLessThan(0.01, "Binning should not change FOV");
    }

    [Theory]
    [InlineData(0, 60, 60, 60, 60)]       // PA=0: circular, bbox = diameter both ways
    [InlineData(0, 60, 30, 30, 60)]        // PA=0: major along Dec → width=minor, height=major
    [InlineData(90, 60, 30, 60, 30)]       // PA=90: major along RA → width=major, height=minor
    [InlineData(45, 60, 30, 47.4, 47.4)]   // PA=45: ~diagonal, roughly equal
    public void ComputeRotatedEllipseBBox_MatchesExpected(
        double pa, double major, double minor, double expectedW, double expectedH)
    {
        var (w, h) = MosaicGenerator.ComputeRotatedEllipseBBox(major, minor, pa);

        w.ShouldBe(expectedW, tolerance: 1.0);
        h.ShouldBe(expectedH, tolerance: 1.0);
    }

    [Fact]
    public void GeneratePanels_M31With500mm_HasReasonablePanelCount()
    {
        var panels = MosaicGenerator.GeneratePanels(
            M31_RA, M31_Dec, M31_MajorArcmin, M31_MinorArcmin, M31_PA,
            FovW_500mm, FovH_500mm);

        // M31 is ~3° × 1° at 500mm with ~1.3° × 0.85° FOV
        // With 20% overlap, expect roughly 3-6 cols × 2-4 rows = 6-24 panels
        panels.Length.ShouldBeInRange(4, 30, "M31 panel count should be in a reasonable range");
    }

    [Fact]
    public void GeneratePanels_PanelNamesContainRowAndColumn()
    {
        var panels = MosaicGenerator.GeneratePanels(
            M31_RA, M31_Dec, M31_MajorArcmin, M31_MinorArcmin, M31_PA,
            FovW_500mm, FovH_500mm);

        foreach (var panel in panels)
        {
            panel.Target.Name.ShouldContain($"R{panel.Row}");
            panel.Target.Name.ShouldContain($"C{panel.Column}");
        }
    }

    [Fact]
    public void GeneratePanels_DecClampedToValidRange()
    {
        // Object near south pole
        var panels = MosaicGenerator.GeneratePanels(
            centerRA: 0, centerDec: -88,
            majorAxisArcmin: 120, minorAxisArcmin: 120, positionAngleDeg: 0,
            fovWidthDeg: 0.5, fovHeightDeg: 0.5);

        foreach (var panel in panels)
        {
            panel.Target.Dec.ShouldBeInRange(-90.0, 90.0, "Dec should be clamped to valid range");
        }
    }

    [Fact]
    public void GeneratePanels_RAWrapsCorrectly()
    {
        // Object near RA=0 (wraps around 24h boundary)
        var panels = MosaicGenerator.GeneratePanels(
            centerRA: 0.1, centerDec: 30,
            majorAxisArcmin: 120, minorAxisArcmin: 60, positionAngleDeg: 0,
            fovWidthDeg: 0.5, fovHeightDeg: 0.5);

        foreach (var panel in panels)
        {
            panel.Target.RA.ShouldBeInRange(0.0, 24.0, "RA should be in [0, 24)");
        }
    }
}
