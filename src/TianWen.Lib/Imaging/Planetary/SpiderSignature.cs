using System;
using System.Collections.Generic;
using System.Numerics;
using TianWen.Lib.Stat;

namespace TianWen.Lib.Imaging.Planetary;

/// <summary>What <see cref="SpiderSignature.Measure"/> found in a stack's halo.</summary>
/// <param name="Statistic">The mean power of harmonics 4 and 8 over the mean power of harmonics 3, 5, 6, 7, 9, 10 and 11.</param>
/// <param name="InnerRadius">The annulus' inner radius, in pixels.</param>
/// <param name="OuterRadius">Its outer radius: the nearest frame edge.</param>
/// <param name="SpikeAngleDeg">The four-fold pattern's brightest direction, from +x toward +y, modulo 90 degrees.</param>
public readonly record struct SpiderMeasurement(double Statistic, double InnerRadius, double OuterRadius, double SpikeAngleDeg);

/// <summary>
/// A four-vane spider's diffraction spikes, found in a planet stack's halo (docs/plans/planetary-restoration.md, R1): the
/// azimuthal harmonics of the sky in a full annulus around the disk, from <see cref="InnerRadii"/> disk radii out to the
/// nearest frame edge. It must be a FULL annulus, since a rectangle's corners reach farther out along its diagonals and would
/// print a four-fold pattern of their own. Harmonics 1 and 2 are left out, because an off-centre disk and an oblate one make
/// them, and so is Saturn, for its rings.
/// <para>
/// The profile is ROBUST: each pixel becomes a z-score against its own radius (the median and the scaled median absolute
/// deviation of that one-pixel ring), and each angle bin takes the MEDIAN of its z-scores over the radii. A spike runs
/// through every radius of its direction and survives the median; a Galilean moon or a hot pixel sits at a few radii and
/// does not. With a plain mean, three moons in 2022-09-03's annulus put power into every harmonic and read a spider-less 0.4
/// beside a four-armed pattern plain to the eye; the z-score also takes the halo's steep fall with radius out, which would
/// otherwise let the innermost radii speak for the whole bin.
/// </para>
/// <para>
/// The rings are ELLIPTICAL, the disk's own shape: an oblate disk's halo is brighter along its equator, and compared on circles
/// that printed a four-fold pattern locked to the disk's axis (4.5 on a pupil with no spider, its peak 2 degrees from the
/// synthetic disk's axis). The angle bins stay in the real angle, since a spike is a straight line whatever the disk's shape.
/// </para>
/// </summary>
public static class SpiderSignature
{
    /// <summary>The annulus starts this many disk radii out, clear of the disk's own blurred edge.</summary>
    public const double InnerRadii = 1.3;

    /// <summary>A spider's statistic is above this.</summary>
    public const double SpiderAbove = 10;

    /// <summary>
    /// A pupil with no spider reads below this on the synthetic check. The converse does not hold on real captures, so a low
    /// statistic is never read as proof there is no spider: 2022-09-03's Blue read 2.4 two minutes after its Red read 18.3
    /// through the same Newtonian.
    /// </summary>
    public const double NoneBelow = 3;

    private const int Bins = 72;
    private static readonly int[] SignalHarmonics = [4, 8];
    private static readonly int[] ReferenceHarmonics = [3, 5, 6, 7, 9, 10, 11];

