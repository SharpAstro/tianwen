using System;
using System.IO;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Planetary;
using TianWen.Lib.Stat;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Pins the high-key PLANETARY preview stretch (<see cref="Image.ComputePlanetaryStretchUniforms"/>):
/// a per-channel black point + a single COMMON scale + a gentle gamma, so a bright disk on a dark
/// sky renders correctly (disk in range, sky colour-neutral) where the deep-sky MTF auto-stretch
/// would blow the disk out to a white blob and a per-channel white point would tint the sky.
/// </summary>
public class PlanetaryPreviewStretchTests : IDisposable
{
    private readonly TempFolders _folders = new();

    public void Dispose() => _folders.Dispose();

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

    // A colour-balanced master (#1229): its sky at zero in every channel, its noise wider in blue, the channel the balance lifted most.
    private const int BalancedSize = 128;
    private static readonly double[] BalancedSkyNoise = [0.004, 0.004, 0.006];

    private static Image BuildBalancedPlanet(bool marked)
    {
        var random = new Random(1229);
        var planes = new float[3][,];
        for (var c = 0; c < 3; c++)
        {
            planes[c] = new float[BalancedSize, BalancedSize];
        }
        for (var y = 0; y < BalancedSize; y++)
        {
            for (var x = 0; x < BalancedSize; x++)
            {
                var (dx, dy) = (x - (BalancedSize / 2.0), y - (BalancedSize / 2.0));
                var signal = (dx * dx) + (dy * dy) < 16 * 16 ? 0.45 : 0;
                for (var c = 0; c < 3; c++)
                {
                    // A normal deviate by Box-Muller, so the sky's noise has the tails a percentile reads.
                    var gauss = Math.Sqrt(-2 * Math.Log(1 - random.NextDouble())) * Math.Cos(2 * Math.PI * random.NextDouble());
                    planes[c][y, x] = (float)(signal + (BalancedSkyNoise[c] * gauss));
                }
            }
        }
        return new Image(planes, BitDepth.Float32, 0.5f, -0.05f, 0f, new ImageMeta { SensorType = SensorType.Color, ColourBalanceSaturation = marked ? 1 : null });
    }

    // The rendered sky's mean in each channel, levels of 255, past 40 px from the disk's middle.
    private static (double R, double G, double B) RenderedSkyMean(Image image, in StretchUniforms u)
    {
        var rgba = new byte[BalancedSize * BalancedSize * 4];
        image.RenderStretchedRgba(u, rgba);
        double r = 0, g = 0, b = 0;
        var n = 0;
        for (var y = 0; y < BalancedSize; y++)
        {
            for (var x = 0; x < BalancedSize; x++)
            {
                var (dx, dy) = (x - (BalancedSize / 2.0), y - (BalancedSize / 2.0));
                if ((dx * dx) + (dy * dy) < 40 * 40)
                {
                    continue;
                }
                var o = ((y * BalancedSize) + x) * 4;
                (r, g, b, n) = (r + rgba[o], g + rgba[o + 1], b + rgba[o + 2], n + 1);
            }
        }
        return (r / n, g / n, b / n);
    }

    [Fact]
    public void ABalancedMastersSkyRendersAlikeInEveryChannel()
    {
        // Unmarked, the black point a channel sits 2.6 of its own noise below the sky, so the noisier blue renders navy (#1229).
        var unmarked = BuildBalancedPlanet(marked: false);
        var tinted = RenderedSkyMean(unmarked, unmarked.ComputePlanetaryStretchUniforms());
        (tinted.B - tinted.R).ShouldBeGreaterThan(2);

        // Marked as balanced, one black point for all three, the highest of their percentiles, and the sky renders alike.
        var balanced = BuildBalancedPlanet(marked: true);
        var u = balanced.ComputePlanetaryStretchUniforms();
        u.Pedestal.G.ShouldBe(u.Pedestal.R);
        u.Pedestal.B.ShouldBe(u.Pedestal.R);
        u.Pedestal.R.ShouldBe(StatisticsHelper.PercentileFast(balanced.GetChannelSpan(0).ToArray(), 0.005), 1e-6f);
        var sky = RenderedSkyMean(balanced, u);
        Math.Abs(sky.B - sky.R).ShouldBeLessThan(0.5);
        Math.Abs(sky.B - sky.G).ShouldBeLessThan(0.5);
        // No darker than the unmarked sky's red and green: the black point is theirs.
        sky.R.ShouldBe(tinted.R, 0.5);
    }

    [Fact]
    public void TheBalanceMarksTheMasterItMakes()
    {
        var captured = BuildSyntheticPlanet();
        captured.ImageMeta.IsColourBalanced.ShouldBeFalse();

        var balanced = PlanetaryColourBalance.Apply(captured, new LinearRgb(1, 1, 1.4), new LinearRgb(SkyFloor[0], SkyFloor[1], SkyFloor[2]),
            PlanetaryColourBalance.DefaultSaturation, PlanetaryColourBalance.JupiterDiskColour);

        balanced.ImageMeta.IsColourBalanced.ShouldBeTrue();
        captured.ImageMeta.IsColourBalanced.ShouldBeFalse();
    }

    [Fact]
    public async Task ABalancedMasterOpenedFromItsFileTakesOneBlackPointInTheViewer()
    {
        // The balance's own cards say so on disk (CBALSAT), and the viewer's planetary stretch reads them back through the document.
        var folder = _folders.Create("balanced-stretch");
        var path = Path.Combine(folder.FullName, "balanced.fits");
        var balance = new ColourBalance(new LinearRgb(1.1, 1, 1.4), default, PlanetaryColourBalance.DefaultSaturation, PlanetaryColourBalance.JupiterDiskColour);
        var unmarked = BuildBalancedPlanet(marked: false);
        unmarked.WriteToFitsFile(path, null, balance.HeaderCards());
        var plain = Path.Combine(folder.FullName, "plain.fits");
        unmarked.WriteToFitsFile(plain);

        var document = (await AstroImageDocument.OpenAsync(path, cancellationToken: TestContext.Current.CancellationToken)).ShouldNotBeNull();
        document.UnstretchedImage.ImageMeta.IsColourBalanced.ShouldBeTrue();
        var u = document.ComputeStretchUniforms(StretchMode.Planetary, StretchParameters.Default);
        u.Pedestal.G.ShouldBe(u.Pedestal.R);
        u.Pedestal.B.ShouldBe(u.Pedestal.R);

        var other = (await AstroImageDocument.OpenAsync(plain, cancellationToken: TestContext.Current.CancellationToken)).ShouldNotBeNull();
        other.UnstretchedImage.ImageMeta.IsColourBalanced.ShouldBeFalse();
        var perChannel = other.ComputeStretchUniforms(StretchMode.Planetary, StretchParameters.Default);
        perChannel.Pedestal.B.ShouldBeLessThan(perChannel.Pedestal.R);
    }
}
