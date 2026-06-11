using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DIR.Lib;
using SharpAstro.Png;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Imaging;

namespace TianWen.UI.Abstractions;

/// <summary>
/// Renders a plate-solve verification overlay: a stretched master in greyscale-ish
/// RGB, with detected star centroids (green circles), nearest catalog projections
/// (red crosses), and residual lines from detected -> catalog colored by miss
/// magnitude. Makes the tol-miss pattern visible at a glance -- if the lines
/// fan out radially, it's pincushion / barrel distortion; if they all point
/// in one direction, it's a global offset; if they bias toward one half of
/// the image, it's a lens decentre or stacking registration residual.
/// <para>
/// Stretch reuses the same <see cref="AstroImageDocument.ComputeStretchUniforms"/>
/// + <see cref="Image.RenderStretchedRgba"/> path the PNG previews use, so what
/// the annotator paints on is byte-identical to <c>master.png</c>.
/// </para>
/// </summary>
public static class PlateSolveAnnotator
{
    // Visual constants. Sized to be visible at typical 2-4 arcsec/px scales
    // without dominating the underlying image. Stroke widths default to 1 px
    // -- enough to read on a 3000x3000 master, won't pixel-bomb a 600x600
    // autocrop preview either.
    private const int DetectedRadiusPx = 6;
    private const int CrossArmPx = 5;
    private static readonly RGBAColor32 DetectedColor = new(0, 255, 0, 255);       // green circle = detected star
    private static readonly RGBAColor32 CatalogAccepted = new(0, 255, 255, 255);   // cyan cross = catalog star ACCEPTED by SPCC (passed tol + has B-V + V_Mag)
    private static readonly RGBAColor32 CatalogRejected = new(255, 64, 64, 255);   // red cross = catalog star CONSIDERED but rejected (out-of-tol or missing photometry)
    private static readonly RGBAColor32 LineMatch = new(64, 255, 64, 220);         // green line: within tol -> accepted
    private static readonly RGBAColor32 LineClose = new(255, 220, 0, 220);         // yellow line: 1-3x tol -> rejected for tolerance
    private static readonly RGBAColor32 LineFar = new(255, 64, 64, 220);           // red line: >3x tol -> very far miss

