using System;
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
public readonly record struct DiskPlacement(double CenterX, double CenterY, double EquatorialRadius, double NorthAngleDeg, bool Mirrored = false);

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
    /// <paramref name="supersample"/> squared rays across it, zero off the disk and on its night side.
    /// </summary>
    public static float[] Render(PlanetMap map, in PlanetAspect aspect, in DiskPlacement placement, int width, int height, double minnaertK, int supersample = 8)
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
        return image;
    }

    /// <summary>
    /// The scene through <paramref name="pupil"/> at <paramref name="wavelengthM"/>: rendered on a grid fine enough to sample
    /// the pupil's cutoff (at most <c>lambda / 2D</c>), convolved with the pupil's diffraction PSF there, and binned to
    /// pixels of <paramref name="arcsecPerPixel"/>, as a detector integrates. An image coarser than the cutoff's Nyquist
    /// scale, as every prime-focus capture is, would otherwise alias the PSF.
    /// </summary>
    public static float[] RenderDiffracted(PlanetMap map, in PlanetAspect aspect, in DiskPlacement placement, int width, int height, double minnaertK,
        in Pupil pupil, double wavelengthM, double arcsecPerPixel, int supersample = 4)
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
        var scene = Render(map, aspect, fine, fineWidth, fineHeight, minnaertK, supersample);

        const int PsfSize = 128;
        var psf = new double[PsfSize * PsfSize];
        var transmission = pupil.Rasterise(PsfSize, ShortExposurePsf.PupilSpacingFor(wavelengthM, fineScale, PsfSize));
        ShortExposurePsf.Compute(transmission, [], PsfSize, psf);
        var blurred = ConvolveCentred(scene, fineWidth, fineHeight, psf, PsfSize);

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
    /// The geometry of one render. The disk frame is the sky plane through the planet's centre: u toward the sky's WEST, v
    /// toward the projected north pole, w toward the observer, in equatorial radii. The body frame shares u (it lies in the
    /// equator, since the pole lies in the v-w plane) and adds e2, the equator's direction nearest the observer, and the pole.
    /// </summary>
    private readonly struct Scene
    {
        private readonly double _centerX, _centerY, _radius;
        private readonly double _northX, _northY, _westX, _westY;
        private readonly double _sinD, _cosD, _q2Inverse, _a;
        private readonly double _centralMeridian;
        private readonly double _sun1, _sun2, _sun3;
        private readonly double _k;

        public Scene(in PlanetAspect aspect, in DiskPlacement placement, double minnaertK)
        {
            _centerX = placement.CenterX;
            _centerY = placement.CenterY;
            _radius = placement.EquatorialRadius;
            var (sinN, cosN) = Math.SinCos(placement.NorthAngleDeg * Math.PI / 180);
            _northX = cosN;
            _northY = sinN;
            // West is north turned a quarter toward +y: with north up on a y-down screen, west is to the right, as the sky
            // looks to the eye. A mirrored image has it the other way.
            var mirror = placement.Mirrored ? -1 : 1;
            _westX = -sinN * mirror;
            _westY = cosN * mirror;

            var q = 1 - aspect.Flattening;
            var d = aspect.SubObserverLatitudeCentric * Math.PI / 180;
            (_sinD, _cosD) = Math.SinCos(d);
            _q2Inverse = 1 / (q * q);
            _a = (_cosD * _cosD) + (_sinD * _sinD * _q2Inverse);
            _centralMeridian = aspect.CentralMeridianIII;

            // The direction to the Sun, from the disk frame into the body frame: e1 is west, e2 = toward cos D - north sin D,
            // the pole = toward sin D + north cos D.
            var (west, north, toward) = PhysicalEphemeris.SunOnTheDisk(aspect);
            _sun1 = west;
            _sun2 = (toward * _cosD) - (north * _sinD);
            _sun3 = (toward * _sinD) + (north * _cosD);
            _k = minnaertK;
        }

        public double Radiance(PlanetMap map, double x, double y)
        {
            var dx = (x - _centerX) / _radius;
            var dy = (y - _centerY) / _radius;
            var u = (dx * _westX) + (dy * _westY);
            var v = (dx * _northX) + (dy * _northY);

            // The ray (u, v, t) meets the spheroid X1^2 + X2^2 + X3^2 / q^2 = 1, with X1 = u, X2 = t cos D - v sin D and
            // X3 = t sin D + v cos D; the nearer of the two meetings is the larger t.
            var b = 2 * v * _sinD * _cosD * (_q2Inverse - 1);
            var c = (u * u) + (v * v * _sinD * _sinD) + (v * v * _cosD * _cosD * _q2Inverse) - 1;
            var discriminant = (b * b) - (4 * _a * c);
            if (discriminant < 0)
            {
                return 0;
            }
            var t = (-b + Math.Sqrt(discriminant)) / (2 * _a);
            var x1 = u;
            var x2 = (t * _cosD) - (v * _sinD);
            var x3 = (t * _sinD) + (v * _cosD);

            // The normal, in the body frame; its latitude is the planetographic latitude.
            var n1 = x1;
            var n2 = x2;
            var n3 = x3 * _q2Inverse;
            var norm = Math.Sqrt((n1 * n1) + (n2 * n2) + (n3 * n3));
            n1 /= norm;
            n2 /= norm;
            n3 /= norm;

            var mu = (n2 * _cosD) + (n3 * _sinD);
            var mu0 = (n1 * _sun1) + (n2 * _sun2) + (n3 * _sun3);
            if (mu <= 0 || mu0 <= 0)
            {
                return 0;
            }

            // A point toward the sky's west has crossed the central meridian already, so its west longitude is the smaller.
            var west = _centralMeridian - (Math.Atan2(x1, x2) * 180 / Math.PI);
            var latitude = Math.Asin(Math.Clamp(n3, -1, 1)) * 180 / Math.PI;
            return map.Sample(latitude, west) * Math.Pow(mu0, _k) * Math.Pow(mu, _k - 1);
        }
    }
}
