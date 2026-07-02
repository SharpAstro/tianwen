using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Stacking;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Pins the masked-finishing-boost render stage (<see cref="MasterPreviewRenderer.ApplyMaskedBoost"/>):
/// the opt-in <c>stack --saturation</c> / <c>--contrast-boost</c> pass that runs on the STRETCHED
/// rgba16 buffer between <see cref="Image.RenderStretchedRgba16"/> and the PNG encode. Driven through
/// the internal buffer-in/buffer-out seam directly -- no renderer solve, no PNG round-trip -- so the
/// assertions isolate exactly what the stage does: boost mid-tone signal, protect background and
/// highlights (star cores), never touch alpha.
/// </summary>
[Collection("Stacking")]
public class MasterPreviewMaskedBoostTests
{
    private const int W = 64;
    private const int H = 64;

    // Region geometry: everything is sized so sampled centres sit further from any region
    // edge than the mask's default feather radius (blurSigma 3 -> kernel radius 9).
    private static bool InSignal(int x, int y) => x is >= 8 and < 32 && y is >= 8 and < 32;
    private static bool InHighlight(int x, int y) => x is >= 42 and < 62 && y is >= 42 and < 62;

    /// <summary>
    /// A synthetic STRETCHED rgba16 frame, background-dominated like a finished deep-sky render:
    /// a dim neutral background, one mid-tone coloured "nebula" block, one near-white "star core"
    /// block. Alpha is opaque.
    /// </summary>
    private static ushort[] SyntheticStretchedRgba()
    {
        var rgba = new ushort[W * H * 4];
        for (var y = 0; y < H; y++)
        {
            for (var x = 0; x < W; x++)
            {
                float r, g, b;
                if (InSignal(x, y))
                {
                    (r, g, b) = (0.60f, 0.45f, 0.30f);
                }
                else if (InHighlight(x, y))
                {
                    (r, g, b) = (0.97f, 0.96f, 0.95f);
                }
                else
                {
                    (r, g, b) = (0.08f, 0.08f, 0.08f);
                }
                var i = (y * W + x) * 4;
                rgba[i] = (ushort)(r * 65535f + 0.5f);
                rgba[i + 1] = (ushort)(g * 65535f + 0.5f);
                rgba[i + 2] = (ushort)(b * 65535f + 0.5f);
                rgba[i + 3] = 65535;
            }
        }
        return rgba;
    }

    private static (ushort R, ushort G, ushort B, ushort A) Px(ushort[] rgba, int x, int y)
    {
        var i = (y * W + x) * 4;
        return (rgba[i], rgba[i + 1], rgba[i + 2], rgba[i + 3]);
    }

    [Fact]
    public void Saturation_boosts_signal_protects_background_and_highlights()
    {
        var rgba = SyntheticStretchedRgba();
        var before = (ushort[])rgba.Clone();

        MasterPreviewRenderer.ApplyMaskedBoost(rgba, channelCount: 3, W, H,
            new ImageMeta { SensorType = SensorType.Color },
            new MaskedBoostOptions(Saturation: 2f));

        // Mid-tone signal centre: R-B spread widened by the saturation boost.
        var sigBefore = Px(before, 19, 19);
        var sigAfter = Px(rgba, 19, 19);
        (sigAfter.R - sigAfter.B).ShouldBeGreaterThan(sigBefore.R - sigBefore.B);

        // Background far from any region (mask ~0): unchanged within u16 rounding.
        var bgBefore = Px(before, 4, 60);
        var bgAfter = Px(rgba, 4, 60);
        ((double)bgAfter.R).ShouldBe(bgBefore.R, 256.0);
        ((double)bgAfter.G).ShouldBe(bgBefore.G, 256.0);
        ((double)bgAfter.B).ShouldBe(bgBefore.B, 256.0);

        // Star-core centre (highlight roll-off): protected, near-unchanged.
        var hiBefore = Px(before, 51, 51);
        var hiAfter = Px(rgba, 51, 51);
        ((double)hiAfter.R).ShouldBe(hiBefore.R, 2600.0); // <= ~4% drift allowed for feather bleed
        ((double)hiAfter.G).ShouldBe(hiBefore.G, 2600.0);
        ((double)hiAfter.B).ShouldBe(hiBefore.B, 2600.0);

        // Alpha untouched everywhere.
        for (var i = 3; i < rgba.Length; i += 4)
        {
            rgba[i].ShouldBe((ushort)65535);
        }
    }

    [Fact]
    public void Mono_buffer_contrast_boost_stays_gray_and_in_range()
    {
        // Mono masters render gray into R=G=B; the boost must keep the replication intact.
        var rgba = SyntheticStretchedRgba();
        MasterPreviewRenderer.ApplyMaskedBoost(rgba, channelCount: 1, W, H,
            new ImageMeta(), new MaskedBoostOptions(ContrastBoost: 0.8f));

        for (var y = 0; y < H; y += 7)
        {
            for (var x = 0; x < W; x += 7)
            {
                var (r, g, b, a) = Px(rgba, x, y);
                g.ShouldBe(r);
                b.ShouldBe(r);
                a.ShouldBe((ushort)65535);
            }
        }
    }

    [Fact]
    public void StretchedImageFromRgba16_roundtrips_color_and_mono()
    {
        var rgba = SyntheticStretchedRgba();

        var color = MasterPreviewRenderer.StretchedImageFromRgba16(rgba, channelCount: 3, W, H, new ImageMeta());
        color.ChannelCount.ShouldBe(3);
        color[0, 19, 19].ShouldBe(0.60f, 1e-4f);
        color[1, 19, 19].ShouldBe(0.45f, 1e-4f);
        color[2, 19, 19].ShouldBe(0.30f, 1e-4f);

        var mono = MasterPreviewRenderer.StretchedImageFromRgba16(rgba, channelCount: 1, W, H, new ImageMeta());
        mono.ChannelCount.ShouldBe(1);
        mono[0, 4, 60].ShouldBe(0.08f, 1e-4f);
    }
}
