using System.Globalization;
using DIR.Lib;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Imaging;

namespace TianWen.UI.Abstractions;

/// <summary>
/// What the floating panel for a selected DETECTED star says (#708): the star's own measurement, row by
/// row, and the one declared tree that lays it out. Placement is the host's, as with
/// <see cref="ObjectInfoPanel"/>, whose palette, width, row heights and Close affordance it reuses so the
/// two panels are one style.
/// </summary>
/// <remarks>
/// <b>Nothing here measures.</b> Every number is already on the <see cref="ImagedStar"/>; the only
/// derived row is RA/Dec, one <see cref="WCS.PixelToSky"/> of the centroid, and it exists only when the
/// frame has a plate solution (a star without one has a position in pixels and no sky position).
/// </remarks>
public static class StarInfoPanel
{
    /// <summary>The panel's title row.</summary>
    public const string Title = "Detected star";

    /// <summary>
    /// The measurement rows under the title, each a pair of values so the panel stays as narrow as the
    /// object panel. The RA/Dec row is present only when <paramref name="wcs"/> can place the centroid.
    /// </summary>
    public static string[] Lines(in ImagedStar star, WCS? wcs)
    {
        var inv = CultureInfo.InvariantCulture;
        var lines = new System.Collections.Generic.List<string>(5)
        {
            string.Create(inv, $"Centroid  x {star.XCentroid:F2}   y {star.YCentroid:F2}"),
            string.Create(inv, $"HFD {star.HFD:F2} px   FWHM {star.StarFWHM:F2} px"),
            string.Create(inv, $"SNR {star.SNR:F1}   Ellipticity {star.Ellipticity:F2}"),
            string.Create(inv, $"Flux {star.Flux:F0}   Background {star.LocalBackground:F1}"),
        };

        if (wcs is { } w && w.PixelToSky(star.XCentroid, star.YCentroid) is var (ra, dec))
        {
            lines.Add($"RA {CoordinateUtils.HoursToHMS(ra)}   Dec {CoordinateUtils.DegreesToDMS(dec)}");
        }

        return [.. lines];
    }

    /// <summary>The panel's design height: the title, the rows and the padding that keeps them off the edge.</summary>
    public static float DesignHeight(int lineCount)
        => ObjectInfoPanel.DesignTitleRowHeight + (ObjectInfoPanel.DesignRowHeight * lineCount) + 16f;

    /// <summary>The title and the rows as one tree, in design units.</summary>
    public static Layout.Node BuildRows(string[] lines, in ObjectInfoPanel.PanelPalette palette)
    {
        const float dFont = ObjectInfoPanel.DesignFontSize;
        var rows = new Layout.Node[lines.Length + 1];
        rows[0] = Layout.Builder
            .Text(Title, dFont * 1.1f, palette.Text, TextAlign.Near, TextAlign.Near)
            .RowH(ObjectInfoPanel.DesignTitleRowHeight);
        for (var i = 0; i < lines.Length; i++)
        {
            rows[i + 1] = Layout.Builder.Text(lines[i], dFont, palette.Text).RowH(ObjectInfoPanel.DesignRowHeight);
        }

        return Layout.Builder.VStack(rows);
    }
}
