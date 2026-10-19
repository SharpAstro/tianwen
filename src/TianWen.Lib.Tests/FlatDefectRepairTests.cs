using System;
using System.Collections.Generic;
using System.Linq;
using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Calibration;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Pins the master flat's repair of its own defects (<see cref="FlatDefects"/>), as the SVBONY SV605CC flats of
/// 2026-10-17 need it: condensation formed on the sensor while it cooled for the run, grew from frame to frame, and was
/// gone by the lights. Each master is compared with the same run built WITHOUT the droplets, so what the repair must
/// restore and what it must leave alone are both read off one reference.
/// </summary>
public class FlatDefectRepairTests
{
    private const int Size = 128;

    /// <summary>How close a repaired photosite comes to the clean run's: it gives up its own response (0.9% at most
    /// here) for its neighbours' median, and the clean master carries its own noise, so 3%, against droplets 33 to 80%
    /// off.</summary>
    private const float RepairTolerance = 0.03f;

    /// <summary>Top-left corners of the droplets' 4x4 boxes, on both Bayer parities.</summary>
    private static readonly (int X, int Y)[] Droplets = [(20, 20), (61, 30), (30, 81), (91, 91)];

    /// <summary>The light path's own small defects, in every frame alike: a 2x2 speck at 0.75 (an absorber on the
    /// sensor), a dead photosite at 0.2, and a photosite of high response at 1.25.</summary>
    private static readonly (int X, int Y)[] Speck = [(100, 40), (101, 40), (100, 41), (101, 41)];
    private static readonly (int X, int Y) Dead = (45, 110);
    private static readonly (int X, int Y) Responsive = (70, 60);

    /// <summary>A 2x2 that DARKENS to 0.75 during a growing run: the speck's own shape and depth, so only time can tell
    /// that it is not the light path's.</summary>
    private static readonly (int X, int Y)[] Fading = [(40, 100), (41, 100), (40, 101), (41, 101)];

    [Fact]
    public void CondensationThatGrewDuringTheRunIsRepaired_TheLightPathsOwnDefectsAreKept()
    {
        var master = MasterFrameBuilder.BuildFlatMaster(Run(frames: 12, growing: true, droplets: true));
        var clean = MasterFrameBuilder.BuildFlatMaster(Run(frames: 12, growing: true, droplets: false));

        foreach (var (x0, y0) in Droplets)
        {
            foreach (var (x, y, _) in DropletShape(x0, y0))
            {
                (master[0, y, x] / clean[0, y, x]).ShouldBe(1f, RepairTolerance, $"the droplet photosite ({x}, {y}) takes its neighbours' level");
            }
        }
        foreach (var (x, y) in Fading)
        {
            (master[0, y, x] / clean[0, y, x]).ShouldBe(1f, RepairTolerance, $"the spot that darkened during the run at ({x}, {y})");
        }
        AssertTheLightPathIsKept(master, clean);
    }

    /// <summary>Condensation that formed before the run and held still cannot be told by time, and three frames are too
    /// few to read one anyway: it is told by moving light, a core brighter than its neighbours inside a darker ring.</summary>
    [Fact]
    public void CondensationThatHeldStillIsRepairedByItsShape()
    {
        var master = MasterFrameBuilder.BuildFlatMaster(Run(frames: 3, growing: false, droplets: true));
        var clean = MasterFrameBuilder.BuildFlatMaster(Run(frames: 3, growing: false, droplets: false));

        foreach (var (x0, y0) in Droplets)
        {
            foreach (var (x, y, _) in DropletShape(x0, y0))
            {
                (master[0, y, x] / clean[0, y, x]).ShouldBe(1f, RepairTolerance, $"the droplet photosite ({x}, {y})");
            }
        }
        AssertTheLightPathIsKept(master, clean);
    }

    /// <summary>A flat cached by an earlier recipe must not be served as this one's: the recipe is in the fingerprint
    /// both master caches check, for flats, and only for flats.</summary>
    [Fact]
    public void AFlatsFingerprintNamesItsRecipe()
    {
        FrameInfo Frame(FrameType type) => new("C:/flats/f0001.fits", Size, Size, 1, BitDepth.Int16, Meta(type));

        MasterFrameBuilder.InputSetFingerprint([Frame(FrameType.Flat)])
            .ShouldNotBe(MasterFrameBuilder.InputSetFingerprint([Frame(FrameType.Dark)]));
        MasterFrameBuilder.InputSetFingerprint([Frame(FrameType.Dark)])
            .ShouldBe(MasterFrameBuilder.InputSetFingerprint([Frame(FrameType.Bias)]), "a dark's and a bias's recipe did not change");
    }

