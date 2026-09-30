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
/// only 46 % of the rotation's difference. A pixel the planet does not cover at the target keeps the source's pixel at the
/// same place (the sky, and the thin strip at the limb the rotation turns into view, which the source saw on the far side).
/// Sampling is Lanczos-3, one resample, since a stack's resampling kernel is its own blur (R5 part 3).
/// </para>
/// </summary>
public static class PlanetaryDerotation
{
    /// <summary>
    /// <paramref name="image"/>, taken at <paramref name="from"/> with its disk where <paramref name="fromPlacement"/> says, as
    /// the planet would have looked at <paramref name="to"/> with its disk where <paramref name="toPlacement"/> puts it: a
    /// de-rotation and a registration in one resample, the lighting carried by Minnaert's law with <paramref name="minnaertK"/>.
    /// </summary>
    public static Image Derotate(Image image, in PlanetAspect from, in DiskPlacement fromPlacement, in PlanetAspect to, in DiskPlacement toPlacement, double minnaertK)
    {
        ArgumentNullException.ThrowIfNull(image);
        var target = new PlanetaryProjection(to, toPlacement);
        var source = new PlanetaryProjection(from, fromPlacement);
        var (width, height) = (image.Width, image.Height);

        // Where each output pixel reads its source, and the lighting's ratio between the two, once for every channel.
        var sourceX = new float[width * height];
        var sourceY = new float[width * height];
        var relight = new float[width * height];
        ParallelFor.Run(height, y =>
        {
            for (var x = 0; x < width; x++)
            {
                var i = (y * width) + x;
                if (target.TryUnproject(x, y, out var latitude, out var west) && source.TryProject(latitude, west, out var sx, out var sy)
                    && source.Minnaert(sx, sy, minnaertK) is var lightFrom and > 0 && target.Minnaert(x, y, minnaertK) is var lightTo and > 0)
                {
                    (sourceX[i], sourceY[i], relight[i]) = ((float)sx, (float)sy, (float)(lightTo / lightFrom));
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
        return new Image(planes, BitDepth.Float32, image.MaxValue, image.MinValue, image.Pedestal, image.ImageMeta);
    }

    /// <summary><see cref="Derotate(Image, in PlanetAspect, in DiskPlacement, in PlanetAspect, in DiskPlacement, double)"/> on one disk.</summary>
    public static Image Derotate(Image image, in PlanetAspect from, in PlanetAspect to, in DiskPlacement placement, double minnaertK)
        => Derotate(image, from, placement, to, placement, minnaertK);
}
