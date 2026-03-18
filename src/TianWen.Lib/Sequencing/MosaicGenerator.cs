using System;
using System.Collections.Immutable;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Devices;

namespace TianWen.Lib.Sequencing;

/// <summary>
/// Generates mosaic panel grids for extended objects that don't fit in a single FOV.
/// Panels are ordered by RA ascending (east-to-west) so that panels crossing the
/// meridian first are imaged first, resulting in at most one GEM flip per mosaic cycle.
/// </summary>
public static class MosaicGenerator
{
    /// <summary>
    /// Computes FOV from OTA parameters without needing an image.
    /// </summary>
    /// <returns>Field of view in degrees (width, height)</returns>
    public static (double WidthDeg, double HeightDeg) ComputeFieldOfView(
        int focalLengthMm, double pixelSizeUm, int sensorWidthPx, int sensorHeightPx, int binning = 1)
    {
        // plate scale = pixelSize_µm / focalLength_mm * 206.265 arcsec/pixel
        var pixelScaleArcsec = pixelSizeUm * binning / focalLengthMm * 206.265;
        const double ArcSecToDeg = 1.0 / 3600.0;
        return (ArcSecToDeg * pixelScaleArcsec * sensorWidthPx / binning,
                ArcSecToDeg * pixelScaleArcsec * sensorHeightPx / binning);
    }

    /// <summary>
    /// Generates mosaic panels from catalog object data.
    /// Looks up RA/Dec and shape from the object database.
    /// </summary>
    public static ImmutableArray<MosaicPanel> GeneratePanels(
        ICelestialObjectDB objectDb,
        CatalogIndex catalogIndex,
        double fovWidthDeg,
        double fovHeightDeg,
        double overlap = 0.2,
        double margin = 0.1)
    {
        if (!objectDb.TryLookupByIndex(catalogIndex, out var obj))
        {
            throw new ArgumentException($"Object {catalogIndex.ToCanonical()} not found in database", nameof(catalogIndex));
        }

        double majorAxisArcmin = 0;
        double minorAxisArcmin = 0;
        double positionAngleDeg = 0;

        if (objectDb.TryGetShape(catalogIndex, out var shape))
        {
            majorAxisArcmin = (double)shape.MajorAxis;
            minorAxisArcmin = (double)shape.MinorAxis;
            positionAngleDeg = Half.IsNaN(shape.PositionAngle) ? 0 : (double)shape.PositionAngle;
        }

        return GeneratePanels(obj.RA, obj.Dec, majorAxisArcmin, minorAxisArcmin,
            positionAngleDeg, fovWidthDeg, fovHeightDeg, overlap, margin, obj.DisplayName, catalogIndex);
    }