    /// <summary>
    /// Render the master <paramref name="image"/> with overlay primitives marking
    /// each detected star (green circle), the nearest Tycho-2 candidate within
    /// the search ring (red cross), and a coloured line between them. Writes a
    /// PNG to <paramref name="outputPath"/>.
    /// </summary>
    /// <param name="image">The master image. WCS-bearing FITS recommended.</param>
    /// <param name="wcs">Plate-solve WCS. Must be the WCS that <paramref name="image"/> was solved with -- the projections are useless otherwise.</param>
    /// <param name="stars">Detected stars on the master (e.g. from <c>FindStarsAsync</c>).</param>
    /// <param name="db">Initialised catalog database; supplies the Tycho-2 candidates around each detected star.</param>
    /// <param name="outputPath">Output PNG path. Overwrites if present.</param>
    /// <param name="matchRadiusArcsec">Same tolerance SPCC uses to decide "matched" -- lines under this length render green.</param>
    /// <param name="searchRadiusArcsec">Maximum distance considered when looking for a nearest catalog candidate. Set well above <paramref name="matchRadiusArcsec"/> so we can see the residuals beyond the SPCC tolerance.</param>
    public static async Task RenderAnnotatedAsync(
        Image image,
        WCS wcs,
        StarList stars,
        ICelestialObjectDB db,
        string outputPath,
        float matchRadiusArcsec = 5f,
        float searchRadiusArcsec = 60f,
        CancellationToken ct = default)
    {
        var (channelCount, width, height) = image.Shape;

        // 1. Stretch + render to RGBA -- same path MasterPreviewRenderer uses for the .png.
        var perChannelStats = new ChannelStretchStats[channelCount];
        for (var c = 0; c < channelCount; c++)
        {
            var (ped, med, mad) = image.GetPedestralMedianAndMADScaledToUnit(c);
            perChannelStats[c] = new ChannelStretchStats(ped, med, mad);
        }
        var uniforms = AstroImageDocument.ComputeStretchUniforms(
            StretchMode.Unlinked,
            StretchParameters.Default,
            perChannelStats,
            lumaStats: null,
            imageMaxValue: image.MaxValue);

        var renderer = new RgbaImageRenderer((uint)width, (uint)height);
        image.RenderStretchedRgba(uniforms, renderer.Surface.Pixels.AsSpan());

        // 2. Walk each detected star, find its nearest catalog candidate, draw the overlay.
        var matchRadiusDeg = matchRadiusArcsec / 3600.0;
        var searchRadiusDeg = searchRadiusArcsec / 3600.0;

        foreach (var star in stars)
        {
            ct.ThrowIfCancellationRequested();

            // Detected centroid -- green circle.
            DrawCircle(renderer, star.XCentroid, star.YCentroid, DetectedRadiusPx, DetectedColor);

            var sky = wcs.PixelToSky(star.XCentroid + 1, star.YCentroid + 1);
            if (sky is not { } pos) continue;

            // Find the nearest catalog candidate within the search radius and
            // capture the photometry gates (B-V / V_Mag presence) so we can
            // mark "accepted by SPCC" vs "considered but rejected" distinctly.
            (double X, double Y)? bestPixel = null;
            var bestDistDeg = searchRadiusDeg;
            var bestHasBmv = false;
            var bestHasVmag = false;
            foreach (var idx in db.CoordinateGrid[pos.RA, pos.Dec])
            {
                if (!db.TryLookupByIndex(idx, out var obj)) continue;
                if (double.IsNaN(obj.RA) || double.IsNaN(obj.Dec)) continue;
                var distDeg = CoordinateUtils.AngularSeparationDeg(pos.RA, pos.Dec, obj.RA, obj.Dec);
                if (distDeg < bestDistDeg
                    && wcs.SkyToPixel(obj.RA, obj.Dec) is { } projected)
                {
                    bestDistDeg = distDeg;
                    bestPixel = projected;
                    bestHasBmv = !Half.IsNaN(obj.BMinusV);
                    bestHasVmag = !Half.IsNaN(obj.V_Mag);
                }
            }

            if (bestPixel is not { } proj) continue;

            // SPCC's accept set: within match tolerance AND non-NaN B-V AND non-NaN V_Mag.
            // Mirrors Tycho2ColorCalibration.MatchStars exactly so the overlay matches
            // what the photometric fit actually saw.
            var accepted = bestDistDeg <= matchRadiusDeg && bestHasBmv && bestHasVmag;

            DrawCross(renderer,
                (float)(proj.X - 1), (float)(proj.Y - 1),
                CrossArmPx,
                accepted ? CatalogAccepted : CatalogRejected);

            // Line from detected -> catalog, coloured by miss magnitude. Three bands so
            // the typical residual scale jumps out: green=match, yellow=close-but-rejected,
            // red=way off (lens distortion / wrong-star).
            var lineColor = bestDistDeg <= matchRadiusDeg ? LineMatch
                : bestDistDeg <= matchRadiusDeg * 3 ? LineClose
                : LineFar;
            renderer.DrawLine(
                star.XCentroid, star.YCentroid,
                (float)(proj.X - 1), (float)(proj.Y - 1),
                lineColor, thickness: 1);
        }

        // 3. Encode PNG with cICP sRGB (same colour signal the master previews carry,
        // 4 bytes instead of ~600-byte iCCP profile per PNG-3 §6.1 priority order).
        var png = PngWriter.Encode(renderer.Surface.Pixels, width, height, new PngWriteOptions { Cicp = CicpChunk.Srgb });
        await File.WriteAllBytesAsync(outputPath, png, ct);
    }

    private static void DrawCircle(RgbaImageRenderer renderer, float cx, float cy, int radius, RGBAColor32 color)
    {
        var x0 = (int)MathF.Round(cx) - radius;
        var y0 = (int)MathF.Round(cy) - radius;
        var x1 = x0 + 2 * radius;
        var y1 = y0 + 2 * radius;
        renderer.DrawEllipse(new RectInt(new PointInt(x0, y0), new PointInt(x1, y1)), color, strokeWidth: 1f);
    }

    private static void DrawCross(RgbaImageRenderer renderer, float cx, float cy, int armPx, RGBAColor32 color)
    {
        var xi = (int)MathF.Round(cx);
        var yi = (int)MathF.Round(cy);
        renderer.DrawLine(xi - armPx, yi, xi + armPx, yi, color, thickness: 1);
        renderer.DrawLine(xi, yi - armPx, xi, yi + armPx, color, thickness: 1);
    }

}
