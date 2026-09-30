using System;
using TianWen.Lib.Astrometry;

namespace TianWen.Lib.Imaging.Planetary;

/// <summary>
/// A planet's image carried from one instant to another by the planet's own rotation (docs/plans/planetary-restoration.md, R6):
/// each output pixel's planetographic latitude and west longitude are read at the target instant, found where they lay at the
/// source instant, and sampled there, through the oblate spheroid (<see cref="PlanetaryProjection"/>), never a flat image
/// rotation. The rotation is the ephemeris' own (System III, <see cref="PhysicalEphemeris"/>); over the minutes one night's
/// stacks span, Systems I, II and III differ by hundredths of a degree, and what is left is the zonal winds, which R6 measures
/// rather than fits.
/// <para>
/// What turns with the planet is its ALBEDO, never its brightness: the limb darkening and the Sun's lighting belong to the
/// viewing geometry, so a point carried along its latitude arrives with the lighting of where it was. Each sample is divided by
/// the lighting at its source and multiplied by the lighting at its target, by Minnaert's law with the limb fit's k
/// (<see cref="PlanetaryProjection.Minnaert"/>); carried as brightness, a 10-minute de-rotation of a rendered planet took out
/// only 46 % of the rotation's difference.
/// </para>
/// <para>
/// <b>A pixel is de-rotated only from a source inside <see cref="SourceRadiusLimit"/> of its disk</b>, and says so
/// (<see cref="Derotation.Covered"/>): nearer the limb a stack's brightness is its seeing-blurred edge, not Minnaert's law, so a
/// source there is relit by a model it does not follow. Over 31 minutes (18.8 degrees) the side the rotation turns into view
/// read its sources out there, and a de-rotation that took them doubled the difference it was meant to remove, its relit
/// samples reaching 195 times the stack's peak. Any other pixel keeps the source's pixel at the same place (the sky, and that
/// strip), and whatever combines de-rotated stacks takes it from a stack that covers it. Sampling is Lanczos-3, one resample,
/// since a stack's resampling kernel is its own blur (R5 part 3).
/// </para>
/// </summary>
public static class PlanetaryDerotation
{
    /// <summary>How far out, in equatorial radii of the source's disk, a de-rotated pixel may read its source.</summary>
    public const double SourceRadiusLimit = 0.9;

    /// <summary>
    /// <paramref name="image"/>, taken at <paramref name="from"/> with its disk where <paramref name="fromPlacement"/> says, as
    /// the planet would have looked at <paramref name="to"/> with its disk where <paramref name="toPlacement"/> puts it: a
    /// de-rotation and a registration in one resample, the lighting carried by Minnaert's law with <paramref name="minnaertK"/>.
    /// </summary>
    public static Derotation Derotate(Image image, in PlanetAspect from, in DiskPlacement fromPlacement, in PlanetAspect to, in DiskPlacement toPlacement, double minnaertK)
    {
        ArgumentNullException.ThrowIfNull(image);
        var target = new PlanetaryProjection(to, toPlacement);
        var source = new PlanetaryProjection(from, fromPlacement);
        var (width, height) = (image.Width, image.Height);

        // Where each output pixel reads its source, and the lighting's ratio between the two, once for every channel.
        var sourceX = new float[width * height];
        var sourceY = new float[width * height];
        var relight = new float[width * height];
        var covered = new bool[width * height];
        var (fromX, fromY, limit) = (fromPlacement.CenterX, fromPlacement.CenterY, SourceRadiusLimit * fromPlacement.EquatorialRadius);
        ParallelFor.Run(height, y =>
        {
            for (var x = 0; x < width; x++)
            {
                var i = (y * width) + x;
                if (target.TryUnproject(x, y, out var latitude, out var west) && source.TryProject(latitude, west, out var sx, out var sy)
                    && ((sx - fromX) * (sx - fromX)) + ((sy - fromY) * (sy - fromY)) < limit * limit
                    && source.Minnaert(sx, sy, minnaertK) is var lightFrom and > 0 && target.Minnaert(x, y, minnaertK) is var lightTo and > 0)
                {
                    (sourceX[i], sourceY[i], relight[i], covered[i]) = ((float)sx, (float)sy, (float)(lightTo / lightFrom), true);
                }
                else
                {
                    (sourceX[i], sourceY[i], relight[i]) = (x, y, 1f);
                }
            }
        });

        var planes = Image.CreateChannelData(image.ChannelCount, height, width);
        for (var c = 0; c < image.ChannelCount; c++)
        {
            var plane = image.GetChannelSpan(c).ToArray();
            var output = planes[c];
            ParallelFor.Run(height, y =>
            {
                for (var x = 0; x < width; x++)
                {
                    var i = (y * width) + x;
                    var value = Image.Lanczos3Value(plane, width, height, sourceX[i], sourceY[i]);
                    output[y, x] = float.IsNaN(value) ? plane[i] : value * relight[i];
                }
            });
        }
        return new Derotation(new Image(planes, BitDepth.Float32, image.MaxValue, image.MinValue, image.Pedestal, image.ImageMeta), covered);
    }

    /// <summary><see cref="Derotate(Image, in PlanetAspect, in DiskPlacement, in PlanetAspect, in DiskPlacement, double)"/> on one disk.</summary>
    public static Derotation Derotate(Image image, in PlanetAspect from, in PlanetAspect to, in DiskPlacement placement, double minnaertK)
        => Derotate(image, from, placement, to, placement, minnaertK);
}

/// <summary>
/// A de-rotated image, and which of its pixels were de-rotated (row-major): those that read their source inside
/// <see cref="PlanetaryDerotation.SourceRadiusLimit"/> of its disk. The rest are the source's own pixels, where they were.
/// </summary>
public sealed record Derotation(Image Image, bool[] Covered);
