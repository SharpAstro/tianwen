using System;
using System.Collections.Immutable;
using System.Numerics;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Imaging.Optics;
using TianWen.Lib.Stat;

namespace TianWen.Lib.Imaging.Planetary;

/// <summary>Where a planet's disk lies in an image, and which way round.</summary>
/// <param name="CenterX">The disk's centre, in pixels (a pixel's centre at its integer coordinates).</param>
/// <param name="CenterY">The disk's centre, in pixels.</param>
/// <param name="EquatorialRadius">The equatorial radius, in pixels.</param>
/// <param name="NorthAngleDeg">The direction from the centre toward the NORTH pole, from +x toward +y. It names the pole's end,
/// where <see cref="LimbFit.AxisAngleDeg"/> is only the axis.</param>
/// <param name="Mirrored">Whether the image is the sky's mirror image (an odd number of reflections, or a readout flipped in
/// one axis): east lies to the right of north instead of the left.</param>
public readonly record struct DiskPlacement(double CenterX, double CenterY, double EquatorialRadius, double NorthAngleDeg, bool Mirrored = false)
{
    /// <summary>The unit vector toward the north pole in the image.</summary>
    public (double X, double Y) North => (Math.Cos(NorthAngleDeg * Math.PI / 180), Math.Sin(NorthAngleDeg * Math.PI / 180));

    /// <summary>
    /// The unit vector toward the planet's west in the image: north turned a quarter toward +y, so with north up on a y-down screen west
    /// is to the right, as the sky looks to the eye. A mirrored image has it the other way.
    /// </summary>
    public (double X, double Y) West => Mirrored ? (Math.Sin(NorthAngleDeg * Math.PI / 180), -Math.Cos(NorthAngleDeg * Math.PI / 180))
        : (-Math.Sin(NorthAngleDeg * Math.PI / 180), Math.Cos(NorthAngleDeg * Math.PI / 180));

    /// <summary>The image point <paramref name="west"/> and <paramref name="north"/> equatorial radii from the centre.</summary>
    public (double X, double Y) ImagePoint(double west, double north) =>
        (CenterX + (EquatorialRadius * ((west * West.X) + (north * North.X))), CenterY + (EquatorialRadius * ((west * West.Y) + (north * North.Y))));
}

/// <summary>
/// A moon beside a planet's disk, as <see cref="GalileanMoon"/> gives it (docs/plans/planetary-restoration.md, R8 follow-up 4): a uniform
/// disk drawn wherever the planet's own disk is not.
/// </summary>
/// <param name="X">Its offset toward the planet's west, along the equator, in equatorial radii.</param>
/// <param name="Y">Its offset toward the planet's north, along the axis, in equatorial radii.</param>
/// <param name="Radius">Its radius, in the planet's equatorial radii.</param>
/// <param name="Level">Its surface brightness over the planet's disk's mean inside 0.8 radii.</param>
public readonly record struct MoonDisk(double X, double Y, double Radius, double Level)
{
    /// <summary>Jupiter's Galilean moons at <paramref name="utc"/> within <paramref name="withinRadii"/> of its centre, each at <paramref name="level"/>.</summary>
    public static ImmutableArray<MoonDisk> Galilean(DateTimeOffset utc, double withinRadii, double level)
    {
        var (moons, _) = GalileanMoons.At(utc);
        var builder = ImmutableArray.CreateBuilder<MoonDisk>();
        foreach (var moon in moons)
        {
            if (Math.Sqrt((moon.X * moon.X) + (moon.Y * moon.Y)) < withinRadii)
            {
                builder.Add(new MoonDisk(moon.X, moon.Y, moon.Radius, level));
            }
        }
        return builder.ToImmutable();
    }
}

