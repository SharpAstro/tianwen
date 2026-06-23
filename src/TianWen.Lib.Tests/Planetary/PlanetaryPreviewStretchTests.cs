using System;
using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.Lib.Stat;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Pins the high-key PLANETARY preview stretch (<see cref="Image.ComputePlanetaryStretchUniforms"/>):
/// a per-channel black point + a single COMMON scale + a gentle gamma, so a bright disk on a dark
/// sky renders correctly (disk in range, sky colour-neutral) where the deep-sky MTF auto-stretch
/// would blow the disk out to a white blob and a per-channel white point would tint the sky.
/// </summary>
public class PlanetaryPreviewStretchTests
{
    private const int N = 64;
    // Unequal per-channel sky floors -- the "blue trap": B sits on a higher floor than R/G, so a
    // per-channel white-point stretch would tint the faint sky blue. The common-scale stretch must
    // remove the floor difference and keep the sky neutral.
    private static readonly float[] SkyFloor = [0.02f, 0.03f, 0.06f];

    /// <summary>
    /// 64x64 RGB synthetic planet: a bright uniform disk (r &lt; 12), a faint halo ring
    /// (12 &lt;= r &lt; 20), and a noisy sky elsewhere. The disk + halo SIGNAL is identical across
    /// channels (added on top of the per-channel floor) so a correct stretch renders them neutral.
    /// </summary>
    private static Image BuildSyntheticPlanet()
    {
        var r = new float[N, N];
        var g = new float[N, N];
        var b = new float[N, N];
        const float cx = 32f, cy = 32f;
        for (var y = 0; y < N; y++)
        {
            for (var x = 0; x < N; x++)
            {
                var dist = MathF.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
                float signal;
                if (dist < 12f) signal = 0.45f;          // disk body (brightest)
                else if (dist < 20f) signal = 0.06f;     // faint halo
                else signal = ((x * 31 + y * 17) % 7) * 0.002f; // sky: deterministic low noise

                r[y, x] = SkyFloor[0] + signal;
                g[y, x] = SkyFloor[1] + signal;
                b[y, x] = SkyFloor[2] + signal;
            }
        }

        return new Image([r, g, b], BitDepth.Float32, 1f, 0f, 0f, new ImageMeta { SensorType = SensorType.Color });
    }

    [Fact]
    public void PercentileFast_matches_a_sorted_lookup()
    {
        // 0..100 inclusive: the value at fractional rank p is the value at index (int)(p * 100)
        // (truncated). PercentileFast only permutes the buffer (never changes the multiset), so
        // successive reads on the same array are well-defined -- no per-call copy needed.
        var src = new float[101];
        for (var i = 0; i < src.Length; i++) src[i] = i;

        StatisticsHelper.PercentileFast(src, 0.0).ShouldBe(0f);
        StatisticsHelper.PercentileFast(src, 0.5).ShouldBe(50f);
        StatisticsHelper.PercentileFast(src, 0.999).ShouldBe(99f); // (int)(0.999 * 100) == 99
        StatisticsHelper.PercentileFast(src, 1.0).ShouldBe(100f);
        StatisticsHelper.PercentileFast(new float[] { 42f }, 0.3).ShouldBe(42f);
        StatisticsHelper.PercentileFast(Span<float>.Empty, 0.5).ShouldBe(float.NaN);
    }

    [Fact]
    public void Planetary_uniforms_use_a_common_scale_and_per_channel_black_point()
    {
        var img = BuildSyntheticPlanet();

        var u = img.ComputePlanetaryStretchUniforms(gamma: 1.0); // pure linear: midtones identity

        u.Mode.ShouldBe(StretchMode.Unlinked);
        u.NormFactor.ShouldBe(1f);
        u.Shadows.ShouldBe((0f, 0f, 0f));
        u.Highlights.ShouldBe((1f, 1f, 1f));

        // gamma 1.0 -> midtones 0.5 (MTF identity) on every channel.
        u.Midtones.R.ShouldBe(0.5f, 1e-4f);
        u.Midtones.G.ShouldBe(0.5f, 1e-4f);
        u.Midtones.B.ShouldBe(0.5f, 1e-4f);

        // ONE common scale across channels (preserves channel ratios -> neutral sky).
        u.Rescale.R.ShouldBe(u.Rescale.G);
        u.Rescale.G.ShouldBe(u.Rescale.B);

        // Per-channel black point tracks each channel's sky floor (R < G < B).
        u.Pedestal.R.ShouldBeLessThan(u.Pedestal.G);
        u.Pedestal.G.ShouldBeLessThan(u.Pedestal.B);
        u.Pedestal.R.ShouldBe(SkyFloor[0], 0.01f);
        u.Pedestal.G.ShouldBe(SkyFloor[1], 0.01f);
        u.Pedestal.B.ShouldBe(SkyFloor[2], 0.01f);
    }

    [Fact]
    public void Planetary_stretch_keeps_a_neutral_sky_and_a_bright_unblown_disk()
    {
        var img = BuildSyntheticPlanet();
        var u = img.ComputePlanetaryStretchUniforms(gamma: 1.0);

        var rgba = new byte[N * N * 4];
        img.RenderStretchedRgba(u, rgba);

        static (int R, int G, int B) At(byte[] buf, int x, int y)
        {
            var o = (y * N + x) * 4;
            return (buf[o], buf[o + 1], buf[o + 2]);
        }

        // Sky (corner): dark AND neutral -- the unequal channel floors (incl. the higher B floor)
        // are removed by the per-channel black point, so no blue cast survives.
        var sky = At(rgba, 2, 1);
        sky.R.ShouldBeLessThan(20);
        sky.G.ShouldBeLessThan(20);
        sky.B.ShouldBeLessThan(20);
        Math.Abs(sky.R - sky.G).ShouldBeLessThanOrEqualTo(4);
        Math.Abs(sky.G - sky.B).ShouldBeLessThanOrEqualTo(4);

        // Disk centre: bright (NOT crushed) and roughly neutral. The deep-sky path would push the
        // whole frame here; the planetary path keeps the sky dark, so a bright disk is the signal.
        var disk = At(rgba, 32, 32);
        disk.R.ShouldBeGreaterThan(245);
        disk.G.ShouldBeGreaterThan(245);
        disk.B.ShouldBeGreaterThan(245);

        // Faint halo: a distinct MID grey -- visible (not black) but well below the disk (not blown
        // out to white). This is the property the deep-sky MTF stretch destroys.
        var halo = At(rgba, 32, 48);
        var haloLuma = (halo.R + halo.G + halo.B) / 3;
        haloLuma.ShouldBeInRange(15, 110);
        haloLuma.ShouldBeGreaterThan((sky.R + sky.G + sky.B) / 3);
        haloLuma.ShouldBeLessThan((disk.R + disk.G + disk.B) / 3);
        // Halo stays neutral too.
        Math.Abs(halo.R - halo.B).ShouldBeLessThanOrEqualTo(6);
    }
}
