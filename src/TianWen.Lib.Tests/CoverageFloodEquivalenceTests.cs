using System;
using System.Collections.Generic;
using TianWen.Lib.Geometry;
using Shouldly;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The word-parallel flood in <see cref="Image.LargestCoveredRectangle()"/>, checked against a
/// per-pixel reference written the obvious way.
/// </summary>
/// <remarks>
/// <para><b>The flood propagates 64 columns at a time</b>, through a Kogge-Stone occluded fill with a
/// carry crossing each word boundary. That is a large amount of bit arithmetic standing in for a loop
/// anyone can read, and the whole auto-crop rests on it, so it is pinned against a reference rather
/// than against its own output: the reference floods one pixel at a time from the border and finds the
/// rectangle with a plain histogram scan.</para>
/// <para><b>The widths are the point.</b> A word holds 64 columns, so 63, 64 and 65 put the frame edge
/// just inside, exactly on, and just past a word boundary, and 127/128/129 do it again one word along.
/// Every carry, mask and padding bug this code can have shows up at one of those and nowhere else -- a
/// frame 200 wide would pass with the carry deleted.</para>
/// </remarks>
public class CoverageFloodEquivalenceTests
{
    private static Image FrameOf(char[][] rows)
    {
        var h = rows.Length;
        var w = rows[0].Length;
        var plane = new float[h, w];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                plane[y, x] = rows[y][x] switch
                {
                    '.' => 0f,
                    'n' => float.NaN,
                    _ => 0.25f + (0.001f * x),
                };
            }
        }

        return new Image([plane], BitDepth.Float32, maxValue: 1f, minValue: 0f, pedestal: 0f,
            imageMeta: new ImageMeta { Instrument = "synth", SensorType = SensorType.Monochrome });
    }

    /// <summary>Absence one pixel at a time: unusable, and reachable from the border through unusable.</summary>
    private static (PixelRect Best, bool[,] Absent) ReferenceRectangle(char[][] rows)
    {
        var h = rows.Length;
        var w = rows[0].Length;

        var unusable = new bool[h, w];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                unusable[y, x] = rows[y][x] is '.' or 'n';
            }
        }

        var absent = new bool[h, w];
        var queue = new Queue<(int Y, int X)>();
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                if ((y == 0 || x == 0 || y == h - 1 || x == w - 1) && unusable[y, x] && !absent[y, x])
                {
                    absent[y, x] = true;
                    queue.Enqueue((y, x));
                }
            }
        }

        while (queue.Count > 0)
        {
            var (cy, cx) = queue.Dequeue();
            foreach (var (dy, dx) in new[] { (-1, 0), (1, 0), (0, -1), (0, 1) })
            {
                var ny = cy + dy;
                var nx = cx + dx;
                if (ny >= 0 && ny < h && nx >= 0 && nx < w && unusable[ny, nx] && !absent[ny, nx])
                {
                    absent[ny, nx] = true;
                    queue.Enqueue((ny, nx));
                }
            }
        }

        // Largest rectangle of covered pixels, by the textbook histogram scan.
        var heights = new int[w];
        var best = PixelRect.Empty;
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                heights[x] = absent[y, x] ? 0 : heights[x] + 1;
            }

            for (var left = 0; left < w; left++)
            {
                var minHeight = int.MaxValue;
                for (var right = left; right < w; right++)
                {
                    minHeight = Math.Min(minHeight, heights[right]);
                    if (minHeight == 0)
                    {
                        break;
                    }

                    var area = minHeight * (right - left + 1);
                    if (area > best.Width * best.Height)
                    {
                        best = new PixelRect(left, y - minHeight + 1, right - left + 1, minHeight);
                    }
                }
            }
        }

        return (best, absent);
    }

    /// <summary>
    /// The two things that are actually true of the answer, rather than its exact coordinates.
    /// </summary>
    /// <remarks>
    /// <b>Equal-area rectangles are common and the tie-break is not part of the contract.</b> On one
    /// 127-wide frame the reference answers 78 x 1 and the production scan 39 x 2 -- both area 78, both
    /// correct, neither more right than the other. Asserting coordinates pins an implementation detail
    /// and reports it as a flood bug; asserting the AREA and that the rectangle contains no absent pixel
    /// pins the two properties the crop actually depends on, and still fails the moment the flood marks
    /// the wrong pixels, because that changes what is reachable.
    /// </remarks>
    private static void ShouldMatch(PixelRect actual, PixelRect expected, bool[,] absent, string because)
    {
        (actual.Width * actual.Height).ShouldBe(expected.Width * expected.Height, because);

        for (var y = actual.Top; y < actual.Bottom; y++)
        {
            for (var x = actual.Left; x < actual.Right; x++)
            {
                absent[y, x].ShouldBeFalse($"{because}: ({x}, {y}) is absent but inside the answer");
            }
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(63)]
    [InlineData(64)]
    [InlineData(65)]
    [InlineData(127)]
    [InlineData(128)]
    [InlineData(129)]
    [InlineData(200)]
    public void TheWordParallelFloodAgreesWithAPerPixelReference(int width)
    {
        var rnd = new Random(width * 7919);

        for (var trial = 0; trial < 25; trial++)
        {
            var height = 3 + rnd.Next(30);
            var rows = new char[height][];
            for (var y = 0; y < height; y++)
            {
                rows[y] = new char[width];
                for (var x = 0; x < width; x++)
                {
                    rows[y][x] = rnd.Next(100) switch
                    {
                        < 12 => rnd.Next(2) == 0 ? '.' : 'n',   // scattered unusable, both kinds
                        _ => '#',
                    };
                }
            }

            // A long RUN of absence reaching in from the left edge, which is the case the cross-word
            // carry exists for and the only one that can catch it: scattered pixels never propagate far
            // enough to leave the word they started in, so a frame of those passes with the carry
            // deleted. Lengths straddle 64 and 128 deliberately.
            if (trial % 3 == 0)
            {
                var barRow = rnd.Next(height);
                var barLength = Math.Min(width, 55 + rnd.Next(80));
                for (var x = 0; x < barLength; x++)
                {
                    rows[barRow][x] = rnd.Next(2) == 0 ? '.' : 'n';
                }
            }

            // Half the trials get a ragged ring as well, which is the shape a real stack leaves and the
            // one that makes the flood do actual work rather than settling in a single pass.
            if (trial % 2 == 0)
            {
                for (var y = 0; y < height; y++)
                {
                    var left = Math.Min(width, 1 + rnd.Next(3));
                    var right = Math.Max(0, width - 1 - rnd.Next(3));
                    for (var x = 0; x < left; x++) { rows[y][x] = rnd.Next(2) == 0 ? '.' : 'n'; }
                    for (var x = right; x < width; x++) { rows[y][x] = rnd.Next(2) == 0 ? '.' : 'n'; }
                }
            }

            var (expected, absent) = ReferenceRectangle(rows);
            var actual = FrameOf(rows).LargestCoveredRectangle();

            ShouldMatch(actual, expected, absent, $"width {width}, trial {trial}, height {height}");
        }
    }

    /// <summary>
    /// A spiral is the shape the sweep cap exists for: it cannot settle in one pass either way, so the
    /// word-parallel version has to agree with the reference about how far it got.
    /// </summary>
    [Fact]
    public void ASpiralOfAbsenceAgreesToo()
    {
        const int w = 129, h = 61;
        var rows = new char[h][];
        for (var y = 0; y < h; y++)
        {
            rows[y] = new char[w];
            Array.Fill(rows[y], '#');
        }

        // An inward spiral of unusable pixels, anchored at the border so it is genuinely absence.
        int top = 0, bottom = h - 1, left = 0, right = w - 1;
        while (top + 2 < bottom && left + 2 < right)
        {
            for (var x = left; x <= right; x++) { rows[top][x] = 'n'; }
            for (var y = top; y <= bottom; y++) { rows[y][right] = 'n'; }
            for (var x = right; x >= left + 2; x--) { rows[bottom][x] = 'n'; }
            for (var y = bottom; y >= top + 2; y--) { rows[y][left + 2] = 'n'; }
            top += 2;
            bottom -= 2;
            left += 2;
            right -= 2;
        }

        var (expected, absent) = ReferenceRectangle(rows);
        ShouldMatch(FrameOf(rows).LargestCoveredRectangle(), expected, absent, "spiral");
    }
}