/// <summary>
/// A planet rendered from its global map at an instant (docs/plans/planetary-restoration.md, T1 and R2): the oblate spheroid
/// the ephemeris gives, turned to its central meridian and sub-observer latitude, lit from its sub-solar point, with the
/// map's limb darkening put back by Minnaert's law (<c>I = albedo mu0^k mu^(k-1)</c>, which is how OPAL removed it).
/// <para>
/// Written apart from <see cref="PlanetaryLimbFit"/>, deliberately: it is the truth that fit is measured against, so it
/// shares none of the fit's model. Every pixel is a ray cast along the line of sight onto the spheroid; the surface normal
/// gives the planetographic latitude, the lighting (<c>mu</c>, <c>mu0</c>) and, with the central meridian, the west
/// longitude. <see cref="RenderDiffracted"/> then passes the scene through the telescope's pupil, which is what makes the
/// render the truth: what this telescope could have shown in perfect seeing, never more.
/// </para>
/// </summary>
public static class PlanetaryRender
{
    /// <summary>
    /// The scene alone, no optics: <paramref name="width"/> by <paramref name="height"/> pixels, row-major, each the mean of
    /// <paramref name="supersample"/> squared rays across it, zero off the disk and on its night side; and <paramref name="moons"/>, each a
    /// uniform disk where the planet's is not, as many rays to a pixel (R8 follow-up 4).
    /// </summary>
    public static float[] Render(PlanetMap map, in PlanetAspect aspect, in DiskPlacement placement, int width, int height, double minnaertK, int supersample = 8,
        ImmutableArray<MoonDisk> moons = default)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentOutOfRangeException.ThrowIfLessThan(supersample, 1);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(placement.EquatorialRadius);

