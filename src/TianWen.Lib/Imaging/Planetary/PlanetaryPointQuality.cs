using System;

namespace TianWen.Lib.Imaging.Planetary;

/// <summary>
/// A point of the disk, frame after frame (docs/plans/planetary-restoration.md, R4 per-point, #1071): how much of its quality is its own
/// rather than its frame's, a patch cut where a frame put the point, and a patch's gain in an a trous band against a reference patch.
/// </summary>
public static class PlanetaryPointQuality
{
    /// <summary>
    /// The share of a quality's variance that is each point's own: <paramref name="quality"/> frame by frame and, within a frame, point
    /// by point. Each point's mean over the frames is taken out first (its patch's own contrast, which never changes); of what is left, the
    /// share that remains once each frame's mean over its points is taken out too. Zero when every point follows its frame, one when no
    /// frame's points move together.
    /// </summary>
    public static double OwnShare(ReadOnlySpan<double> quality, int frames, int points)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(quality.Length, frames * points);
        ArgumentOutOfRangeException.ThrowIfLessThan(frames, 2);
        ArgumentOutOfRangeException.ThrowIfLessThan(points, 2);
        var pointMeans = new double[points];
        for (var t = 0; t < frames; t++)
        {
            for (var p = 0; p < points; p++)
            {
                pointMeans[p] += quality[(t * points) + p] / frames;
            }
        }
        double before = 0, after = 0;
        for (var t = 0; t < frames; t++)
        {
            double frameMean = 0;
            for (var p = 0; p < points; p++)
            {
                frameMean += (quality[(t * points) + p] - pointMeans[p]) / points;
            }
            for (var p = 0; p < points; p++)
            {
                var centred = quality[(t * points) + p] - pointMeans[p];
                before += centred * centred;
                after += (centred - frameMean) * (centred - frameMean);
            }
        }
        return before > 0 ? after / before : double.NaN;
    }

    /// <summary>
    /// A <paramref name="size"/> by <paramref name="size"/> patch of <paramref name="plane"/> whose sample (size / 2, size / 2) lies at
    /// (<paramref name="cx"/>, <paramref name="cy"/>), read bilinearly between the plane's samples; zero beyond the plane.
    /// </summary>
    public static void Cut(ReadOnlySpan<float> plane, int width, int height, double cx, double cy, int size, Span<float> into)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(plane.Length, width * height);
        ArgumentOutOfRangeException.ThrowIfNotEqual(into.Length, size * size);
        var (x0, y0) = (cx - (size / 2), cy - (size / 2));
        for (var j = 0; j < size; j++)
        {
            var y = y0 + j;
            var iy = (int)Math.Floor(y);
            var ty = y - iy;
            for (var i = 0; i < size; i++)
            {
                var x = x0 + i;
                var ix = (int)Math.Floor(x);
                var tx = x - ix;
                var top = (Sample(plane, width, height, ix, iy) * (1 - tx)) + (Sample(plane, width, height, ix + 1, iy) * tx);
                var bottom = (Sample(plane, width, height, ix, iy + 1) * (1 - tx)) + (Sample(plane, width, height, ix + 1, iy + 1) * tx);
                into[(j * size) + i] = (float)((top * (1 - ty)) + (bottom * ty));
            }
        }
    }

    /// <summary>
    /// As <see cref="Cut"/>, read through Lanczos-3 instead (#1350): bilinear's blur depends on where between the plane's samples the patch
    /// lands, up to a fifth of band 2 at half a pixel either way, so a score read through it ranks frames partly by that offset. Every sample
    /// of a patch shares one offset, so the weights are made once and applied an axis at a time.
    /// </summary>
    public static void CutLanczos3(ReadOnlySpan<float> plane, int width, int height, double cx, double cy, int size, Span<float> into)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(plane.Length, width * height);
        ArgumentOutOfRangeException.ThrowIfNotEqual(into.Length, size * size);
        var (x0, y0) = (cx - (size / 2), cy - (size / 2));
        var (ix, iy) = ((int)Math.Floor(x0), (int)Math.Floor(y0));
        Span<float> wx = stackalloc float[6];
        Span<float> wy = stackalloc float[6];
        Image.Lanczos3Weights((float)(x0 - ix), wx);
        Image.Lanczos3Weights((float)(y0 - iy), wy);
        Normalise(wx);
        Normalise(wy);

        // Rows iy - 2 to iy + size + 2, each filtered across, then down.
        var span = size + 5;
        var rows = new float[span * size];
        for (var r = 0; r < span; r++)
        {
            var sy = iy - 2 + r;
            for (var i = 0; i < size; i++)
            {
                var v = 0f;
                for (var k = 0; k < 6; k++)
                {
                    v += wx[k] * Sample(plane, width, height, ix + i - 2 + k, sy);
                }
                rows[(r * size) + i] = v;
            }
        }
        for (var j = 0; j < size; j++)
        {
            for (var i = 0; i < size; i++)
            {
                var v = 0f;
                for (var k = 0; k < 6; k++)
                {
                    v += wy[k] * rows[((j + k) * size) + i];
                }
                into[(j * size) + i] = v;
            }
        }
    }

    private static void Normalise(Span<float> w)
    {
        var sum = 0f;
        foreach (var v in w)
        {
            sum += v;
        }
        for (var i = 0; i < w.Length; i++)
        {
            w[i] /= sum;
        }
    }

    private static float Sample(ReadOnlySpan<float> plane, int width, int height, int x, int y)
        => x >= 0 && y >= 0 && x < width && y < height ? plane[(y * width) + x] : 0f;

    /// <summary>
    /// <paramref name="patch"/>'s least-squares gain in a trous band <paramref name="band"/> (1 the finest) against
    /// <paramref name="reference"/>'s, both <paramref name="size"/> square, over their central <paramref name="inner"/> square, where the
    /// transform's reach stays inside the patch: the frame's transfer there over the reference's, which noise does not bias (R4).
    /// </summary>
    public static double BandGain(ReadOnlySpan<float> patch, ReadOnlySpan<float> reference, int size, int band, int inner)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(band, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(inner, size);
        var p = ATrousWaveletTransform.Decompose(patch, size, size, band).Detail(band - 1);
        var r = ATrousWaveletTransform.Decompose(reference, size, size, band).Detail(band - 1);
        var from = (size - inner) / 2;
        double pr = 0, rr = 0;
        for (var y = from; y < from + inner; y++)
        {
            for (var x = from; x < from + inner; x++)
            {
                var i = (y * size) + x;
                pr += (double)p[i] * r[i];
                rr += (double)r[i] * r[i];
            }
        }
        return rr > 0 ? pr / rr : double.NaN;
    }
}
