using System;
using System.Collections.Generic;
using Shouldly;
using TianWen.Lib.Imaging.Planetary;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// <see cref="MetricDisk.RingTouched"/> flags every pixel a ring the observer sees comes near, however thin the rings are drawn (#1410).
/// It read nine points 2 px apart, so once the rings were thinner than that along the axis, below a tilt B of about 2 to 4 degrees, which is
/// every capture of 2024 to 2026, a pixel beside the band went unflagged, and where the band passed between pixel centres no pixel was
/// flagged at all: the de-rotation then moved ring pixels as albedo and the colour balance read them as globe.
/// </summary>
public sealed class RingMaskTests
{
    // Saturn's C ring's inner edge and A ring's outer edge, in equatorial radii (74,658 and 136,775 km of 60,268)
    private const double Inner = 1.2388, Outer = 2.2694;
    private const int Supersample = 10;

    [Theory]
    [InlineData(29.6, 0.5)]
    [InlineData(29.6, 1.0)]
    [InlineData(29.6, 2.0)]
    [InlineData(49.0, 1.0)]
    [InlineData(49.0, 3.8)]
    [InlineData(49.0, 15.0)]
    public void EveryPixelNearAVisibleRingIsFlaggedAndNoneFarFromOne(double radius, double tiltDeg)
    {
        var rings = new DiskRings(Inner, Outer, Math.Sin(tiltDeg * Math.PI / 180), NearSign: -1);
        var size = (int)Math.Ceiling((2 * Outer * radius) + 24);
        var disk = new MetricDisk((size / 2.0) + 0.3, (size / 2.0) - 0.4, radius, AxisRatio: 0.9, AxisAngleDeg: 96.3) { Rings = rings };
        var counts = VisibleRingCounts(disk, rings, size);

        var (holes, strays) = (new List<string>(), new List<string>());
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var touched = disk.RingTouched(x, y);
                if (!touched && CountWithin(counts, size, x, y, 0.7) > 0)
                {
                    holes.Add($"({x}, {y})");
                }
                else if (touched && CountWithin(counts, size, x, y, 3.5) == 0)
                {
                    strays.Add($"({x}, {y})");
                }
            }
        }

        holes.ShouldBeEmpty($"{holes.Count} pixels within 0.7 px of a visible ring went unflagged, first {string.Join(", ", holes.GetRange(0, Math.Min(5, holes.Count)))}");
        strays.ShouldBeEmpty($"{strays.Count} pixels over 3.5 px from any visible ring were flagged, first {string.Join(", ", strays.GetRange(0, Math.Min(5, strays.Count)))}");
    }

    // A ring point the observer sees: in the rings' plane between their edges, and off the globe or on the near half crossing it
    private static bool VisibleRing(in MetricDisk disk, in DiskRings rings, double x, double y)
    {
        var rho = disk.RingPlaneRadiiAt(x, y);
        if (rho < rings.InnerRadii || rho > rings.OuterRadii)
        {
            return false;
        }
        var angle = disk.AxisAngleDeg * Math.PI / 180;
        var along = (((x - disk.X) * Math.Cos(angle)) + ((y - disk.Y) * Math.Sin(angle))) / disk.Radius;
        return disk.RadiiAt(x, y) > 1 || along * rings.NearSign > 0;
    }

    // A summed-area table of the visible ring over the frame sampled ten times finer than its pixels, pixel (x, y) the centre of its own
    private static int[] VisibleRingCounts(in MetricDisk disk, in DiskRings rings, int size)
    {
        var n = size * Supersample;
        var table = new int[(n + 1) * (n + 1)];
        for (var j = 0; j < n; j++)
        {
            var y = ((j + 0.5) / Supersample) - 0.5;
            var row = 0;
            for (var i = 0; i < n; i++)
            {
                row += VisibleRing(disk, rings, ((i + 0.5) / Supersample) - 0.5, y) ? 1 : 0;
                table[((j + 1) * (n + 1)) + i + 1] = table[(j * (n + 1)) + i + 1] + row;
            }
        }
        return table;
    }

    // How many fine samples of visible ring lie within `half` px of pixel (x, y) along both image axes
    private static int CountWithin(int[] table, int size, int x, int y, double half)
    {
        var n = size * Supersample;
        int Cell(double at) => Math.Clamp((int)Math.Round((at + 0.5) * Supersample), 0, n);
        var (x0, x1, y0, y1) = (Cell(x - half), Cell(x + half), Cell(y - half), Cell(y + half));
        return table[(y1 * (n + 1)) + x1] - table[(y0 * (n + 1)) + x1] - table[(y1 * (n + 1)) + x0] + table[(y0 * (n + 1)) + x0];
    }
}
