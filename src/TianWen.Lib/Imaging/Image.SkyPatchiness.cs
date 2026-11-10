using System;
using System.Runtime.InteropServices;

namespace TianWen.Lib.Imaging;

public partial class Image
{
    /// <summary>The side of the square tiles <see cref="SkyPatchiness"/> reads the sky in, in pixels.</summary>
    public const int SkyPatchinessTile = 64;

    /// <summary>
    /// How patchy the sky is once its smooth part is taken out: the median of each <see cref="SkyPatchinessTile"/>-pixel tile,
    /// a quadratic surface fitted to those medians across the frame by least squares, and the median absolute deviation of
    /// the tiles about that surface, in the channel's own value space. <see cref="float.NaN"/> for a frame smaller than three
    /// tiles a side, which has too few tiles to fit six terms to.
    /// </summary>
    /// <remarks>
    /// <para><b>Cloud is the reason this exists</b> (#1951). A cloud moving through a frame is invisible to the grade's star
    /// count, HFD and ellipticity, since a uniform dimming changes none of them and the detector sets its threshold from the
    /// frame's own noise; on a 2026-05-14 Virgo run four cloudy frames out-scored the clear ones. Nor is it extra noise: a
    /// photosite against its same-colour neighbour, and each 64 px tile's own spread, read the same under cloud as in a clear
    /// sky (0.97 to 1.05 times the group's best tenth for both). What cloud lit from below adds is unevenness LARGER than a
    /// tile, and that is also what a twilight or light-pollution gradient adds, which is why a whole-frame MAD rose under both
    /// and could not tell them apart. The two differ in SHAPE: a gradient is smooth and lies on a quadratic, cloud is patchy
    /// and does not. Measured over two nights, the clear groups' 90th percentile sat at 1.07 to 1.10 times their best tenth,
    /// the comment's cloudy frames at 8.1 to 12.7, and the owner's own <c>BAD_</c> marks of one cloudy stretch were 35 of 36
    /// above twice it.</para>
    /// <para>A tile's MEDIAN is the sample, so the few stars a tile holds do not move it, and an object that fills the field
    /// moves it the same way in every frame of the field, which is why the figure is read against its group's best rather
    /// than on its own. A Bayer mosaic is read at one green phase, as the masked statistics are: a grid over a mosaic would
    /// otherwise mix the photosite colours, which sit at different levels.</para>
    /// </remarks>
    public float SkyPatchiness(int channel) => SkySurface(channel).Patchiness;

    /// <summary>
    /// The sky of one channel in one pass over the <see cref="SkyPatchinessTile"/>-pixel tile medians (#1974): the fitted
    /// quadratic surface's value at the frame centre, the size and direction of its linear part, and the patchiness about it
    /// (the same figure as <see cref="SkyPatchiness"/>). Everything is NaN for a frame with too few tiles.
    /// </summary>
    public SkySurface SkySurface(int channel)
    {
        var (_, width, height) = Shape;
        var tilesX = width / SkyPatchinessTile;
        var tilesY = height / SkyPatchinessTile;
        if (tilesX < 3 || tilesY < 3)
        {
            return TianWen.Lib.Imaging.SkySurface.Unknown;
        }

        // One green phase of a mosaic (its first start, which is red's row), every other pixel of a plain plane: either way a
        // tile is read at a step of two, a quarter of its pixels, which is plenty for a median.
        var (row0, col0) = IsCfaMosaic ? GreenPhase() : (0, 0);
        var data = Planes[channel].Data;
        var tileCount = tilesX * tilesY;
        var perTile = (SkyPatchinessTile / 2) * (SkyPatchinessTile / 2);
        using var samples = ArrayPoolHelper.Rent<float>(perTile);
        using var medians = ArrayPoolHelper.Rent<double>(tileCount);
        using var us = ArrayPoolHelper.Rent<double>(tileCount);
        using var vs = ArrayPoolHelper.Rent<double>(tileCount);

        var t = 0;
        for (var ty = 0; ty < tilesY; ty++)
        {
            for (var tx = 0; tx < tilesX; tx++)
            {
                var n = 0;
                for (var y = ty * SkyPatchinessTile + row0; y < (ty + 1) * SkyPatchinessTile; y += 2)
                {
                    var rowSpan = MemoryMarshal.CreateReadOnlySpan(ref data[y, 0], width);
                    for (var x = tx * SkyPatchinessTile + col0; x < (tx + 1) * SkyPatchinessTile; x += 2)
                    {
                        if (float.IsFinite(rowSpan[x]))
                        {
                            samples[n++] = rowSpan[x];
                        }
                    }
                }
                if (n == 0)
                {
                    continue;
                }
                var tile = samples.AsSpan(0, n);
                tile.Sort();
                medians[t] = tile[n / 2];
                // Tile centres on [-0.5, 0.5] keep the normal equations well conditioned whatever the frame's size.
                us[t] = (tx + 0.5) / tilesX - 0.5;
                vs[t] = (ty + 0.5) / tilesY - 0.5;
                t++;
            }
        }
        if (t < 6)
        {
            return TianWen.Lib.Imaging.SkySurface.Unknown;
        }

        var coefficients = FitQuadraticSurface(us.AsSpan(0, t), vs.AsSpan(0, t), medians.AsSpan(0, t));
        using var residuals = ArrayPoolHelper.Rent<float>(t);
        for (var i = 0; i < t; i++)
        {
            residuals[i] = (float)(medians[i] - QuadraticAt(coefficients, us[i], vs[i]));
        }
        var patchiness = MedianAbsoluteDeviation(residuals.AsSpan(0, t));
        // u and v span one frame width and height, so b and c are the change per frame; the surface rises along (b, c).
        var gradient = Math.Sqrt(coefficients[1] * coefficients[1] + coefficients[2] * coefficients[2]);
        var angle = gradient > 0 ? Math.Atan2(coefficients[2], coefficients[1]) * 180.0 / Math.PI : double.NaN;
        return new SkySurface((float)coefficients[0], (float)gradient, (float)angle, patchiness);

        (int Row, int Col) GreenPhase()
        {
            Span<(int Row, int Col)> starts = stackalloc (int Row, int Col)[2];
            CfaPhaseStarts(CfaChannel.Green, starts);
            return starts[0];
        }
    }

