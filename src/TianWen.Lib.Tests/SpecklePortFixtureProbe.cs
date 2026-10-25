using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Degradation;
using TianWen.Lib.Imaging.StarRemoval;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Writes the fixture <c>training/denoise/test_n2n_speckle.py</c> pins the torch speckle penalty
/// (<c>n2n_speckle.py</c>) against: a small synthetic plane with dug, clean and edge sites, an absent mask, the noise cap
/// <see cref="StarlessSpeckles.NoiseCap"/> gives it, and what <see cref="StarlessSpeckles"/> reads at each site (flag,
/// ring median, sigma), as raw little-endian planes plus a JSON sidecar.
/// </summary>
/// <remarks>
/// <para>The speckle teacher (docs/plans/star-remover-training.md, "The speckle teacher") is a port of the C# measure, and a
/// port that is quietly off (a median at the wrong rank, a ring one pixel wide, a cap missed) still penalises
/// something, so the trainer and the gate would drift apart without anything failing. The only way to know they agree is
/// to run both on one plane and compare per site.</para>
/// <para>Opt-in through <c>TIANWEN_SPECKLE_FIXTURE_DIR</c> (skips otherwise), because it writes files and a unit run
/// should not. Deterministic (fixed seed): the committed fixture regenerates byte-identical until the measure changes.
/// Regenerate it into <c>training/denoise/fixtures/speckle</c>.</para>
/// </remarks>
public sealed class SpecklePortFixtureProbe
{
    private const string DirVar = "TIANWEN_SPECKLE_FIXTURE_DIR";
    private const int Size = 160;
    private const float Sky = 0.1f;
    private const float Sigma = 0.002f;
    private const int Border = 4;

