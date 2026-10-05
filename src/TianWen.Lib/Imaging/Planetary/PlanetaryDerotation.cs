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
/// <para>
/// The same rule carries a frame to its capture's epoch inside the stacker (R6 part 2, <see cref="PlanetaryDerotationOptions"/>),
/// through a <see cref="DerotationField"/> the stack's own resample applies: there the strip past the limit is the frame's
/// pixel where it lies, so a stack's rim is the rotation's average and its inside the rotation taken out.
/// </para>
/// <para>
/// <b>Saturn's rings do not turn with its globe</b> (S5, #1234): they are the same all the way round, so what the rotation moves
/// is the globe alone. A pixel a ring the observer sees covers (<see cref="MetricDisk.RingTouched"/>: off the globe, or across it on
/// the near side) keeps its own pixel at either end, and no globe pixel reads its source from under one.
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
        var field = new DerotationTarget(to, toPlacement, image.Width, image.Height, minnaertK).FieldFrom(from, fromPlacement);
        return new Derotation(Resample(image, field), field.CoveredMask());
    }

    /// <summary><see cref="Derotate(Image, in PlanetAspect, in DiskPlacement, in PlanetAspect, in DiskPlacement, double)"/> on one disk.</summary>
    public static Derotation Derotate(Image image, in PlanetAspect from, in PlanetAspect to, in DiskPlacement placement, double minnaertK)
        => Derotate(image, from, placement, to, placement, minnaertK);

    /// <summary>
    /// <paramref name="image"/> resampled through <paramref name="field"/> by Lanczos-3: each output pixel its source's sample,
    /// relit; where the kernel cannot reach, the pixel at the same place.
    /// </summary>
    public static Image Resample(Image image, DerotationField field)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(field);
        var (width, height) = (image.Width, image.Height);
        if (field.Width != width || field.Height != height)
        {
            throw new ArgumentException($"A {field.Width} x {field.Height} field cannot resample a {width} x {height} image.", nameof(field));
        }
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
                    var value = Image.Lanczos3Value(plane, width, height, field.SourceX[i], field.SourceY[i]);
                    output[y, x] = float.IsNaN(value) ? plane[i] : value * field.Relight[i];
                }
            });
        }
        return new Image(planes, BitDepth.Float32, image.MaxValue, image.MinValue, image.Pedestal, image.ImageMeta);
    }

    /// <summary>
    /// A turn of the central meridian under this many degrees between two stacks leaves their north as the limb fit has it: either way
    /// round carries the planet too little to matter, or to tell (<see cref="AgreementBothWays"/>).
    /// </summary>
    public const double LeastTurnToTellNorthDeg = 1;

    /// <summary>
    /// Which way round a planet turns (R6 part 2): <paramref name="earlier"/> carried from <paramref name="from"/>'s instant to
    /// <paramref name="to"/>'s and set against <paramref name="later"/>, both on <paramref name="placement"/>'s disk, with its north as
    /// given and turned over. The RMS apart each way over the pixels the carry covers (<see cref="DifferenceRms"/>); the smaller is the
    /// north. Near opposition a limb fit's north can be its south, and a de-rotation turned the wrong way turns the planet backwards, so
    /// north comes from this agreement, never the fit alone. A caller checks the turn first (<see cref="LeastTurnToTellNorthDeg"/>).
    /// </summary>
    public static (double AsGiven, double TurnedOver) AgreementBothWays(Image earlier, in PlanetAspect from, Image later, in PlanetAspect to,
        in DiskPlacement placement, double minnaertK)
    {
        ArgumentNullException.ThrowIfNull(earlier);
        ArgumentNullException.ThrowIfNull(later);
        return (Apart(earlier, from, later, to, placement, minnaertK),
            Apart(earlier, from, later, to, placement with { NorthAngleDeg = placement.NorthAngleDeg + 180 }, minnaertK));

        static double Apart(Image earlier, in PlanetAspect from, Image later, in PlanetAspect to, in DiskPlacement disk, double k)
        {
            var carried = Derotate(earlier, from, to, disk, k);
            return DifferenceRms(carried.Image, later, disk, carried.Covered).Rms;
        }
    }

    /// <summary>
    /// How far apart two images of one planet on one disk are: the RMS of their difference inside
    /// <see cref="SourceRadiusLimit"/> of <paramref name="disk"/> and over the pixels <paramref name="over"/> names (row-major),
    /// each as the mean of its channels on its own disk level (<see cref="PlanetaryMetrics.Normalise"/>), so two captures'
    /// gains are not counted; and how many pixels that was. What a de-rotation is judged by, and what decides a stack's north.
    /// </summary>
    public static (double Rms, int Pixels) DifferenceRms(Image a, Image b, in DiskPlacement disk, ReadOnlySpan<bool> over)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        var (width, height) = (a.Width, a.Height);
        if (b.Width != width || b.Height != height || over.Length != width * height)
        {
            throw new ArgumentException($"Two {width} x {height} images and a mask of as many pixels are compared; got {b.Width} x {b.Height} and {over.Length}.");
        }
        var metric = new MetricDisk(disk.CenterX, disk.CenterY, disk.EquatorialRadius);
        var region = new Geometry.PixelRect(0, 0, width, height);
        var (lumaA, lumaB) = (new float[width * height], new float[width * height]);
        LumaProxy.Fill(a, region, lumaA);
        LumaProxy.Fill(b, region, lumaB);
        var (pa, pb) = (PlanetaryMetrics.Normalise(lumaA, width, height, metric), PlanetaryMetrics.Normalise(lumaB, width, height, metric));
        double sum = 0;
        var count = 0;
        var reach = SourceRadiusLimit * disk.EquatorialRadius;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = (y * width) + x;
                var (dx, dy) = (x - disk.CenterX, y - disk.CenterY);
                if ((dx * dx) + (dy * dy) < reach * reach && over[i])
                {
                    var d = pa[i] - pb[i];
                    sum += d * d;
                    count++;
                }
            }
        }
        return (count == 0 ? double.NaN : Math.Sqrt(sum / count), count);
    }
}