    /// <summary>The six terms of <c>a + b u + c v + d u^2 + e u v + f v^2</c> fitted to <paramref name="z"/> by least squares.</summary>
    internal static double[] FitQuadraticSurface(ReadOnlySpan<double> u, ReadOnlySpan<double> v, ReadOnlySpan<double> z)
    {
        // The normal equations, 6 x 6, solved by Gaussian elimination with partial pivoting: small and fixed, so no library.
        var a = new double[6, 7];
        Span<double> basis = stackalloc double[6];
        for (var i = 0; i < z.Length; i++)
        {
            QuadraticBasis(u[i], v[i], basis);
            for (var p = 0; p < 6; p++)
            {
                for (var q = 0; q < 6; q++)
                {
                    a[p, q] += basis[p] * basis[q];
                }
                a[p, 6] += basis[p] * z[i];
            }
        }
        for (var col = 0; col < 6; col++)
        {
            var pivot = col;
            for (var r = col + 1; r < 6; r++)
            {
                if (Math.Abs(a[r, col]) > Math.Abs(a[pivot, col]))
                {
                    pivot = r;
                }
            }
            for (var c = 0; c < 7; c++)
            {
                (a[col, c], a[pivot, c]) = (a[pivot, c], a[col, c]);
            }
            for (var r = 0; r < 6; r++)
            {
                if (r != col && a[col, col] != 0)
                {
                    var f = a[r, col] / a[col, col];
                    for (var c = 0; c < 7; c++)
                    {
                        a[r, c] -= f * a[col, c];
                    }
                }
            }
        }
        var coefficients = new double[6];
        for (var p = 0; p < 6; p++)
        {
            coefficients[p] = a[p, p] != 0 ? a[p, 6] / a[p, p] : 0;
        }
        return coefficients;
    }

    internal static double QuadraticAt(ReadOnlySpan<double> coefficients, double u, double v)
    {
        Span<double> basis = stackalloc double[6];
        QuadraticBasis(u, v, basis);
        var sum = 0.0;
        for (var p = 0; p < 6; p++)
        {
            sum += coefficients[p] * basis[p];
        }
        return sum;
    }

    private static void QuadraticBasis(double u, double v, Span<double> basis)
    {
        basis[0] = 1;
        basis[1] = u;
        basis[2] = v;
        basis[3] = u * u;
        basis[4] = u * v;
        basis[5] = v * v;
    }

    /// <summary>The median absolute deviation from the median; reorders <paramref name="values"/>.</summary>
    private static float MedianAbsoluteDeviation(Span<float> values)
    {
        values.Sort();
        var median = values[values.Length / 2];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = MathF.Abs(values[i] - median);
        }
        values.Sort();
        return values[values.Length / 2];
    }
}
