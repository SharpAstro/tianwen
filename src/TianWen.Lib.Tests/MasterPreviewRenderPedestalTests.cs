using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Stacking;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Regression guards for the unified display render (<see cref="MasterPreviewRenderer"/>).
/// These pin the two colour bugs the WB-once + zero-pedestal refactor fixed, neither of
/// which the pre-existing stacking / stretch tests would have caught:
/// <list type="bullet">
///   <item><b>Pedestal blow-out</b>: an enhanced (GraXpert-flattened) master sits on a
///   high <see cref="Image.MinValue"/> floor. Subtracting that floor as a pedestal drove
///   faint per-channel medians negative (the whole frame rendered black) or amplified tiny
///   per-channel differences into a hard cast (green crushed). <c>WithZeroPedestal</c> must
///   neutralise this so the auto-stretch's own shadow clipping sets the black point.</item>
///   <item><b>Background neutrality</b>: a neutral linear master must render with a neutral
///   background regardless of the floor it sits on.</item>
/// </list>
/// Driven through the public solve-only <see cref="MasterPreviewRenderer.RenderAsync"/>
/// (empty output path = no file write, returns the <see cref="StretchUniforms"/>), then the
/// CPU <see cref="Image.RenderStretchedRgba16"/> mirror -- no GPU, no reflection.
/// </summary>
[Collection("Stacking")]
public class MasterPreviewRenderPedestalTests
{
    /// <summary>
    /// Builds a synthetic 3-channel linear master: a neutral background at
    /// <paramref name="background"/> with deterministic faint per-channel noise and a
    /// scattering of brighter "signal" pixels, wrapped with an explicit
    /// <paramref name="minValue"/> floor (the metadata pedestal). When
    /// <paramref name="minValue"/> sits at or above the background, this reproduces the
    /// bogus-floor enhanced master that rendered black before the fix.
    /// </summary>
    private static Image SyntheticNeutralMaster(int w, int h, float background, float minValue, float redBias = 0f)
    {
        var r = new float[h, w];
        var g = new float[h, w];
        var b = new float[h, w];
        // Deterministic pseudo-noise (no Math.Random -- reproducible).
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var n = ((x * 1103515245 + y * 12345) & 0xFFFF) / 65535f * 0.01f; // [0, 0.01)
                r[y, x] = background + n + redBias;
                g[y, x] = background + n;
                b[y, x] = background + n;
                // Sparse bright signal so the stretch has a real dynamic range to map.
                if (((x ^ y) & 0x1F) == 0)
                {
                    r[y, x] = 0.55f;
                    g[y, x] = 0.55f;
                    b[y, x] = 0.55f;
                }
            }
        }
        var meta = new ImageMeta("synth", DateTime.UnixEpoch, TimeSpan.FromSeconds(60),
            FrameType.Light, "", 3.76f, 3.76f, 500, -1, Filter.Luminance, 1, 1,
            float.NaN, SensorType.Color, 0, 0, RowOrder.TopDown, float.NaN, float.NaN);
        // maxValue=1, the explicit floor as minValue. pedestal arg left 0 (the integrator
        // convention); the renderer reads the pedestal from MinValue/MaxValue.
        return new Image([r, g, b], BitDepth.Float32, 1.0f, minValue, 0f, meta);
    }

    private static (double R, double G, double B) BackgroundMedianRatios(ushort[] rgba, int w, int h)
    {
        // Median per channel over the whole frame (background-dominated by construction).
        var n = w * h;
        var rr = new ushort[n];
        var gg = new ushort[n];
        var bb = new ushort[n];
        for (var i = 0; i < n; i++)
        {
            rr[i] = rgba[i * 4];
            gg[i] = rgba[i * 4 + 1];
            bb[i] = rgba[i * 4 + 2];
        }
        Array.Sort(rr); Array.Sort(gg); Array.Sort(bb);
        return (rr[n / 2], gg[n / 2], bb[n / 2]);
    }

    private static async Task<PreviewRender> SolveAsync(Image img, CancellationToken ct)
    {
        var renderer = new MasterPreviewRenderer(catalogDb: null, NullLogger.Instance);
        // Empty output path -> solve only, no PNG written. Neutral WB override skips SPCC
        // (no catalog / WCS needed) so the test isolates the bg-neut + stretch behaviour.
        return await renderer.RenderAsync(
            img, img.ImageMeta, wcs: null, statsSource: null, outputPath: string.Empty,
            whiteBalanceOverride: (1f, 1f, 1f), ct: ct);
    }

    private static async Task<ushort[]> RenderAsync(Image img, CancellationToken ct)
    {
        var render = await SolveAsync(img, ct);
        var rgba = new ushort[img.Width * img.Height * 4];
        img.RenderStretchedRgba16(render.Uniforms, rgba);
        return rgba;
    }

    [Theory]
    [InlineData(0.40f)]  // GraXpert-flattened floor just below the background
    [InlineData(0.16f)]  // bogus floor sitting ABOVE the background (the drizzle case)
    public async Task Render_ForcesZeroPedestal_RegardlessOfMinValue(float minValue)
    {
        // The load-bearing contract of WithZeroPedestal: the stretch must NOT subtract the
        // master's MinValue floor as a pedestal. The auto-stretch's own shadow clipping sets
        // the black point; leaving the pedestal in (= MinValue/MaxValue) is what crushed a
        // channel / rendered the frame black on enhanced masters. Decisive + sensitive: the
        // uniform's Pedestal MUST be 0 here -- without the fix it would equal minValue.
        var img = SyntheticNeutralMaster(64, 64, background: 0.49f, minValue: minValue);
        var render = await SolveAsync(img, TestContext.Current.CancellationToken);

        render.Uniforms.Pedestal.R.ShouldBe(0f, $"pedestal leaked (MinValue={minValue}); WithZeroPedestal regressed");
        render.Uniforms.Pedestal.G.ShouldBe(0f);
        render.Uniforms.Pedestal.B.ShouldBe(0f);
    }

    [Fact]
    public async Task Render_HighFloorNeutralMaster_RendersNeutralBackground()
    {
        // GraXpert-flattened neutral master: floor 0.40 just below the 0.49 background.
        // Pre-fix this amplified tiny per-channel residues into a cast; the background
        // must come out neutral (all channels within ~10%).
        var img = SyntheticNeutralMaster(64, 64, background: 0.49f, minValue: 0.40f);
        var rgba = await RenderAsync(img, TestContext.Current.CancellationToken);

        var (r, g, b) = BackgroundMedianRatios(rgba, img.Width, img.Height);
        g.ShouldBeGreaterThan(256, "background must not crush to black");
        g.ShouldBeLessThan(64000, "background must not saturate");
        (r / g).ShouldBeInRange(0.9, 1.1, $"R/G cast: R={r} G={g}");
        (b / g).ShouldBeInRange(0.9, 1.1, $"B/G cast: B={b} G={g}");
    }

    [Fact]
    public async Task Render_HighFloor_MatchesZeroFloor_Parity()
    {
        // The floor is a uniform DC offset the auto-stretch removes; the SAME pixel data
        // with MinValue=0 vs MinValue=0.40 must render to near-identical backgrounds.
        var lowFloor = SyntheticNeutralMaster(64, 64, background: 0.49f, minValue: 0f);
        var highFloor = SyntheticNeutralMaster(64, 64, background: 0.49f, minValue: 0.40f);

        var rgbaLow = await RenderAsync(lowFloor, TestContext.Current.CancellationToken);
        var rgbaHigh = await RenderAsync(highFloor, TestContext.Current.CancellationToken);

        var (_, gLow, _) = BackgroundMedianRatios(rgbaLow, lowFloor.Width, lowFloor.Height);
        var (_, gHigh, _) = BackgroundMedianRatios(rgbaHigh, highFloor.Width, highFloor.Height);

        // Within 5% of each other -- the floor must not change the rendered tone.
        var rel = Math.Abs(gHigh - gLow) / Math.Max(gLow, 1.0);
        rel.ShouldBeLessThan(0.05, $"high-floor render diverged from zero-floor: gLow={gLow} gHigh={gHigh}");
    }
}