    private static void AssertTheLightPathIsKept(Image master, Image clean)
    {
        // Read against the defect's OWN neighbours, never the clean run: that master is built by the same code, so a
        // defect the repair wrongly took would be gone from both and their ratio would still read 1.
        var data = master.GetChannelArray(0);
        Span<float> scratch = stackalloc float[BadPixelDetection.FlatNeighbourhoodSize];
        foreach (var (x, y, depth) in Speck.Select(static p => (p.X, p.Y, 0.75f)).Append((Dead.X, Dead.Y, 0.2f)).Append((Responsive.X, Responsive.Y, 1.25f)))
        {
            (BadPixelDetection.FlatRatio(data, y, x, 2, scratch) / depth).ShouldBe(1f, 0.03f,
                $"the light path's own defect at ({x}, {y}) keeps the flat's value, {depth} of its neighbours");
        }
        var elsewhere = 0f;
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                if (!Droplets.Any(d => x >= d.X - 1 && x < d.X + 5 && y >= d.Y - 1 && y < d.Y + 5) && !Fading.Contains((x, y)))
                {
                    elsewhere = MathF.Max(elsewhere, MathF.Abs((master[0, y, x] / clean[0, y, x]) - 1f));
                }
            }
        }
        elsewhere.ShouldBeLessThan(0.005f, "vignetting, the dust shadow and every photosite's response are untouched");
    }

    /// <summary>The droplet measured on the SV605CC: a 2x2 core inside a ring of 8, in a 4x4 box.</summary>
    private static (int X, int Y, float Factor)[] DropletShape(int x0, int y0) =>
    [
        (x0 + 1, y0 + 1, 1.6f), (x0 + 2, y0 + 1, 1.8f), (x0 + 1, y0 + 2, 1.5f), (x0 + 2, y0 + 2, 1.6f),
        (x0 + 1, y0, 0.36f), (x0 + 2, y0, 0.33f), (x0, y0 + 1, 0.36f), (x0 + 3, y0 + 1, 0.35f),
        (x0, y0 + 2, 0.39f), (x0 + 3, y0 + 2, 0.36f), (x0 + 1, y0 + 3, 0.40f), (x0 + 2, y0 + 3, 0.37f),
    ];

    /// <summary>A flat run of RGGB frames: each colour at its own level, vignetting, a soft dust shadow, a fixed 0.5%
    /// response scatter, the light path's own defects, and a 0.5% noise of each frame's own. The droplets, when asked,
    /// grow over the first two thirds of a growing run and hold still otherwise.</summary>
    private static List<Image> Run(int frames, bool growing, bool droplets)
    {
        var run = new List<Image>(frames);
        for (var f = 0; f < frames; f++)
        {
            var strength = growing ? MathF.Min(1f, f / (frames * 2f / 3f)) : 1f;
            var data = new float[Size, Size];
            for (var y = 0; y < Size; y++)
            {
                for (var x = 0; x < Size; x++)
                {
                    var level = ((y & 1, x & 1)) switch { (0, 0) => 0.95f, (1, 1) => 0.85f, _ => 1f };
                    var r2 = ((x - 64f) * (x - 64f) + (y - 64f) * (y - 64f)) / (64f * 64f);
                    var d = MathF.Sqrt((x - 95f) * (x - 95f) + (y - 75f) * (y - 75f));
                    var dust = 1f - 0.08f * Smooth(d - 8f) * Smooth(24f - d);
                    data[y, x] = 30000f * level * (1f - 0.15f * r2) * dust * (1f + Scatter(x, y, 0, 0.009f)) * (1f + Scatter(x, y, f + 1, 0.009f));
                }
            }
            foreach (var (x, y) in Speck)
            {
                data[y, x] *= 0.75f;
            }
            data[Dead.Y, Dead.X] *= 0.2f;
            data[Responsive.Y, Responsive.X] *= 1.25f;
            if (droplets)
            {
                if (growing)
                {
                    foreach (var (x, y) in Fading)
                    {
                        data[y, x] *= 1f - 0.25f * strength;
                    }
                }
                foreach (var (x0, y0) in Droplets)
                {
                    foreach (var (x, y, factor) in DropletShape(x0, y0))
                    {
                        data[y, x] *= 1f + (factor - 1f) * strength;
                    }
                }
            }
            run.Add(new Image([data], BitDepth.Float32, 65535f, 0f, 0f, Meta(FrameType.Flat)));
        }
        return run;

        static float Smooth(float t) => 1f / (1f + MathF.Exp(-t / 2.5f));
    }

    /// <summary>A deterministic per-photosite scatter, uniform in +-<paramref name="halfWidth"/>, one pattern per
    /// <paramref name="seed"/>.</summary>
    private static float Scatter(int x, int y, int seed, float halfWidth)
    {
        var h = (((uint)x * 73856093u) ^ ((uint)y * 19349663u) ^ ((uint)seed * 83492791u)) * 0x5bd1e995u;
        h ^= h >> 15;
        return ((h >> 8) / (float)(1u << 24) * 2f - 1f) * halfWidth;
    }

    private static ImageMeta Meta(FrameType type) => new("SVBONY SV605CC", DateTime.UnixEpoch, TimeSpan.FromSeconds(10),
        type, "", 3.76f, 3.76f, 120, 20, Filter.Unknown, 1, 1, -5f, SensorType.RGGB, 0, 0, RowOrder.TopDown, float.NaN, float.NaN);
}
