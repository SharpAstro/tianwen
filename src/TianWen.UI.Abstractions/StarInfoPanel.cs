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

    /// <summary>
    /// The whole panel as ONE declared tree, in design units: a one-unit border node that swallows a press
    /// anywhere on it, around the padded background, around the title row (with Close at its end) and the
    /// rows. Its width is declared and its height is the engine's measurement of the rows, so there is no
    /// box to sum by hand; the host floats it with <see cref="Layout.Builder.Anchored"/>.
    /// </summary>
    public static Layout.Node Build(string[] lines, in ObjectInfoPanel.PanelPalette palette, in ObjectInfoPanel.PanelActions actions)
    {
        const float dFont = ObjectInfoPanel.DesignFontSize;
        var rows = new Layout.Node[lines.Length + 1];

        var title = Layout.Builder
            .Text(Title, dFont * 1.1f, palette.Text, TextAlign.Near, TextAlign.Near)
            .WStar();
        rows[0] = (ObjectInfoPanel.BuildCloseButton(in actions, in palette) is { } close
                ? Layout.Builder.HStack(title, close.WFixed(ObjectInfoPanel.DesignCloseSize))
                : Layout.Builder.HStack(title))
            .RowH(ObjectInfoPanel.DesignTitleRowHeight);

        for (var i = 0; i < lines.Length; i++)
        {
            rows[i + 1] = Layout.Builder.Text(lines[i], dFont, palette.Text).RowH(ObjectInfoPanel.DesignRowHeight);
        }

        var body = Layout.Builder.VStack(rows)
            .Pad(10f, 6f)
            .Bg(palette.Background)
            .WFixed(ObjectInfoPanel.DesignWidth);

        return Layout.Builder.VStack(body)
            .Pad(1f)
            .Bg(palette.Border)
            .WFixed(ObjectInfoPanel.DesignWidth + 2f)
            .Clickable(new HitResult.ButtonHit("StarPanelBackground"), _ => { });
    }
}