    [Fact]
    public void WriteTheFixtureTheTorchPortIsPinnedAgainst()
    {
        var dir = Environment.GetEnvironmentVariable(DirVar);
        Assert.SkipWhen(string.IsNullOrWhiteSpace(dir), $"{DirVar} not set");
        Directory.CreateDirectory(dir!);

        var rng = new Random(20261025);

        // Correlated noise, as a master's is (warp and demosaic): the noise cap fits a pixel sigma and a correlation width
        // from lag-1 and lag-2 differences, which white noise cannot fix.
        var white = new float[Size * Size];
        for (var i = 0; i < white.Length; i++)
        {
            white[i] = (float)StarInjection.Gaussian(rng);
        }
        var plane = Image.SeparableGaussianBlur(white, Size, Size, 1f);
        var mean = 0.0;
        foreach (var v in plane)
        {
            mean += v;
        }
        mean /= plane.Length;
        var variance = 0.0;
        foreach (var v in plane)
        {
            variance += (v - mean) * (v - mean);
        }
        var scale = Sigma / (float)Math.Sqrt(variance / plane.Length);
        for (var i = 0; i < plane.Length; i++)
        {
            plane[i] = Sky + ((plane[i] - (float)mean) * scale);
        }
        // The right half is a nebula's texture: a 4 sigma ripple 24 px in period, where the ring's MAD reads the ripple and
        // the cap reads the pixels' own noise.
        for (var y = 0; y < Size; y++)
        {
            for (var x = Size / 2; x < Size; x++)
            {
                plane[(y * Size) + x] += 4 * Sigma * (float)(Math.Sin(2 * Math.PI * x / 24) * Math.Sin(2 * Math.PI * y / 24));
            }
        }

        // The canvas ring (absent, exact zero) and a 5 x 5 absent blob that is dark too: neither may be read.
        var absent = new BitMatrix(Size, Size);
        var absentBytes = new byte[Size * Size];
        void Absent(int x, int y)
        {
            absent[y, x] = true;
            absentBytes[(y * Size) + x] = 1;
            plane[(y * Size) + x] = 0f;
        }
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                if (x < Border || y < Border || x >= Size - Border || y >= Size - Border)
                {
                    Absent(x, y);
                }
            }
        }
        for (var y = 98; y <= 102; y++)
        {
            for (var x = 98; x <= 102; x++)
            {
                Absent(x, y);
            }
        }

        // Sites: a dipole dug into a quarter of them at three depths (one dark pixel beside a bright one, the mean kept),
        // clean ones between, a sub-pixel centre on most, and the edge and absent cases by hand.
        var sites = new List<(float X, float Y)>();
        var fractions = new[] { 0f, 0.4f, -0.3f, 0.75f };
        var depths = new[] { 8f, 0f, 3f, 6f, 0f, 8f };
        for (var k = 0; k < 60; k++)
        {
            var x = 14 + rng.Next(Size - 28) + fractions[k % fractions.Length];
            var y = 14 + rng.Next(Size - 28) + fractions[(k / 2) % fractions.Length];
            sites.Add(((float)x, (float)y));
            var depth = depths[k % depths.Length] * Sigma;
            if (depth > 0)
            {
                var px = (int)Math.Round(x);
                var py = (int)Math.Round(y);
                if (!absent[py, px] && !absent[py, px + 1])
                {
                    plane[(py * Size) + px] -= depth;
                    plane[(py * Size) + px + 1] += depth;
                }
            }
        }
        sites.Add((100f, 100f));    // the core is the absent blob: dark, never read
        sites.Add((106.3f, 100f));  // the ring crosses the blob
        sites.Add((13.2f, 80f));    // the ring crosses the canvas ring
        sites.Add((5f, 80f));       // the window leaves the frame
        sites.Add((147.4f, 80f));   // the last column the window fits
        sites.Add((148.5f, 80f));   // one past it
        plane[(80 * Size) + 13] -= 8 * Sigma;
        plane[(80 * Size) + 14] += 8 * Sigma;

        var cap = StarlessSpeckles.NoiseCap(plane, Size, Size, absent);
        var reads = StarlessSpeckles.ReadSites(plane, Size, Size, absent, sites, cap);

        var flags = new sbyte[reads.Length];
        var medians = new float[reads.Length];
        var sigmas = new float[reads.Length];
        var xy = new float[reads.Length * 2];
        var speckled = 0;
        var unread = 0;
        for (var i = 0; i < reads.Length; i++)
        {
            flags[i] = reads[i].Speckled is { } s ? (sbyte)(s ? 1 : 0) : (sbyte)-1;
            medians[i] = reads[i].Median;
            sigmas[i] = reads[i].Sigma;
            xy[2 * i] = sites[i].X;
            xy[(2 * i) + 1] = sites[i].Y;
            speckled += flags[i] == 1 ? 1 : 0;
            unread += flags[i] == -1 ? 1 : 0;
        }

        File.WriteAllBytes(Path.Combine(dir!, "plane.f32"), MemoryMarshal.AsBytes<float>(plane).ToArray());
        File.WriteAllBytes(Path.Combine(dir!, "absent.u8"), absentBytes);
        File.WriteAllBytes(Path.Combine(dir!, "cap.f32"), MemoryMarshal.AsBytes<float>(cap).ToArray());
        File.WriteAllBytes(Path.Combine(dir!, "sites.f32"), MemoryMarshal.AsBytes<float>(xy).ToArray());
        File.WriteAllBytes(Path.Combine(dir!, "flags.i8"), MemoryMarshal.AsBytes<sbyte>(flags).ToArray());
        File.WriteAllBytes(Path.Combine(dir!, "median.f32"), MemoryMarshal.AsBytes<float>(medians).ToArray());
        File.WriteAllBytes(Path.Combine(dir!, "sigma.f32"), MemoryMarshal.AsBytes<float>(sigmas).ToArray());
        File.WriteAllText(Path.Combine(dir!, "fixture.json"), JsonSerializer.Serialize(new
        {
            size = Size,
            sites = reads.Length,
            speckled,
            unread,
            speckleSigma = StarlessSpeckles.SpeckleSigma,
            coreRadius = StarlessSpeckles.CoreRadius,
            skyInner = StarlessSpeckles.SkyInner,
            skyOuter = StarlessSpeckles.SkyOuter,
            capBlock = (int)Math.Round(2 * StarlessSpeckles.SkyOuter),
            layout = "plane, cap: size x size float32 little-endian row-major; absent: size x size uint8 (1 = absent); "
                + "sites: sites x 2 float32 (x, y); flags: sites int8 (1 speckled, 0 not, -1 not read); median, sigma: sites float32 "
                + "(NaN where not read); cap is +inf where the difference-noise model does not fit",
        }));

        // The fixture is only worth pinning against if it exercises every outcome.
        Assert.True(speckled > 10, $"{speckled} speckled sites");
        Assert.True(unread >= 2, $"{unread} unread sites");
        Assert.True(speckled < reads.Length - unread - 10, "clean sites remain");
    }
}
