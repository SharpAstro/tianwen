using System;
using System.IO;
using System.Runtime.InteropServices;
using SharpAstro.Ser;

namespace TianWen.Lib.Tests;

/// <summary>
/// Shared synthetic-SER builders for the planetary tests: a temp path, a 16-bit frame writer, and
/// sharp-vs-blurred frame generators used to exercise the quality estimators / grader without committing
/// a binary fixture.
/// </summary>
internal static class PlanetarySerFixtures
{
    public static string NewTempPath() => Path.Combine(Path.GetTempPath(), $"ser-planetary-{Guid.NewGuid():N}.ser");

    /// <summary>Writes a 16-bit SER; each frame is <c>width*height*planes</c> ushorts (host order).</summary>
    public static void WriteSer(string path, int width, int height, SerColorId colorId, ushort[][] frames,
        DateTimeOffset[]? timestamps = null)
    {
        using var writer = new SerWriter(path, width, height, colorId, pixelDepthPerPlane: 16);
        for (var i = 0; i < frames.Length; i++)
        {
            var bytes = MemoryMarshal.AsBytes<ushort>(frames[i]);
            if (timestamps is not null)
            {
                writer.AppendFrame(bytes, timestamps[i]);
            }
            else
            {
                writer.AppendFrame(bytes);
            }
        }
    }

    /// <summary>A full-contrast checkerboard (maximal spatial frequency) -- the sharpest possible frame.</summary>
    public static ushort[] Checker(int width, int height)
    {
        var f = new ushort[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                f[(y * width) + x] = ((x + y) & 1) == 0 ? (ushort)0 : ushort.MaxValue;
            }
        }

        return f;
    }

    /// <summary>Box-blurs <paramref name="src"/> <paramref name="passes"/> times (3x3, edge-clamped) -- a softer frame.</summary>
    public static ushort[] Blur(ushort[] src, int width, int height, int passes)
    {
        var cur = (ushort[])src.Clone();
        var next = new ushort[src.Length];
        for (var p = 0; p < passes; p++)
        {
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var sum = 0;
                    var n = 0;
                    for (var dy = -1; dy <= 1; dy++)
                    {
                        var yy = y + dy;
                        if (yy < 0 || yy >= height) continue;
                        for (var dx = -1; dx <= 1; dx++)
                        {
                            var xx = x + dx;
                            if (xx < 0 || xx >= width) continue;
                            sum += cur[(yy * width) + xx];
                            n++;
                        }
                    }

                    next[(y * width) + x] = (ushort)(sum / n);
                }
            }

            (cur, next) = (next, cur);
        }

        return cur;
    }
}
