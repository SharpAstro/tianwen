using System;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// <b>A demosaic must not invent structure on a flat field.</b> Every interpolated sample is a
/// weighted pick among neighbours, and if the rule that picks them can see the sky's COLOUR rather
/// than only its structure, the pick is biased -- differently at red sites than at blue ones, which
/// are alternate rows and alternate columns. That lands as a two-pixel alternation on both axes: fine
/// stripes across the whole background, at 1:1, on every frame.
/// </summary>
/// <remarks>
/// <para>Found on a 120 s SVBONY SV605CC sub (GRBG, Optolong L-Quad): the sky sat at R 1472, G 2680,
/// B 2888 ADU and VNG's green alternated 6.4 display levels column to column against 12 levels of
/// pixel noise -- thirty times what MHC and AHD showed on the same frame. The cause was that VNG's
/// "gradients" were colour differences (<c>|2g - v - c|</c> is <c>2*(green - centre)</c> when flat),
/// so the 1.5x threshold selected on how near the interpolated channel sat to the centre pixel's own
/// colour: blue is near green and really did select, red is far and admitted everything.</para>
/// <para>The test is on a FLAT field on purpose. A flat field has one right answer per channel, so a
/// bias has nowhere to hide -- while on a real frame it is indistinguishable from the sky until it is
/// integrated over a whole row. Noise is essential too: the noise-free case passed throughout, since
/// with no noise there is nothing for a threshold to select between.</para>
/// </remarks>
[Collection("Imaging")]
public class VngFlatFieldBiasTests
{
    private const int Size = 192;
    private const float RedLevel = 1472f, GreenLevel = 2680f, BlueLevel = 2888f;

    /// <summary>The real sub's own per-channel noise, which is what the selection had to chew on.</summary>
    private const float RedSigma = 59f, GreenSigma = 100f, BlueSigma = 107f;

    /// <summary>
    /// A GRBG mosaic of one uniform sky: red at odd x / even y, blue at even x / odd y, green
    /// elsewhere, each photosite with its own channel's noise. Seeded, so a failure is reproducible.
    /// </summary>
    private static Image FlatMosaic(int seed)
    {
        var rng = new Random(seed);
        float Gauss()
        {
            var u1 = 1.0 - rng.NextDouble();
            var u2 = rng.NextDouble();
            return (float)(Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2));
        }