    /// <summary>
    /// Measures the halo of <paramref name="image"/> (row-major) around a disk at (<paramref name="centerX"/>,
    /// <paramref name="centerY"/>) of equatorial <paramref name="radius"/> pixels, polar over equatorial <paramref name="axisRatio"/>
    /// and its POLE along <paramref name="axisAngleDeg"/> (from +x toward +y, as <see cref="LimbFit.AxisAngleDeg"/>), or null
    /// when no full annulus fits in the frame or some direction has no ring with any spread (a sky clipped flat).
    /// </summary>
    public static SpiderMeasurement? Measure(ReadOnlySpan<float> image, int width, int height, double centerX, double centerY, double radius, double axisRatio = 1, double axisAngleDeg = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(image.Length, width * height);
        var inner = InnerRadii * radius;
        var outer = Math.Min(Math.Min(centerX, centerY), Math.Min(width - 1 - centerX, height - 1 - centerY));
        if (outer < inner + 4)
        {
            return null;
        }

        // Each one-pixel elliptical ring's pixels, with the elliptical radius (in equatorial pixels) and the angle bin of each. The
        // ellipse of equatorial radius `outer` lies inside the circle of that radius, so the rings stay whole in the frame.
        var (axisSin, axisCos) = Math.SinCos(axisAngleDeg * Math.PI / 180);
        var ringCount = (int)Math.Floor(outer) - (int)Math.Floor(inner) + 1;
        var ringValues = new List<float>[ringCount];
        var ringRadii = new List<double>[ringCount];
        var ringBins = new List<int>[ringCount];
        for (var k = 0; k < ringCount; k++)
        {
            ringValues[k] = [];
            ringRadii[k] = [];
            ringBins[k] = [];
        }
        var firstRing = (int)Math.Floor(inner);
        for (var y = (int)Math.Floor(centerY - outer); y <= (int)Math.Ceiling(centerY + outer); y++)
        {
            if (y < 0 || y >= height)
            {
                continue;
            }
            var dy = y - centerY;
            for (var x = (int)Math.Floor(centerX - outer); x <= (int)Math.Ceiling(centerX + outer); x++)
            {
                if (x < 0 || x >= width)
                {
                    continue;
                }
                var dx = x - centerX;
                // Along the axis the disk reaches only its polar radius, so that coordinate is stretched by the axis ratio.
                var polar = ((dx * axisCos) + (dy * axisSin)) / axisRatio;
                var equatorial = (-dx * axisSin) + (dy * axisCos);
                var r = Math.Sqrt((polar * polar) + (equatorial * equatorial));
                if (r < inner || r > outer)
                {
                    continue;
                }
                var k = (int)Math.Floor(r) - firstRing;
                ringValues[k].Add(image[(y * width) + x]);
                ringRadii[k].Add(r);
                ringBins[k].Add((int)Math.Floor((Math.Atan2(dy, dx) + Math.PI) / (2 * Math.PI) * Bins) % Bins);
            }
        }

        // The radial profile: each ring's median, at its pixels' mean radius. A pixel is compared with the profile interpolated
        // at its OWN radius, not with its ring's median: within a one-pixel ring the square grid puts the pixels along the axes
        // and along the diagonals at different radii, and with the halo falling steeply a ring median printed that as a four-fold
        // pattern of its own (4.2 on a pupil with no spider).
        var profileRadius = new double[ringCount];
        var profileValue = new double[ringCount];
        var usable = 0;
        for (var k = 0; k < ringCount; k++)
        {
            if (ringValues[k].Count < 8)
            {
                continue;
            }
            var scratch = ringValues[k].ToArray();
            profileValue[usable] = StatisticsHelper.NthSmallest(scratch, scratch.Length / 2);
            var radiusSum = 0.0;
            foreach (var r in ringRadii[k])
            {
                radiusSum += r;
            }
            profileRadius[usable] = radiusSum / ringRadii[k].Count;
            usable++;
        }
        if (usable < 2)
        {
            return null;
        }

        // The z-score of every pixel against the profile at its radius, scaled by its ring's spread, gathered per angle bin.
        var binScores = new List<float>[Bins];
        for (var bin = 0; bin < Bins; bin++)
        {
            binScores[bin] = [];
        }
        for (var k = 0; k < ringCount; k++)
        {
            var values = ringValues[k];
            if (values.Count < 8)
            {
                continue;
            }
            var residuals = new float[values.Count];
            for (var i = 0; i < values.Count; i++)
            {
                residuals[i] = values[i] - (float)ProfileAt(profileRadius.AsSpan(0, usable), profileValue.AsSpan(0, usable), ringRadii[k][i]);
            }
            var scratch = new float[residuals.Length];
            for (var i = 0; i < residuals.Length; i++)
            {
                scratch[i] = Math.Abs(residuals[i]);
            }
            var spread = 1.4826f * StatisticsHelper.NthSmallest(scratch, scratch.Length / 2);
            if (!(spread > 0))
            {
                continue;
            }
            for (var i = 0; i < values.Count; i++)
            {
                binScores[ringBins[k][i]].Add(residuals[i] / spread);
            }
        }

        Span<double> profile = stackalloc double[Bins];
        for (var bin = 0; bin < Bins; bin++)
        {
            var scores = binScores[bin];
            if (scores.Count == 0)
            {
                return null;
            }
            var array = scores.ToArray();
            profile[bin] = StatisticsHelper.NthSmallest(array, array.Length / 2);
        }

        var signal = MeanPower(profile, SignalHarmonics);
        var reference = MeanPower(profile, ReferenceHarmonics);
        var fourfold = Harmonic(profile, 4);
        // A pattern cos 4(theta - alpha) has the fourth harmonic's phase -4 alpha. The bins start at -pi, not 0, which shifts that
        // phase by 4 pi: a whole number of turns.
        var peakDeg = (((-fourfold.Phase / 4) * 180 / Math.PI % 90) + 90) % 90;
        return new SpiderMeasurement(reference > 0 ? signal / reference : double.PositiveInfinity, inner, outer, peakDeg);
    }

    // The radial profile linearly interpolated at r (held at its ends).
    private static double ProfileAt(ReadOnlySpan<double> radii, ReadOnlySpan<double> values, double r)
    {
        if (r <= radii[0])
        {
            return values[0];
        }
        for (var i = 1; i < radii.Length; i++)
        {
            if (r <= radii[i])
            {
                var t = (r - radii[i - 1]) / (radii[i] - radii[i - 1]);
                return values[i - 1] + (t * (values[i] - values[i - 1]));
            }
        }
        return values[^1];
    }

    private static double MeanPower(ReadOnlySpan<double> profile, ReadOnlySpan<int> harmonics)
    {
        var total = 0.0;
        foreach (var m in harmonics)
        {
            var h = Harmonic(profile, m);
            total += (h.Real * h.Real) + (h.Imaginary * h.Imaginary);
        }
        return total / harmonics.Length;
    }

    private static Complex Harmonic(ReadOnlySpan<double> profile, int m)
    {
        var acc = Complex.Zero;
        for (var bin = 0; bin < profile.Length; bin++)
        {
            var theta = 2 * Math.PI * (bin + 0.5) / profile.Length;
            acc += profile[bin] * Complex.FromPolarCoordinates(1, -m * theta);
        }
        return acc / profile.Length;
    }
}
