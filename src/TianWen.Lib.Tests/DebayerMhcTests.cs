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
/// <c>SharpAstro.Ser.SerImaging.DebayerMhc</c>. The GPU one can't be unit-tested headless, so we pin
/// the CPU impl directly against the SER.Lib reference (same 5x5 kernels) -- if the CPU and the
/// reference match, and the GPU mirrors the same documented coefficients, all three stay in lock-step.
/// </summary>
public class DebayerMhcTests
{
    private const int MaxSample = 65535;

    /// <summary>
    /// The CPU MHC debayer must reproduce <see cref="SerImaging.DebayerMhc"/> pixel-for-pixel.
    /// Both apply the identical 5x5 kernels with clamp-to-edge neighbours; SER normalises by
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
    /// grey field with no brightness shift, on the interior AND the clamped edges. This catches a
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

    private static Image BuildRggbImage(ushort[] samples, int w, int h)
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
            BayerOffsetX: 0, BayerOffsetY: 0,
            RowOrder: RowOrder.TopDown,
            Latitude: float.NaN,
            Longitude: float.NaN);

        return new Image([channel], BitDepth.Int16, maxValue: MaxSample, minValue: 0f, pedestal: 0f, imageMeta: meta);
    }
}
