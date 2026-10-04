using System;
using System.Threading.Tasks;
using SharpAstro.Ser;
using Shouldly;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Correctness guards for the Malvar-He-Cutler CPU debayer (<see cref="DebayerAlgorithm.MHC"/>,
/// <c>Image.DebayerMHCAsync</c>). MHC has three independent implementations that must agree:
/// this CPU one, the GPU <c>debayerMhc</c> branch in <c>VkFitsImagePipeline</c>, and the canonical
/// <c>SharpAstro.Ser.SerImaging.DebayerMhc</c>. The CPU one is pinned here against the SER.Lib reference (same 5x5
/// kernels, the same mirror at the frame's edge), and against the shader by <c>GpuVngDebayerParityTests</c>.
/// </summary>
public class DebayerMhcTests
{
    private const int MaxSample = 65535;

    /// <summary>
    /// The CPU MHC debayer must reproduce <see cref="SerImaging.DebayerMhc"/> pixel-for-pixel.
    /// Both apply the identical 5x5 kernels with neighbours mirrored at the frame's edge; SER normalises by
    /// <c>1/maxSampleValue</c> and clamps to [0,1], so we normalise + clamp the CPU output the same way.
    /// </summary>
    [Fact]
    public async Task DebayerMHC_MatchesSerImagingReference()
    {
        const int w = 16, h = 16;
        var samples = BuildMosaic(w, h);

        // Canonical reference: SER.Lib decodes the RGGB mosaic to linear RGB in [0,1] (clamped).
        var reference = SerImaging.DecodeToLinearRgb(samples, w, h, SerColorId.BayerRGGB, MaxSample, SerDebayer.Mhc);

        // TianWen CPU MHC on the same raw mosaic, normalised to unit by the same 1/maxSampleValue.
        var image = BuildRggbImage(samples, w, h);
        var debayered = await image.DebayerAsync(DebayerAlgorithm.MHC, normalizeToUnit: true, TestContext.Current.CancellationToken);
        debayered.ChannelCount.ShouldBe(3);

        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var p = ((y * w) + x) * 3;
                // SerImaging clamps to [0,1]; mirror that on the CPU output before comparing.
                AssertClose(reference[p + 0], Math.Clamp(debayered[0, y, x], 0f, 1f), x, y, 'R');
                AssertClose(reference[p + 1], Math.Clamp(debayered[1, y, x], 0f, 1f), x, y, 'G');
                AssertClose(reference[p + 2], Math.Clamp(debayered[2, y, x], 0f, 1f), x, y, 'B');
            }
        }
    }

    /// <summary>
    /// Unity gain: every MHC kernel sums to 8 (x0.125 = 1), so a flat mosaic must debayer to a flat
    /// grey field with no brightness shift, on the interior AND the mirrored edges. This catches a
    /// transcription slip in any single kernel (a wrong coefficient breaks the sum-to-8 invariant).
    /// </summary>
    [Fact]
    public async Task DebayerMHC_FlatFieldIsUnityGain()
    {
        const int w = 12, h = 12;
        const ushort flat = 40000;
        var samples = new ushort[w * h];
        Array.Fill(samples, flat);

        var image = BuildRggbImage(samples, w, h);
        var debayered = await image.DebayerAsync(DebayerAlgorithm.MHC, normalizeToUnit: true, TestContext.Current.CancellationToken);

        var expected = (float)flat / MaxSample;
        for (var c = 0; c < 3; c++)
        {
            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    debayered[c, y, x].ShouldBe(expected, 1e-4f,
                        $"flat field must stay flat: channel {c} at ({x}, {y})");
                }
            }
        }
    }

    /// <summary>
    /// Each colour flat at its own level (a sky with a pedestal a channel) must debayer to those levels at EVERY pixel, the outer two
    /// rows and columns included: a tap outside the plane has to read its own colour (#1258). Repeating the edge sample read the
    /// neighbouring colour there, which put a planetary master's blue edge row 13 % below its sky. Every pattern phase and odd and
    /// even sizes, so each edge is met by each colour.
    /// </summary>
    [Theory]
    [InlineData(0, 0, 16, 12)]
    [InlineData(1, 1, 15, 13)]
    [InlineData(1, 0, 9, 10)]
    [InlineData(0, 1, 10, 9)]
    public async Task DebayerMHC_EachColourFlatReproducesItsLevelUpToTheFramesEdge(int offsetX, int offsetY, int w, int h)
    {
        const ushort red = 30000, green = 20000, blue = 40000;
        var samples = new ushort[w * h];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var (xRed, yRed) = ((x & 1) == offsetX, (y & 1) == offsetY);
                samples[(y * w) + x] = xRed && yRed ? red : !xRed && !yRed ? blue : green;
            }
        }

        var image = BuildRggbImage(samples, w, h, offsetX, offsetY);
        var debayered = await image.DebayerAsync(DebayerAlgorithm.MHC, normalizeToUnit: true, TestContext.Current.CancellationToken);

        ReadOnlySpan<float> expected = [red / (float)MaxSample, green / (float)MaxSample, blue / (float)MaxSample];
        for (var c = 0; c < 3; c++)
        {
            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    debayered[c, y, x].ShouldBe(expected[c], 1e-5f, $"channel {c} at ({x}, {y})");
                }
            }
        }
    }

    // A 5x5 kernel on a plane narrower than its reach mirrors more than once, down to a single sample.
    [Theory]
    [InlineData(3, 3)]
    [InlineData(2, 2)]
    [InlineData(1, 1)]
    [InlineData(2, 5)]
    public async Task DebayerMHC_ASmallPlaneDebayersWithoutReadingPastIt(int w, int h)
    {
        var samples = new ushort[w * h];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = (ushort)(1000 * (i + 1));
        }

        var debayered = await BuildRggbImage(samples, w, h).DebayerAsync(DebayerAlgorithm.MHC, normalizeToUnit: true, TestContext.Current.CancellationToken);

        (debayered.ChannelCount, debayered.Width, debayered.Height).ShouldBe((3, w, h));
    }

    private static void AssertClose(float expected, float actual, int x, int y, char ch)
    {
        if (MathF.Abs(expected - actual) > 1e-4f)
        {
            actual.ShouldBe(expected, 1e-4f, $"CPU MHC != SerImaging reference at {ch} ({x}, {y})");
        }
    }

    // Deterministic, full-range mosaic with strong local gradients so the MHC Laplacian-correction
    // terms are actually exercised (a flat or low-variation field would hide a wrong gradient coeff).
    private static ushort[] BuildMosaic(int w, int h)
    {
        var samples = new ushort[w * h];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                samples[(y * w) + x] = (ushort)((((x * 211) + (y * 97) + (x * y)) * 7) & 0xFFFF);
            }
        }
        return samples;
    }

    private static Image BuildRggbImage(ushort[] samples, int w, int h, int bayerOffsetX = 0, int bayerOffsetY = 0)
    {
        var channel = new float[h, w];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                channel[y, x] = samples[(y * w) + x];
            }
        }

        var meta = new ImageMeta(
            Instrument: "synthetic",
            ExposureStartTime: DateTimeOffset.UnixEpoch,
            ExposureDuration: TimeSpan.FromSeconds(1),
            FrameType: FrameType.Light,
            Telescope: "synthetic",
            PixelSizeX: 3.76f,
            PixelSizeY: 3.76f,
            FocalLength: 275,
            FocusPos: -1,
            Filter: Filter.None,
            BinX: 1, BinY: 1,
            CCDTemperature: float.NaN,
            SensorType: SensorType.RGGB,
            BayerOffsetX: bayerOffsetX, BayerOffsetY: bayerOffsetY,
            RowOrder: RowOrder.TopDown,
            Latitude: float.NaN,
            Longitude: float.NaN);

        return new Image([channel], BitDepth.Int16, maxValue: MaxSample, minValue: 0f, pedestal: 0f, imageMeta: meta);
    }
}
