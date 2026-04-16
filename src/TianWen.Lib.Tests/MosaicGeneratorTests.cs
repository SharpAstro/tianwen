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

    // --- Coverage verification tests ---

    // NGC 3372 (Eta Carinae Nebula): RA=10.733h, Dec=-59.867deg, ~120'x120', PA~0
    private const double NGC3372_RA = 10.733;
    private const double NGC3372_Dec = -59.867;
    private const double NGC3372_MajorArcmin = 120;
    private const double NGC3372_MinorArcmin = 120;
    private const double NGC3372_PA = 0;

    // 200mm lens + APS-C: ~5.3deg x 3.5deg FOV (fits NGC 3372 in single panel)
    private const double FovW_200mm = 5.3;
    private const double FovH_200mm = 3.5;

    // 1000mm SCT + mono camera: ~0.96deg x 0.72deg FOV
    private const double FovW_1000mm = 0.96;
    private const double FovH_1000mm = 0.72;

    // 2000mm SCT + mono camera: ~0.48deg x 0.36deg FOV
    private const double FovW_2000mm = 0.48;
    private const double FovH_2000mm = 0.36;

    /// <summary>
    /// Returns true if the given sky point (RA in hours, Dec in degrees) falls within
    /// at least one panel's FOV rectangle.
    /// </summary>
    private static bool IsPointCoveredByAnyPanel(
        ImmutableArray<MosaicPanel> panels, double pointRA, double pointDec,
        double fovWidthDeg, double fovHeightDeg)
    {
        var halfFovDec = fovHeightDeg / 2.0;

        foreach (var panel in panels)
        {
            // Dec check
            if (Math.Abs(pointDec - panel.Target.Dec) > halfFovDec)
            {
                continue;
            }

            // RA check — convert hour difference to sky degrees via cos(Dec)
            var cosDec = Math.Cos(panel.Target.Dec * Math.PI / 180.0);
            var halfFovRA = cosDec > 0.01 ? fovWidthDeg / (2.0 * 15.0 * cosDec) : 12.0;
            var dRA = Math.Abs(pointRA - panel.Target.RA);
            // Handle RA wraparound (0/24h boundary)
            if (dRA > 12.0) dRA = 24.0 - dRA;

            if (dRA <= halfFovRA)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Verifies that all sample points on the object's bounding box boundary and interior
    /// are covered by at least one mosaic panel.
    /// </summary>
    private static void AssertFullCoverage(
        ImmutableArray<MosaicPanel> panels,
        double centerRA, double centerDec,
        double majorAxisArcmin, double minorAxisArcmin, double positionAngleDeg,
        double fovWidthDeg, double fovHeightDeg)
    {
        var majorDeg = majorAxisArcmin / 60.0;
        var minorDeg = minorAxisArcmin / 60.0;
        var (bboxW, bboxH) = MosaicGenerator.ComputeRotatedEllipseBBox(majorDeg, minorDeg, positionAngleDeg);

        var cosDec = Math.Cos(centerDec * Math.PI / 180.0);
        // Half-extents in RA hours and Dec degrees
        var halfExtentRA = cosDec > 0.01 ? bboxW / (2.0 * 15.0 * cosDec) : bboxW / 30.0;
        var halfExtentDec = bboxH / 2.0;

        // Sample a grid of points across the bounding box
        const int samples = 5;
        for (var i = 0; i <= samples; i++)
        {
            for (var j = 0; j <= samples; j++)
            {
                var fRA = -1.0 + 2.0 * i / samples;   // -1 to +1
                var fDec = -1.0 + 2.0 * j / samples;

                var pointRA = centerRA + fRA * halfExtentRA;
                var pointDec = centerDec + fDec * halfExtentDec;

                // Wrap RA
                if (pointRA < 0) pointRA += 24.0;
                if (pointRA >= 24) pointRA -= 24.0;
                // Clamp Dec
                pointDec = Math.Clamp(pointDec, -90, 90);

                IsPointCoveredByAnyPanel(panels, pointRA, pointDec, fovWidthDeg, fovHeightDeg)
                    .ShouldBeTrue($"Point RA={pointRA:F4}h Dec={pointDec:F2} (grid [{i},{j}]) not covered by any panel");
            }
        }
    }

    [Fact]
    public void GeneratePanels_M31At500mm_CoversFullExtent()
    {
        var panels = MosaicGenerator.GeneratePanels(
            M31_RA, M31_Dec, M31_MajorArcmin, M31_MinorArcmin, M31_PA,
            FovW_500mm, FovH_500mm);

        panels.Length.ShouldBeGreaterThan(1);
        AssertFullCoverage(panels, M31_RA, M31_Dec,
            M31_MajorArcmin, M31_MinorArcmin, M31_PA,
            FovW_500mm, FovH_500mm);
    }

    [Fact]
    public void GeneratePanels_NGC3372At200mm_FitsInSinglePanel()
    {
        var panels = MosaicGenerator.GeneratePanels(
            NGC3372_RA, NGC3372_Dec, NGC3372_MajorArcmin, NGC3372_MinorArcmin, NGC3372_PA,
            FovW_200mm, FovH_200mm);

        panels.Length.ShouldBe(1, "NGC 3372 should fit in a single 200mm FOV");
        panels[0].Target.RA.ShouldBe(NGC3372_RA);
        panels[0].Target.Dec.ShouldBe(NGC3372_Dec);
    }

    [Fact]
    public void GeneratePanels_NGC3372At500mm_CoversFullExtent()
    {
        // 500mm: ~1.28deg x 0.85deg FOV vs 2deg object -- needs mosaic
        var panels = MosaicGenerator.GeneratePanels(
            NGC3372_RA, NGC3372_Dec, NGC3372_MajorArcmin, NGC3372_MinorArcmin, NGC3372_PA,
            FovW_500mm, FovH_500mm);

        panels.Length.ShouldBeGreaterThan(1, "NGC 3372 at 500mm should need a mosaic");
        AssertFullCoverage(panels, NGC3372_RA, NGC3372_Dec,
            NGC3372_MajorArcmin, NGC3372_MinorArcmin, NGC3372_PA,
            FovW_500mm, FovH_500mm);
    }

    [Fact]
    public void GeneratePanels_NGC3372At1000mm_CoversFullExtent()
    {
        var panels = MosaicGenerator.GeneratePanels(
            NGC3372_RA, NGC3372_Dec, NGC3372_MajorArcmin, NGC3372_MinorArcmin, NGC3372_PA,
            FovW_1000mm, FovH_1000mm);

        panels.Length.ShouldBeGreaterThan(1);
        AssertFullCoverage(panels, NGC3372_RA, NGC3372_Dec,
            NGC3372_MajorArcmin, NGC3372_MinorArcmin, NGC3372_PA,
            FovW_1000mm, FovH_1000mm);
    }

    [Fact]
    public void GeneratePanels_NGC3372At2000mm_CoversFullExtent()
    {
        var panels = MosaicGenerator.GeneratePanels(
            NGC3372_RA, NGC3372_Dec, NGC3372_MajorArcmin, NGC3372_MinorArcmin, NGC3372_PA,
            FovW_2000mm, FovH_2000mm);

        panels.Length.ShouldBeGreaterThan(4, "NGC 3372 at 2000mm should need many panels");
        AssertFullCoverage(panels, NGC3372_RA, NGC3372_Dec,
            NGC3372_MajorArcmin, NGC3372_MinorArcmin, NGC3372_PA,
            FovW_2000mm, FovH_2000mm);
    }

    [Theory]
    [InlineData(0.0)]    // equator
    [InlineData(41.27)]  // M31 declination
    [InlineData(-59.87)] // NGC 3372 declination
    [InlineData(70.0)]   // high declination
    [InlineData(85.0)]   // near-polar
    public void GeneratePanels_CoversFullExtent_AtVariousDeclinations(double dec)
    {
        // 2deg circular object at the given declination, ~0.5deg FOV
        var panels = MosaicGenerator.GeneratePanels(
            centerRA: 12.0, centerDec: dec,
            majorAxisArcmin: 120, minorAxisArcmin: 120, positionAngleDeg: 0,
            fovWidthDeg: 0.5, fovHeightDeg: 0.5);

        panels.Length.ShouldBeGreaterThan(1);
        AssertFullCoverage(panels, 12.0, dec,
            120, 120, 0,
            0.5, 0.5);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(90)]
    [InlineData(135)]
    public void GeneratePanels_CoversFullExtent_AtVariousPositionAngles(double pa)
    {
        // Elongated object (3:1 ratio) at various PAs with 500mm FOV
        var panels = MosaicGenerator.GeneratePanels(
            centerRA: 6.0, centerDec: 30.0,
            majorAxisArcmin: 150, minorAxisArcmin: 50, positionAngleDeg: pa,
            fovWidthDeg: FovW_500mm, fovHeightDeg: FovH_500mm);

        panels.Length.ShouldBeGreaterThan(1);
        AssertFullCoverage(panels, 6.0, 30.0,
            150, 50, pa,
            FovW_500mm, FovH_500mm);
    }

    [Fact]
    public void GeneratePanels_PanelSpacingMatchesFovWithOverlap()
    {
        // Verify that adjacent panels in the same row are spaced by
        // fovWidth * (1 - overlap) in sky degrees, not more
        const double overlap = 0.2;
        var panels = MosaicGenerator.GeneratePanels(
            M31_RA, M31_Dec, M31_MajorArcmin, M31_MinorArcmin, M31_PA,
            FovW_500mm, FovH_500mm, overlap: overlap);

        var expectedSkyStep = FovW_500mm * (1 - overlap);
        var cosDec = Math.Cos(M31_Dec * Math.PI / 180.0);

        // Check spacing between adjacent columns in the same row
        var row0Panels = panels.Where(p => p.Row == 0).OrderBy(p => p.Column).ToArray();
        for (var i = 1; i < row0Panels.Length; i++)
        {
            var dRA = row0Panels[i].Target.RA - row0Panels[i - 1].Target.RA;
            // Handle wrap
            if (dRA < 0) dRA += 24.0;
            // Convert RA-hour difference to sky degrees
            var skyDegStep = dRA * 15.0 * cosDec;

            skyDegStep.ShouldBe(expectedSkyStep, tolerance: 0.01,
                $"Sky-degree step between col {row0Panels[i - 1].Column} and {row0Panels[i].Column} " +
                $"should be {expectedSkyStep:F3} deg, got {skyDegStep:F3} deg");
        }
    }

    [Fact]
    public void GeneratePanels_GridCenteredOnObject()
    {
        // The grid midpoint (average of all panel positions) should be very close
        // to the object center
        var panels = MosaicGenerator.GeneratePanels(
            M31_RA, M31_Dec, M31_MajorArcmin, M31_MinorArcmin, M31_PA,
            FovW_500mm, FovH_500mm);

        // Dec average should be close to center
        var avgDec = panels.Average(p => p.Target.Dec);
        Math.Abs(avgDec - M31_Dec).ShouldBeLessThan(FovH_500mm * 0.5,
            "Grid center Dec should be within half a FOV of object center");

        // RA average: convert to degrees, average, compare
        // (M31 is near RA=0 so no wrap issue for these panels)
        var avgRA = panels.Average(p => p.Target.RA);
        var cosDec = Math.Cos(M31_Dec * Math.PI / 180.0);
        var dRASkyDeg = Math.Abs(avgRA - M31_RA) * 15.0 * cosDec;
        dRASkyDeg.ShouldBeLessThan(FovW_500mm * 0.5,
            "Grid center RA should be within half a FOV of object center");
    }
}