        var plane = new float[Size, Size];
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                var isRed = (x & 1) == 1 && (y & 1) == 0;
                var isBlue = (x & 1) == 0 && (y & 1) == 1;
                plane[y, x] = isRed ? RedLevel + RedSigma * Gauss()
                    : isBlue ? BlueLevel + BlueSigma * Gauss()
                    : GreenLevel + GreenSigma * Gauss();
            }
        }

        var meta = new ImageMeta
        {
            Instrument = "synth",
            SensorType = SensorType.RGGB,
            BayerOffsetX = 1,
            BayerOffsetY = 0,
        };
        return new Image([plane], BitDepth.Float32, maxValue: 65535f, minValue: 0f, pedestal: 0f, imageMeta: meta);
    }

    /// <summary>Mean of one output channel over the interior, at the photosites matching a predicate.</summary>
    private static double MeanWhere(Image rgb, int channel, Func<int, int, bool> site)
    {
        var data = rgb.GetChannelSpan(channel);
        var w = rgb.Width;
        double sum = 0;
        var n = 0;
        for (var y = 8; y < rgb.Height - 8; y++)
        {
            for (var x = 8; x < w - 8; x++)
            {
                if (!site(x, y))
                {
                    continue;
                }
                sum += data[y * w + x];
                n++;
            }
        }
        return n > 0 ? sum / n : double.NaN;
    }

    /// <summary>The strength of a two-sample alternation in a profile: the gap between its even and odd entries.</summary>
    private static double Alternation(double[] profile)
    {
        double even = 0, odd = 0;
        int ne = 0, no = 0;
        for (var i = 0; i < profile.Length; i++)
        {
            if ((i & 1) == 0) { even += profile[i]; ne++; } else { odd += profile[i]; no++; }
        }
        return Math.Abs(even / ne - odd / no);
    }

    private static double[] ColumnMeans(Image rgb, int channel)
    {
        var data = rgb.GetChannelSpan(channel);
        var w = rgb.Width;
        var prof = new double[w - 16];
        for (var x = 8; x < w - 8; x++)
        {
            double sum = 0;
            for (var y = 8; y < rgb.Height - 8; y++)
            {
                sum += data[y * w + x];
            }
            prof[x - 8] = sum / (rgb.Height - 16);
        }
        return prof;
    }

    private static double[] RowMeans(Image rgb, int channel)
    {
        var data = rgb.GetChannelSpan(channel);
        var w = rgb.Width;
        var prof = new double[rgb.Height - 16];
        for (var y = 8; y < rgb.Height - 8; y++)
        {
            double sum = 0;
            for (var x = 8; x < w - 8; x++)
            {
                sum += data[y * w + x];
            }
            prof[y - 8] = sum / (w - 16);
        }
        return prof;
    }

    /// <summary>
    /// <b>The bug itself.</b> Green interpolated at a blue site read 2779 against a true 2680 -- a
    /// whole green sigma -- while at a red site it was right, and the asymmetry is what made it
    /// visible: the two site kinds sit on opposite parities. A tolerance of 15 ADU is a seventh of
    /// the old error and well outside the ~3 ADU the mean of this many samples scatters by.
    /// </summary>
    [Theory]
    [InlineData(DebayerAlgorithm.VNG)]
    [InlineData(DebayerAlgorithm.MHC)]
    [InlineData(DebayerAlgorithm.AHD)]
    public async Task InterpolatedGreenIsUnbiasedAtBothRedAndBlueSites(DebayerAlgorithm algorithm)
    {
        var rgb = await FlatMosaic(seed: 42).DebayerAsync(algorithm, normalizeToUnit: false,
            TestContext.Current.CancellationToken);

        var atRed = MeanWhere(rgb, 1, (x, y) => (x & 1) == 1 && (y & 1) == 0);
        var atBlue = MeanWhere(rgb, 1, (x, y) => (x & 1) == 0 && (y & 1) == 1);

        atRed.ShouldBe(GreenLevel, 15.0, $"{algorithm}: green interpolated at a RED site");
        atBlue.ShouldBe(GreenLevel, 15.0, $"{algorithm}: green interpolated at a BLUE site");
    }

    /// <summary>
    /// And the consequence, stated the way it was seen: the picture itself, profiled along each axis,
    /// must carry no two-pixel alternation. This is the assertion that would have caught the stripes
    /// without anyone having to know which interpolation step produced them -- which is why it is here
    /// as well as the per-site test above.
    /// </summary>
    [Theory]
    [InlineData(DebayerAlgorithm.VNG)]
    [InlineData(DebayerAlgorithm.MHC)]
    [InlineData(DebayerAlgorithm.AHD)]
    public async Task NoTwoPixelAlternationOnEitherAxisOfAFlatField(DebayerAlgorithm algorithm)
    {
        var rgb = await FlatMosaic(seed: 7).DebayerAsync(algorithm, normalizeToUnit: false,
            TestContext.Current.CancellationToken);

        // Per channel, in that channel's own noise units, so the bound means the same thing for a
        // quiet red plane as for a noisy blue one. The old VNG scored 0.62 sigma here; everything
        // correct scores under 0.05.
        var sigmas = new[] { RedSigma, GreenSigma, BlueSigma };
        var names = new[] { "red", "green", "blue" };
        for (var c = 0; c < 3; c++)
        {
            var noiseFloor = sigmas[c] / Math.Sqrt(Size - 16);

            Alternation(ColumnMeans(rgb, c)).ShouldBeLessThan(3 * noiseFloor,
                $"{algorithm}: {names[c]} alternates column to column on a flat field");
            Alternation(RowMeans(rgb, c)).ShouldBeLessThan(3 * noiseFloor,
                $"{algorithm}: {names[c]} alternates row to row on a flat field");
        }
    }
}