        var scene = new Scene(aspect, placement, minnaertK);
        var image = new float[width * height];
        var step = 1.0 / supersample;
        ParallelFor.Run(height, y =>
        {
            for (var x = 0; x < width; x++)
            {
                var sum = 0.0;
                for (var j = 0; j < supersample; j++)
                {
                    var py = y - 0.5 + ((j + 0.5) * step);
                    for (var i = 0; i < supersample; i++)
                    {
                        sum += scene.Radiance(map, x - 0.5 + ((i + 0.5) * step), py);
                    }
                }
                image[(y * width) + x] = (float)(sum / (supersample * supersample));
            }
        });
        if (!moons.IsDefaultOrEmpty)
        {
            AddMoons(image, width, height, scene, placement, moons, supersample);
        }
        return image;
    }

    // Each moon at its level over the disk's mean inside 0.8 radii, on the rays that miss the planet's disk.
    private static void AddMoons(float[] image, int width, int height, in Scene scene, in DiskPlacement placement, ImmutableArray<MoonDisk> moons, int supersample)
    {
        double sum = 0;
        var count = 0;
        var inner = 0.64 * placement.EquatorialRadius * placement.EquatorialRadius;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var (dx, dy) = (x - placement.CenterX, y - placement.CenterY);
                if ((dx * dx) + (dy * dy) < inner)
                {
                    sum += image[(y * width) + x];
                    count++;
                }
            }
        }
        var mean = count > 0 ? sum / count : 0;
        var step = 1.0 / supersample;
        foreach (var moon in moons)
        {
            var (cx, cy) = placement.ImagePoint(moon.X, moon.Y);
            var r = moon.Radius * placement.EquatorialRadius;
            var value = moon.Level * mean;
            for (var y = Math.Max(0, (int)Math.Floor(cy - r - 1)); y <= Math.Min(height - 1, (int)Math.Ceiling(cy + r + 1)); y++)
            {
                for (var x = Math.Max(0, (int)Math.Floor(cx - r - 1)); x <= Math.Min(width - 1, (int)Math.Ceiling(cx + r + 1)); x++)
                {
                    var hits = 0;
                    for (var j = 0; j < supersample; j++)
                    {
                        var py = y - 0.5 + ((j + 0.5) * step);
                        for (var i = 0; i < supersample; i++)
                        {
                            var px = x - 0.5 + ((i + 0.5) * step);
                            if (((px - cx) * (px - cx)) + ((py - cy) * (py - cy)) < r * r && !scene.OnDisk(px, py))
                            {
                                hits++;
                            }
                        }
                    }
                    image[(y * width) + x] += (float)(value * hits / (supersample * supersample));
                }
            }
        }
    }

    /// <summary>
    /// The scene through <paramref name="pupil"/> at <paramref name="wavelengthM"/>: rendered on a grid fine enough to sample
    /// the pupil's cutoff (at most <c>lambda / 2D</c>), convolved with the pupil's diffraction PSF there, and binned to
    /// pixels of <paramref name="arcsecPerPixel"/>, as a detector integrates. An image coarser than the cutoff's Nyquist
    /// scale, as every prime-focus capture is, would otherwise alias the PSF.
    /// </summary>
    /// <remarks>
    /// The PSF is computed on a grid twice the fine frame (<see cref="DiffractionGridFor"/>) and the scene convolved over that grid's
    /// period, so the PSF's wing reaches the whole frame (#1213). A circular aperture's edge spread falls only as one over the distance,
    /// and the 128-sample PSF this used to be cut the glow past the limb to nothing beyond 1.6 radii and to 88 % at the limb itself.
    /// </remarks>
    public static float[] RenderDiffracted(PlanetMap map, in PlanetAspect aspect, in DiskPlacement placement, int width, int height, double minnaertK,
        in Pupil pupil, double wavelengthM, double arcsecPerPixel, int supersample = 4, ImmutableArray<MoonDisk> moons = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(arcsecPerPixel);
        var nyquistArcsec = wavelengthM / (2 * pupil.DiameterM) * ShortExposurePsf.ArcsecPerRadian;
        var factor = Math.Max(1, (int)Math.Ceiling(arcsecPerPixel / nyquistArcsec));
        var fineScale = arcsecPerPixel / factor;

        // A pixel x spans [x - 0.5, x + 0.5] and holds `factor` fine samples, the k-th centred at x - 0.5 + (k + 0.5) / factor:
        // fine coordinate f is image coordinate (f + 0.5) / factor - 0.5.
        var fine = placement with
        {
            CenterX = ((placement.CenterX + 0.5) * factor) - 0.5,
            CenterY = ((placement.CenterY + 0.5) * factor) - 0.5,
            EquatorialRadius = placement.EquatorialRadius * factor,
        };
        var fineWidth = width * factor;
        var fineHeight = height * factor;
        var scene = Render(map, aspect, fine, fineWidth, fineHeight, minnaertK, supersample, moons);

        var psfSize = DiffractionGridFor(fineWidth, fineHeight);
        var psf = new double[psfSize * psfSize];
        var transmission = pupil.Rasterise(psfSize, ShortExposurePsf.PupilSpacingFor(wavelengthM, fineScale, psfSize));
        ShortExposurePsf.Compute(transmission, [], psfSize, psf);
        var blurred = ConvolvePeriodic(scene, fineWidth, fineHeight, psf, psfSize);

        var image = new float[width * height];
        var norm = 1.0 / (factor * factor);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var sum = 0.0;
                for (var dy = 0; dy < factor; dy++)
                {
                    var row = ((y * factor) + dy) * fineWidth;
                    for (var dx = 0; dx < factor; dx++)
                    {
                        sum += blurred[row + (x * factor) + dx];
                    }
                }
                image[(y * width) + x] = (float)(sum * norm);
            }
        }
        return image;
    }

    /// <summary>
    /// The side of the grid a diffraction PSF is computed on for a <paramref name="width"/> by <paramref name="height"/> fine frame: a
    /// power of two at least twice the frame's larger side, so the PSF's wing reaches across the frame. The PSF comes from an FFT, so it
    /// is periodic on this grid, and the light that truly falls more than a frame away folds back into it: 0.06 % of the flux for a
    /// clear 254 mm aperture and a disk filling 60 % of a 128 px frame, 0.26 % with four 1 mm vanes, whose spikes reach farthest (#1213).
    /// </summary>
    internal static int DiffractionGridFor(int width, int height) => Math.Max(128, NextPowerOfTwo(2 * Math.Max(width, height)));

    // Convolves `image` with a kernel of `size` squared samples centred on (size / 2, size / 2) over the kernel's own period: the
    // kernel is the whole PSF on that grid, at least twice the image (DiffractionGridFor), so the scene outside the frame is black sky
    // and what wraps comes from a frame away.
    private static float[] ConvolvePeriodic(float[] image, int width, int height, double[] kernel, int size)
    {
        var a = new Complex[size * size];
        var b = new Complex[size * size];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                a[(y * size) + x] = new Complex(image[(y * width) + x], 0);
            }
        }
        var half = size / 2;
        for (var y = 0; y < size; y++)
        {
            var ty = (y - half + size) % size;
            for (var x = 0; x < size; x++)
            {
                b[(ty * size) + ((x - half + size) % size)] = new Complex(kernel[(y * size) + x], 0);
            }
        }
        Fft2D.Forward(a, size, size);
        Fft2D.Forward(b, size, size);
        for (var i = 0; i < a.Length; i++)
        {
            a[i] *= b[i];
        }
        Fft2D.Inverse(a, size, size);
        var result = new float[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                result[(y * width) + x] = (float)a[(y * size) + x].Real;
            }
        }
        return result;
    }

    // Convolves `image` with a kernel of `size` squared samples centred on (size / 2, size / 2), by FFT over a grid padded
    // past both, so nothing wraps: the scene outside the frame is black sky.
    private static float[] ConvolveCentred(float[] image, int width, int height, double[] kernel, int size)
    {
        var paddedWidth = NextPowerOfTwo(width + size);
        var paddedHeight = NextPowerOfTwo(height + size);
        var a = new Complex[paddedWidth * paddedHeight];
        var b = new Complex[paddedWidth * paddedHeight];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                a[(y * paddedWidth) + x] = new Complex(image[(y * width) + x], 0);
            }
        }
        var half = size / 2;
        for (var y = 0; y < size; y++)
        {
            var ty = ((y - half) + paddedHeight) % paddedHeight;
            for (var x = 0; x < size; x++)
            {
                var tx = ((x - half) + paddedWidth) % paddedWidth;
                b[(ty * paddedWidth) + tx] = new Complex(kernel[(y * size) + x], 0);
            }
        }
        Fft2D.Forward(a, paddedWidth, paddedHeight);
        Fft2D.Forward(b, paddedWidth, paddedHeight);
        for (var i = 0; i < a.Length; i++)
        {
            a[i] *= b[i];
        }
        Fft2D.Inverse(a, paddedWidth, paddedHeight);

        var result = new float[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                result[(y * width) + x] = (float)a[(y * paddedWidth) + x].Real;
            }
        }
        return result;
    }

    private static int NextPowerOfTwo(int n)
    {
        var p = 1;
        while (p < n)
        {
            p <<= 1;
        }
        return p;
    }

    /// <summary>
    /// The lighting of one render over its geometry, <see cref="PlanetaryProjection"/>: each ray's point on the spheroid, its
    /// latitude and longitude, and the Sun and observer angles there.
    /// </summary>
    private readonly struct Scene(in PlanetAspect aspect, in DiskPlacement placement, double minnaertK)
    {
        private readonly PlanetaryProjection _projection = new(aspect, placement);
        private readonly double _k = minnaertK;

        public double Radiance(PlanetMap map, double x, double y)
        {
            if (!_projection.TrySurface(x, y, out var latitude, out var west, out var mu, out var mu0) || mu <= 0 || mu0 <= 0)
            {
                return 0;
            }
            return map.Sample(latitude, west) * Math.Pow(mu0, _k) * Math.Pow(mu, _k - 1);
        }

        // Whether the ray meets the planet at all, day side or night.
        public bool OnDisk(double x, double y) => _projection.TrySurface(x, y, out _, out _, out _, out _);
    }
}