/// <summary>
/// A de-rotated image, and which of its pixels were de-rotated (row-major): those that read their source inside
/// <see cref="PlanetaryDerotation.SourceRadiusLimit"/> of its disk. The rest are the source's own pixels, where they were.
/// </summary>
public sealed record Derotation(Image Image, bool[] Covered);

/// <summary>
/// The target half of a de-rotation, worked out once: every output pixel's planetographic latitude, west longitude and lighting at
/// the target instant, on the target's disk. A stack carries thousands of frames to one epoch, and what differs between them is
/// only where each output pixel lay at the frame's own instant (<see cref="FillFrom"/>).
/// </summary>
public sealed class DerotationTarget
{
    private readonly int _width;
    private readonly int _height;
    private readonly double _minnaertK;
    private readonly double[] _latitude;
    private readonly double[] _west;
    private readonly double[] _lightTo;

    /// <summary>The planet at <paramref name="to"/> on a <paramref name="width"/> x <paramref name="height"/> grid, its disk where <paramref name="toPlacement"/> puts it.</summary>
    public DerotationTarget(in PlanetAspect to, in DiskPlacement toPlacement, int width, int height, double minnaertK)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        (_width, _height, _minnaertK) = (width, height, minnaertK);
        var target = new PlanetaryProjection(to, toPlacement);
        var rings = RingsOf(to, toPlacement);
        (_latitude, _west, _lightTo) = (new double[width * height], new double[width * height], new double[width * height]);
        var (latitudes, wests, lights) = (_latitude, _west, _lightTo);
        ParallelFor.Run(height, y =>
        {
            for (var x = 0; x < width; x++)
            {
                var i = (y * width) + x;
                // A pixel off the disk, dark, or under a ring is never de-rotated; NaN says so to every source instant.
                if (target.TryUnproject(x, y, out var latitude, out var west) && target.Minnaert(x, y, minnaertK) is var lightTo and > 0
                    && rings?.RingTouched(x, y) is not true)
                {
                    (latitudes[i], wests[i], lights[i]) = (latitude, west, lightTo);
                }
                else
                {
                    (latitudes[i], wests[i], lights[i]) = (double.NaN, double.NaN, 0);
                }
            }
        });
    }

    // Saturn's rings about a globe where `placement` puts it at `aspect`, as the metrics read them (its north is the disk's axis); null for
    // a planet without rings.
    private static MetricDisk? RingsOf(in PlanetAspect aspect, in DiskPlacement placement)
    {
        var options = PlanetaryLimbFit.OptionsFor(aspect);
        return options.Rings is { } rings
            ? new MetricDisk(placement.CenterX, placement.CenterY, placement.EquatorialRadius, options.AxisRatio, placement.NorthAngleDeg)
            {
                Rings = DiskRings.Of(placement.NorthAngleDeg, placement.NorthAngleDeg, options, rings),
            }
            : null;
    }

    /// <summary>The grid's width.</summary>
    public int Width => _width;

    /// <summary>The grid's height.</summary>
    public int Height => _height;

    /// <summary>A new field reading every pixel from the planet at <paramref name="from"/>, its disk where <paramref name="fromPlacement"/> puts it.</summary>
    public DerotationField FieldFrom(in PlanetAspect from, in DiskPlacement fromPlacement)
    {
        var field = new DerotationField(_width, _height);
        FillFrom(from, fromPlacement, field);
        return field;
    }

    /// <summary>
    /// Fills <paramref name="field"/> (this grid's size) with where each output pixel reads its source when the planet is at
    /// <paramref name="from"/> with its disk where <paramref name="fromPlacement"/> puts it, and the lighting's ratio between the
    /// two, by the rule <see cref="PlanetaryDerotation"/> states.
    /// </summary>
    public void FillFrom(in PlanetAspect from, in DiskPlacement fromPlacement, DerotationField field)
    {
        ArgumentNullException.ThrowIfNull(field);
        if (field.Width != _width || field.Height != _height)
        {
            throw new ArgumentException($"A {field.Width} x {field.Height} field for a {_width} x {_height} target.", nameof(field));
        }
        var source = new PlanetaryProjection(from, fromPlacement);
        var rings = RingsOf(from, fromPlacement);
        var (fromX, fromY, limit) = (fromPlacement.CenterX, fromPlacement.CenterY, PlanetaryDerotation.SourceRadiusLimit * fromPlacement.EquatorialRadius);
        var (width, k, latitudes, wests, lights) = (_width, _minnaertK, _latitude, _west, _lightTo);
        var (sourceX, sourceY, relight, covered) = (field.SourceX, field.SourceY, field.Relight, field.Covered);
        ParallelFor.Run(_height, y =>
        {
            for (var x = 0; x < width; x++)
            {
                var i = (y * width) + x;
                if (lights[i] > 0 && source.TryProject(latitudes[i], wests[i], out var sx, out var sy)
                    && ((sx - fromX) * (sx - fromX)) + ((sy - fromY) * (sy - fromY)) < limit * limit
                    && source.Minnaert(sx, sy, k) is var lightFrom and > 0
                    && rings?.RingTouched(sx, sy) is not true)
                {
                    (sourceX[i], sourceY[i], relight[i], covered[i]) = ((float)sx, (float)sy, (float)(lights[i] / lightFrom), true);
                }
                else
                {
                    (sourceX[i], sourceY[i], relight[i], covered[i]) = (x, y, 1f, false);
                }
            }
        });
    }
}

