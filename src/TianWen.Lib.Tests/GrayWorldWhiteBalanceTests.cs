using Shouldly;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Unit tests for <see cref="GrayWorldWhiteBalance"/> gray-world: it must equalise the channel means of the
/// illuminated pixels, normalise so the dimmest channel stays at 1.0 (only attenuate, never amplify), and
/// sample a Bayer mosaic at its CFA sites.
/// </summary>
public class GrayWorldWhiteBalanceTests
{
    private const double Tol = 1e-4;

    [Fact]
    public void GrayWorldRgb_UniformPatch_AttenuatesBrighterChannelsToTheDimmest()
    {
        // Uniform colour: R twice as bright as G == B. Gray-world should cut R to 0.5 and leave G, B at 1.
        var r = new float[64];
        var g = new float[64];
        var b = new float[64];
        for (var i = 0; i < 64; i++) { r[i] = 0.8f; g[i] = 0.4f; b[i] = 0.4f; }

        var wb = GrayWorldWhiteBalance.GrayWorldRgb(r, g, b);

        wb.HasValue.ShouldBeTrue();
        var (mr, mg, mb) = wb!.Value;
        mr.ShouldBe(0.5f, Tol);
        mg.ShouldBe(1.0f, Tol);
        mb.ShouldBe(1.0f, Tol);
    }

    [Fact]
    public void GrayWorldRgb_NeutralPatch_IsIdentity()
    {
        var r = new float[16];
        var g = new float[16];
        var b = new float[16];
        for (var i = 0; i < 16; i++) { r[i] = 0.6f; g[i] = 0.6f; b[i] = 0.6f; }

        var wb = GrayWorldWhiteBalance.GrayWorldRgb(r, g, b);

        wb.HasValue.ShouldBeTrue();
        var (mr, mg, mb) = wb!.Value;
        mr.ShouldBe(1.0f, Tol);
        mg.ShouldBe(1.0f, Tol);
        mb.ShouldBe(1.0f, Tol);
    }

    [Fact]
    public void GrayWorldRgb_BackgroundExcludedFromMeans()
    {
        // A dim "sky" majority (below the 0.2 * peak threshold) plus a small bright "planet" patch with a
        // colour cast. The means must come from the planet only -- if the sky leaked in, the near-equal dim
        // pixels would pull the multipliers toward 1.
        var r = new float[100];
        var g = new float[100];
        var b = new float[100];
        for (var i = 0; i < 100; i++) { r[i] = 0.01f; g[i] = 0.01f; b[i] = 0.01f; } // sky
        for (var i = 0; i < 10; i++) { r[i] = 1.0f; g[i] = 0.5f; b[i] = 0.5f; }       // planet (R 2x G/B)

        var wb = GrayWorldWhiteBalance.GrayWorldRgb(r, g, b);

        wb.HasValue.ShouldBeTrue();
        var (mr, mg, mb) = wb!.Value;
        mr.ShouldBe(0.5f, Tol);
        mg.ShouldBe(1.0f, Tol);
        mb.ShouldBe(1.0f, Tol);
    }

    [Fact]
    public void GrayWorldBayer_RGGB_SamplesCfaSitesPerColour()
    {
        // 4x4 RGGB mosaic, offset (0,0): R at even/even, B at odd/odd, G at the other two. R sites are
        // twice the G/B sites, so the result must cut R to 0.5 and leave G, B at 1 -- proving the mosaic
        // was sampled by CFA colour, not averaged as one plane.
        const int w = 4, h = 4;
        var mosaic = new float[w * h];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var isR = (x & 1) == 0 && (y & 1) == 0;
                var isB = (x & 1) == 1 && (y & 1) == 1;
                mosaic[y * w + x] = isR ? 0.8f : isB ? 0.4f : 0.4f;
            }
        }

        var wb = GrayWorldWhiteBalance.GrayWorldBayer(mosaic, w, h, bayerOffsetX: 0, bayerOffsetY: 0);

        wb.HasValue.ShouldBeTrue();
        var (mr, mg, mb) = wb!.Value;
        mr.ShouldBe(0.5f, Tol);
        mg.ShouldBe(1.0f, Tol);
        mb.ShouldBe(1.0f, Tol);
    }

    [Fact]
    public void GrayWorld_OnlyEverAttenuates_NoMultiplierAboveOne()
    {
        // Whatever the cast, the dimmest channel pins at 1.0 and the rest are <= 1 (never amplify noise).
        var r = new float[16];
        var g = new float[16];
        var b = new float[16];
        for (var i = 0; i < 16; i++) { r[i] = 0.9f; g[i] = 0.7f; b[i] = 0.3f; }

        var wb = GrayWorldWhiteBalance.GrayWorldRgb(r, g, b);

        wb.HasValue.ShouldBeTrue();
        var (mr, mg, mb) = wb!.Value;
        mr.ShouldBeLessThanOrEqualTo(1.0f);
        mg.ShouldBeLessThanOrEqualTo(1.0f);
        mb.ShouldBe(1.0f, Tol); // B is the dimmest -> pinned at 1
    }

    [Fact]
    public void GrayWorldRgb_AllBlack_ReturnsNull()
    {
        var black = new float[16];
        GrayWorldWhiteBalance.GrayWorldRgb(black, black, black).HasValue.ShouldBeFalse();
    }

    [Fact]
    public void GrayWorldBayer_DimensionMismatch_ReturnsNull()
    {
        var mosaic = new float[10];
        GrayWorldWhiteBalance.GrayWorldBayer(mosaic, width: 4, height: 4, bayerOffsetX: 0, bayerOffsetY: 0)
            .HasValue.ShouldBeFalse();
    }
}