    /// <summary>
    /// Generates mosaic panels for an extended object.
    /// </summary>
    /// <param name="centerRA">Center RA in hours (0–24)</param>
    /// <param name="centerDec">Center Dec in degrees (-90–+90)</param>
    /// <param name="majorAxisArcmin">Major axis in arcminutes (from catalog shape)</param>
    /// <param name="minorAxisArcmin">Minor axis in arcminutes</param>
    /// <param name="positionAngleDeg">Position angle in degrees, N through E</param>
    /// <param name="fovWidthDeg">Sensor FOV width in degrees</param>
    /// <param name="fovHeightDeg">Sensor FOV height in degrees</param>
    /// <param name="overlap">Fractional overlap between panels (0.0–1.0, default 0.2 = 20%)</param>
    /// <param name="margin">Fractional margin around the object (0.0–1.0, default 0.1 = 10%)</param>
    /// <param name="objectName">Parent object name for panel naming (e.g., "M31" → "M31_R0C0")</param>
    /// <param name="catalogIndex">Catalog index propagated to each panel's Target</param>
    /// <returns>Panels sorted by RA ascending (column-first sweep for meridian-aware ordering)</returns>
    public static ImmutableArray<MosaicPanel> GeneratePanels(
        double centerRA,
        double centerDec,
        double majorAxisArcmin,
        double minorAxisArcmin,
        double positionAngleDeg,
        double fovWidthDeg,
        double fovHeightDeg,
        double overlap = 0.2,
        double margin = 0.1,
        string? objectName = null,
        CatalogIndex? catalogIndex = null)
    {
        if (fovWidthDeg <= 0) throw new ArgumentOutOfRangeException(nameof(fovWidthDeg));
        if (fovHeightDeg <= 0) throw new ArgumentOutOfRangeException(nameof(fovHeightDeg));

        // Convert object size from arcmin to degrees
        var majorDeg = majorAxisArcmin / 60.0;
        var minorDeg = minorAxisArcmin / 60.0;

        // Compute bounding box of the rotated ellipse in RA/Dec space
        var (bboxWidthDeg, bboxHeightDeg) = ComputeRotatedEllipseBBox(majorDeg, minorDeg, positionAngleDeg);

        // Single-panel shortcut: if the bounding box fits within a single FOV (with margin), no mosaic needed
        var effectiveFovW = fovWidthDeg * (1 - margin);
        var effectiveFovH = fovHeightDeg * (1 - margin);
        var namePrefix = objectName ?? "Mosaic";

        if (bboxWidthDeg <= effectiveFovW && bboxHeightDeg <= effectiveFovH)
        {
            return [new MosaicPanel(
                new Target(centerRA, centerDec, FormatPanelName(namePrefix, 0, 0), catalogIndex),
                Row: 0, Column: 0, TransitTimeHours: centerRA)];
        }

        // Add margin
        bboxWidthDeg *= 1 + margin;
        bboxHeightDeg *= 1 + margin;

        // Panel step sizes (with overlap)
        var stepDec = fovHeightDeg * (1 - overlap);
        var cosDec = Math.Cos(centerDec * Math.PI / 180.0);
        // RA step must account for cos(Dec) projection — wider RA steps at higher declinations
        var stepRA = cosDec > 0.01 ? fovWidthDeg * (1 - overlap) / cosDec : fovWidthDeg * (1 - overlap);

        // Grid dimensions
        var nRows = Math.Max(1, (int)Math.Ceiling(bboxHeightDeg / stepDec));
        var nCols = Math.Max(1, (int)Math.Ceiling(bboxWidthDeg / stepRA));

        // Center the grid on the object center
        // RA is in degrees for the grid math, convert to hours at the end
        var centerRADeg = centerRA * 15.0; // hours → degrees
        var gridOriginRADeg = centerRADeg - (nCols - 1) * stepRA * 0.5 * cosDec;
        var gridOriginDec = centerDec - (nRows - 1) * stepDec * 0.5;

        // Generate panels: column-first sweep (RA ascending) for meridian-aware ordering
        var builder = ImmutableArray.CreateBuilder<MosaicPanel>(nRows * nCols);
        for (var col = 0; col < nCols; col++)
        {
            for (var row = 0; row < nRows; row++)
            {
                var panelDec = gridOriginDec + row * stepDec;
                // Clamp Dec to valid range
                panelDec = Math.Clamp(panelDec, -90, 90);

                var panelRADeg = gridOriginRADeg + col * stepRA * cosDec;
                // Wrap RA to [0, 360)
                panelRADeg = ((panelRADeg % 360) + 360) % 360;
                var panelRA = panelRADeg / 15.0; // degrees → hours

                var name = FormatPanelName(namePrefix, row, col);
                var target = new Target(panelRA, panelDec, name, catalogIndex);
                builder.Add(new MosaicPanel(target, row, col, panelRA));
            }
        }

        return builder.MoveToImmutable();
    }

    /// <summary>
    /// Computes the axis-aligned bounding box of an ellipse rotated by the given position angle.
    /// </summary>
    internal static (double WidthDeg, double HeightDeg) ComputeRotatedEllipseBBox(
        double majorDeg, double minorDeg, double positionAngleDeg)
    {
        // PA is measured N through E. For bounding box, we need the angle from the RA axis.
        // In equatorial coordinates: RA increases east, Dec increases north.
        // PA = 0 means major axis along Dec (north), PA = 90 means along RA (east).
        var paRad = positionAngleDeg * Math.PI / 180.0;
        var sinPA = Math.Sin(paRad);
        var cosPA = Math.Cos(paRad);

        var a = majorDeg / 2.0; // semi-major
        var b = minorDeg / 2.0; // semi-minor

        // Bounding box of a rotated ellipse:
        // halfWidth  = sqrt((a*sinPA)² + (b*cosPA)²)
        // halfHeight = sqrt((a*cosPA)² + (b*sinPA)²)
        var halfWidth = Math.Sqrt(a * sinPA * (a * sinPA) + b * cosPA * (b * cosPA));
        var halfHeight = Math.Sqrt(a * cosPA * (a * cosPA) + b * sinPA * (b * sinPA));

        return (halfWidth * 2, halfHeight * 2);
    }

    private static string FormatPanelName(string objectName, int row, int col)
        => $"{objectName}_R{row}C{col}";
}
