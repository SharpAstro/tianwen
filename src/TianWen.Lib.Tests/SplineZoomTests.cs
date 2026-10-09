using System;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Shouldly;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// <see cref="SplineZoom"/> against scipy itself. The deconvolver's model was trained and read out on frames resampled
/// by <c>scipy.ndimage.zoom(plane, factor, order=3)</c>, so a resample that is merely a good cubic is not enough: each
/// case of <c>spline-zoom-fixture.json.gz</c> (written by <c>tools/spline-zoom-fixture.py</c>) is a float32 plane, the
/// shape scipy gave it and scipy's float32 output, and the C# output must land within <see cref="Tolerance"/> of it.
/// </summary>
/// <remarks>
/// The bound is float32 rounding, not a fit: both sides compute in double and round once, and a 1e-6 bound on values up
/// to about 3.6 (the largest in the fixture) is a few float32 ulps. Measured when it was written (scipy 1.18.0): 17 of
/// the 20 cases bit-identical, two off by 4e-17 on values near zero, and one by 2.4e-7, a single float32 ulp at 3, where
/// the separable order's double round-off crossed a float rounding boundary. The cases that matter most are the edges
/// (the mirrored prefilter and taps: scipy's 'reflect', 'nearest' and zero-padded 'grid-constant' miss them by 2e-3 to
/// 0.13) and the two shapes where scipy writes 0 over a whole last row or column because the last coordinate rounded
/// past the input's last sample (any boundary rule without that misses them by up to 1.09).
/// </remarks>
[Collection("Imaging")]
public class SplineZoomTests(ITestOutputHelper output)
{
    private const string FixtureName = "spline-zoom-fixture.json.gz";

    private const double Tolerance = 1e-6;

    private static readonly Lazy<JsonElement> Fixture = new Lazy<JsonElement>(LoadFixture, isThreadSafe: true);

    public static TheoryData<string> CaseNames()
    {
        var names = new TheoryData<string>();
        foreach (var c in Fixture.Value.GetProperty("cases").EnumerateArray())
        {
            names.Add(c.GetProperty("name").GetString() ?? "");
        }

        return names;
    }

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void MatchesScipy(string name)
    {
        var c = FindCase(name);
        var input = ReadPlane(c, "input", "inHeight", "inWidth");
        var expected = ReadPlane(c, "output", "outHeight", "outWidth");
        var outHeight = expected.GetLength(0);
        var outWidth = expected.GetLength(1);

        var actual = SplineZoom.Zoom(input, outHeight, outWidth, TestContext.Current.CancellationToken);

        actual.GetLength(0).ShouldBe(outHeight);
        actual.GetLength(1).ShouldBe(outWidth);
        var worst = 0.0;
        var worstAt = (Y: 0, X: 0);
        for (var y = 0; y < outHeight; y++)
        {
            for (var x = 0; x < outWidth; x++)
            {
                var d = Math.Abs((double)actual[y, x] - expected[y, x]);
                if (d > worst)
                {
                    worst = d;
                    worstAt = (y, x);
                }
            }
        }

        output.WriteLine($"{name}: {input.GetLength(0)} x {input.GetLength(1)} -> {outHeight} x {outWidth}, max |C# - scipy| {worst:0.###e+0} at ({worstAt.Y}, {worstAt.X})");
        worst.ShouldBeLessThanOrEqualTo(Tolerance, $"{name}: worst at ({worstAt.Y}, {worstAt.X}), C# {actual[worstAt.Y, worstAt.X]} against scipy {expected[worstAt.Y, worstAt.X]}");

        // The factor scipy was called with gives the same shape through OutputSize, and a scalar factor gives the
        // same plane through the factor overload.
        var factor = c.GetProperty("factor");
        var fy = factor[0].GetDouble();
        var fx = factor[1].GetDouble();
        SplineZoom.OutputSize(input.GetLength(0), fy).ShouldBe(outHeight);
        SplineZoom.OutputSize(input.GetLength(1), fx).ShouldBe(outWidth);
        if (fy == fx)
        {
            SplineZoom.Zoom(input, fy, TestContext.Current.CancellationToken).Cast<float>().ShouldBe(actual.Cast<float>());
        }
    }

    [Fact]
    public void OutputSizeIsScipys()
    {
        var checkedCount = 0;
        foreach (var s in Fixture.Value.GetProperty("outputSizes").EnumerateArray())
        {
            var n = s.GetProperty("n").GetInt32();
            var factor = s.GetProperty("factor").GetDouble();
            SplineZoom.OutputSize(n, factor).ShouldBe(s.GetProperty("size").GetInt32(), $"{n} x {factor:R}");
            checkedCount++;
        }

        checkedCount.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void ACancelledTokenThrows()
    {
        var plane = new float[40, 50];
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Should.Throw<OperationCanceledException>(() => SplineZoom.Zoom(plane, 51, 64, cts.Token));
    }

    private static JsonElement FindCase(string name)
    {
        foreach (var c in Fixture.Value.GetProperty("cases").EnumerateArray())
        {
            if (c.GetProperty("name").GetString() == name)
            {
                return c;
            }
        }

        throw new InvalidOperationException($"No case {name} in {FixtureName}.");
    }

    /// <summary>A plane stored as base64 of little-endian float32 bytes, row-major.</summary>
    private static float[,] ReadPlane(JsonElement c, string data, string height, string width)
    {
        var h = c.GetProperty(height).GetInt32();
        var w = c.GetProperty(width).GetInt32();
        var bytes = Convert.FromBase64String(c.GetProperty(data).GetString() ?? "");
        bytes.Length.ShouldBe(4 * h * w);

        var plane = new float[h, w];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                plane[y, x] = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(4 * (y * w + x), 4));
            }
        }

        return plane;
    }

    private static JsonElement LoadFixture()
    {
        using var gz = SharedTestData.OpenEmbeddedFileStream(FixtureName)
            ?? throw new InvalidOperationException($"Missing embedded test data {FixtureName}.");
        using var raw = new GZipStream(gz, CompressionMode.Decompress, false);
        using var doc = JsonDocument.Parse(raw);
        return doc.RootElement.Clone();
    }
}
