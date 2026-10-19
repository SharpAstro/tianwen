using System;
using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Calibration;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Pins that the dark-derived mask judges a photosite against its OWN NEIGHBOURS. The SV605CC's dark runs warm in one
/// corner (916 against 804 ADU, falling off over about 20 px); judged against the lattice's median the whole corner was
/// flagged, and as the Tarantula's subs of 2026-10-17 moved only +-9 px no frame filled it in the drizzled master.
/// </summary>
public class BadPixelWarmCornerTests
{
    private const int Size = 256;
    private const float Floor = 804f;
    private const int TipAllowance = 8;   // two per Bayer position

    /// <summary>The corner's very tip is a local peak of a curved surface and may cost a photosite or two per Bayer
    /// position once the budget walk has lowered the threshold to the read noise (7 here); the REGION is never flagged,
    /// where the lattice-median test took about 820 photosites on the real dark.</summary>
    [Fact]
    public void AWarmCornerIsDarkCurrentNotADefect()
    {
        var mask = BadPixelDetection.BuildMaskFromDark(Dark(hot: []), 8f).ShouldNotBeNull().ShouldHaveSingleItem();

        var flagged = 0;
        for (var y = 0; y < 40; y++)
        {
            for (var x = 0; x < 40; x++)
            {
                flagged += mask[y, x] ? 1 : 0;
            }
        }
        flagged.ShouldBeLessThanOrEqualTo(TipAllowance, "the warm corner is dark current the dark subtracts");
    }

    [Fact]
    public void AHotPixelIsStillFoundInsideAndOutsideTheCorner()
    {
        (int X, int Y)[] hot = [(3, 4), (10, 7), (120, 90), (201, 230)];

        var mask = BadPixelDetection.BuildMaskFromDark(Dark(hot), 8f).ShouldNotBeNull().ShouldHaveSingleItem();

        foreach (var (x, y) in hot)
        {
            mask[y, x].ShouldBeTrue($"the hot pixel at ({x}, {y})");
        }
        BadPixelDetection.CountMaskedPixels([mask]).ShouldBeLessThanOrEqualTo(hot.Length + TipAllowance, "the planted defects and at most the corner's tip");
    }

    /// <summary>An RGGB dark: a floor with read noise in whole ADU, a smooth warm corner at the origin, and
    /// the given hot photosites 300 ADU above whatever is under them.</summary>
    private static Image Dark((int X, int Y)[] hot)
    {
        var data = new float[Size, Size];
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                var noise = (((uint)x * 73856093u) ^ ((uint)y * 19349663u)) * 0x5bd1e995u >> 29;   // 0..7
                var glow = 112f * MathF.Exp(-MathF.Sqrt((x * x) + (y * y)) / 20f);   // 916 at the tip, as measured
                data[y, x] = MathF.Round(Floor + (noise - 3.5f) + glow);
            }
        }
        foreach (var (x, y) in hot)
        {
            data[y, x] += 300f;
        }
        var meta = new ImageMeta("SVBONY SV605CC", DateTime.UnixEpoch, TimeSpan.FromSeconds(120),
            FrameType.Dark, "", 3.76f, 3.76f, 120, 20, Filter.Unknown, 1, 1,
            -5f, SensorType.RGGB, 0, 0, RowOrder.TopDown, float.NaN, float.NaN);
        return new Image([data], BitDepth.Float32, 1300f, Floor - 4f, 0f, meta);
    }
}