/// <summary>
/// Where each pixel of a grid reads its source through a de-rotation, row-major: the source's position, the lighting's ratio
/// its sample is multiplied by, and whether it was de-rotated at all (<see cref="PlanetaryDerotation.SourceRadiusLimit"/>); a
/// pixel that was not reads the pixel at its own place, unrelit. A stack's displacement mesh adds it to each frame's
/// registration (<see cref="DisplacementMesh"/>), so its offsets are what it carries: <see cref="OffsetAt"/>.
/// </summary>
public sealed class DerotationField
{
    /// <summary>An identity field over a <paramref name="width"/> x <paramref name="height"/> grid, to be filled.</summary>
    public DerotationField(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        (Width, Height) = (width, height);
        (SourceX, SourceY, Relight, Covered) = (new float[width * height], new float[width * height], new float[width * height], new bool[width * height]);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = (y * width) + x;
                (SourceX[i], SourceY[i], Relight[i]) = (x, y, 1f);
            }
        }
    }

    /// <summary>The grid's width.</summary>
    public int Width { get; }

    /// <summary>The grid's height.</summary>
    public int Height { get; }

    internal float[] SourceX { get; }

    internal float[] SourceY { get; }

    internal float[] Relight { get; }

    internal bool[] Covered { get; }

    /// <summary>Which pixels were de-rotated, as a copy.</summary>
    public bool[] CoveredMask() => (bool[])Covered.Clone();

    /// <summary>
    /// How far the source of output position (<paramref name="x"/>, <paramref name="y"/>) lies from it, interpolated bilinearly
    /// between pixels (and exactly at one), clamped at the edges.
    /// </summary>
    public (float OffsetX, float OffsetY) OffsetAt(float x, float y)
    {
        var (i00, i10, i01, i11, fx, fy, x0, y0, x1, y1) = Corners(x, y);
        var ox = Bilinear(SourceX[i00] - x0, SourceX[i10] - x1, SourceX[i01] - x0, SourceX[i11] - x1, fx, fy);
        var oy = Bilinear(SourceY[i00] - y0, SourceY[i10] - y0, SourceY[i01] - y1, SourceY[i11] - y1, fx, fy);
        return (ox, oy);
    }

    /// <summary>The lighting's ratio at output position (<paramref name="x"/>, <paramref name="y"/>), interpolated as <see cref="OffsetAt"/> is.</summary>
    public float RelightAt(float x, float y)
    {
        var (i00, i10, i01, i11, fx, fy, _, _, _, _) = Corners(x, y);
        return Bilinear(Relight[i00], Relight[i10], Relight[i01], Relight[i11], fx, fy);
    }

    private (int I00, int I10, int I01, int I11, float Fx, float Fy, int X0, int Y0, int X1, int Y1) Corners(float x, float y)
    {
        var cx = Math.Clamp(x, 0, Width - 1);
        var cy = Math.Clamp(y, 0, Height - 1);
        var x0 = (int)cx;
        var y0 = (int)cy;
        var x1 = Math.Min(x0 + 1, Width - 1);
        var y1 = Math.Min(y0 + 1, Height - 1);
        return ((y0 * Width) + x0, (y0 * Width) + x1, (y1 * Width) + x0, (y1 * Width) + x1, cx - x0, cy - y0, x0, y0, x1, y1);
    }

    private static float Bilinear(float v00, float v10, float v01, float v11, float fx, float fy)
        => (v00 * (1 - fx) * (1 - fy)) + (v10 * fx * (1 - fy)) + (v01 * (1 - fx) * fy) + (v11 * fx * fy);
}
