using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;

namespace TianWen.Lib.Imaging.StarRemoval;

/// <summary>
/// The catalogued stars of a plate-solved frame, for <see cref="CoreStars"/> (#2242): from the star database ASTAP installs
/// where there is one (Gaia, to about magnitude 17 at its density cap), else from the embedded Tycho-2 (to about magnitude
/// 11, which holds the bright stars a core is most marked by). Magnitudes are each catalogue's own (Gaia BP, or Tycho's V);
/// <see cref="CoreStars"/> reads its zero point off the same list, so the two are never mixed.
/// </summary>
public static class CatalogFrameStars
{
    /// <summary>The stars and where they came from.</summary>
    /// <param name="Stars">The catalogued stars on the frame.</param>
    /// <param name="Source">What was read, for a message.</param>
    public sealed record Result(IReadOnlyList<FrameStar> Stars, string Source);

    /// <summary>
    /// The catalogued stars on a frame of <paramref name="width"/> by <paramref name="height"/> pixels solved as
    /// <paramref name="wcs"/>. Null with the reason when the solution cannot place the frame or no catalogue is at hand.
    /// </summary>
    /// <param name="installed">The star database ASTAP installed (<see cref="AstapStarDatabase.FindInstalled"/>), asked
    /// first; null where there is none.</param>
    /// <param name="embedded">The embedded catalogue, asked when there is no installed database; null to ask only for that.</param>
    public static async ValueTask<(Result? Stars, string? Why)> ReadAsync(
        WCS wcs, int width, int height, AstapStarDatabase? installed, ICelestialObjectDB? embedded, CancellationToken cancellationToken = default)
    {
        if (wcs.PixelToSky((width - 1) / 2.0, (height - 1) / 2.0) is not { } centre)
        {
            return (null, "the plate solution cannot place the frame on the sky");
        }
        if (installed is { } database)
        {
            if (await database.ReadFrameAsync(wcs, width, height, float.MaxValue, cancellationToken).ConfigureAwait(false) is not { } frame)
            {
                return (null, "the plate solution cannot place the frame on the sky");
            }
            var stars = new List<FrameStar>(frame.Stars.Length);
            foreach (var star in frame.Stars)
            {
                stars.Add(new FrameStar((float)star.X, (float)star.Y, star.Star.Magnitude));
            }
            return (new Result(stars, $"the installed ASTAP database {database.Name}, {stars.Count} stars"), null);
        }
        if (embedded is not { } objectDb)
        {
            return (null, "no ASTAP star database is installed");
        }
        // The frame's reach on the sky: its centre to its farthest corner.
        var radius = 0.0;
        (double X, double Y)[] corners = [(-0.5, -0.5), (width - 0.5, -0.5), (-0.5, height - 0.5), (width - 0.5, height - 0.5)];
        foreach (var (x, y) in corners)
        {
            if (wcs.PixelToSky(x, y) is not { } corner)
            {
                return (null, "the plate solution cannot place the frame on the sky");
            }
            radius = Math.Max(radius, SeparationDegrees(centre.RA, centre.Dec, corner.RA, corner.Dec));
        }
        await objectDb.InitDBAsync(waitForTycho2BulkLoad: true, cancellationToken).ConfigureAwait(false);
        var onFrame = new List<FrameStar>();
        foreach (var star in CatalogStarCounter.EnumerateFieldStars(objectDb, centre.RA, centre.Dec, radius * 1.01))
        {
            if (wcs.SkyToPixel(star.RA, (double)star.Dec) is { } at && at.X >= -0.5 && at.X < width - 0.5 && at.Y >= -0.5 && at.Y < height - 0.5)
            {
                onFrame.Add(new FrameStar((float)at.X, (float)at.Y, (float)star.V_Mag));
            }
        }
        return (new Result(onFrame, $"the embedded catalogue (Tycho-2), {onFrame.Count} stars"), null);
    }

    private static double SeparationDegrees(double raHoursA, double decA, double raHoursB, double decB)
    {
        var (sinA, cosA) = Math.SinCos(double.DegreesToRadians(decA));
        var (sinB, cosB) = Math.SinCos(double.DegreesToRadians(decB));
        var cos = (sinA * sinB) + (cosA * cosB * Math.Cos((raHoursA - raHoursB) * Math.PI / 12));
        return double.RadiansToDegrees(Math.Acos(Math.Clamp(cos, -1, 1)));
    }
}
