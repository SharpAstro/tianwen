using DIR.Lib;
using System;
using System.Threading.Tasks;
using SdlVulkan.Renderer;
using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.Lib.Tests.Helpers;
using TianWen.UI.Abstractions;
using TianWen.UI.Shared;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The shader's VNG demosaic against <see cref="Image.DebayerVNGAsync"/>, on the same mosaic.
/// </summary>
/// <remarks>
/// <para>This is the test the MHC pair never got: <c>DebayerMhcTests</c> pins the CPU implementation
/// against SER.Lib's and then TRUSTS that the GLSL transcription of the same kernels agrees, on the
/// grounds that a shader cannot be unit-tested headless. It can -- <see cref="OffscreenGpuFixture"/>
/// has been rendering the stretch pipeline offscreen since the GPU stretch tests landed -- and for
/// VNG the trust would be worth less anyway. MHC is five fixed 5x5 kernels, which either transcribe
/// or obviously do not; VNG makes a DECISION per direction per pixel, so a mirrored-but-wrong
/// threshold or a swapped gradient still produces a perfectly reasonable-looking image that simply
/// is not the one the file gets.</para>
///
/// <para>Why it matters more here than for any other shader stage: the viewer's Save writes what the
/// CPU debayer produces while the screen shows what the shader produces, so these two functions ARE
/// the "saved as displayed" promise. Every other stage (stretch, curves, HDR) is already covered by
/// <see cref="GpuStretchPipelineTests"/> and is deliberately held constant here -- the render runs
/// with <see cref="StretchMode.None"/>, neutral white balance and no curves, so both sides reduce to
/// clamp-and-quantise and any divergence localises to the demosaic.</para>
/// </remarks>
[Collection("Imaging")]
public sealed class GpuVngDebayerParityTests(OffscreenGpuFixture gpu, ITestOutputHelper output)
    : IClassFixture<OffscreenGpuFixture>
{
    private const int Width = 256;
    private const int Height = 256;

    /// <summary>Neutral everything: in <see cref="StretchMode.None"/> both the GLSL and
    /// <see cref="Image.RenderStretchedRgba"/> reduce to <c>clamp(value * whiteBalance)</c>, so with a
    /// unit white balance the 8-bit byte IS the demosaic output and nothing else.</summary>
    private static StretchUniforms Passthrough => new(
        StretchMode.None,
        NormFactor: 1f,
        Pedestal: (0f, 0f, 0f),
        Shadows: (0f, 0f, 0f),
        Midtones: (0.5f, 0.5f, 0.5f),
        Highlights: (1f, 1f, 1f),
        Rescale: (1f, 1f, 1f));

    [Fact]
    public async Task TheShaderVngIsTheCpuVng()
    {
        if (!gpu.VulkanAvailable)
        {
            Assert.Skip($"Vulkan runtime not available ({gpu.UnavailableReason})");
            return;
        }

        var ct = TestContext.Current.CancellationToken;
        var mosaic = BuildMosaic();
        var image = BuildRggbImage(mosaic);

        var cpuRgba = new byte[Width * Height * 4];
        var debayered = await image.DebayerAsync(DebayerAlgorithm.VNG, normalizeToUnit: false, ct);
        debayered.ChannelCount.ShouldBe(3);
        debayered.RenderStretchedRgba(Passthrough, cpuRgba);

        var vngMode = ImageRendererBase<object>.GpuDebayerMode(DebayerAlgorithm.VNG);
        var gpuRgba = await gpu.InvokeAsync(() => RenderMosaic(mosaic, vngMode), ct);

        var vng = Compare(cpuRgba, gpuRgba, "VNG shader vs VNG CPU");

        // The control, and it is not decoration. A shader whose VNG branch was never reached (a
        // renumbered mode, a fallthrough to bilinear) would still render something, and on a smooth
        // enough fixture every demosaic agrees to within a byte or two -- the parity assertion below
        // would then pass over a broken branch. So render the SAME mosaic through the MHC branch and
        // require that it disagrees loudly: that is what proves this fixture can tell demosaics apart
        // at all, and therefore that the parity number below means something.
        var control = Compare(cpuRgba, await gpu.InvokeAsync(
            () => RenderMosaic(mosaic, ImageRendererBase<object>.GpuDebayerMode(DebayerAlgorithm.MHC)), ct),
            "MHC shader vs VNG CPU (control)");

        // Three states measured on this machine, which is where the bounds below come from rather
        // than from taste. The middle column is the shader with ONE epsilon added to vngGreenAtRB's
        // threshold that the CPU does not have -- the smallest realistic transcription slip:
        //
        //                    correct   epsilon slip   MHC (a different algorithm)
        //   max byte diff          1              8                            86
        //   bytes differing >1  0.000%         0.471%                       5.335%
        //
        // The MEAN is useless here and measuring it is what said so: 0.49 / 0.50 / 0.64, all three
        // within a third of each other, because every one of them is dominated by a half-LSB floor
        // that belongs to neither algorithm -- the CPU truncates its byte (`(byte)(v * 255f)`) while
        // the GPU's UNORM8 attachment rounds, so about half of all bytes are off by one on ANY
        // CPU/GPU comparison. That is the floor GpuStretchPipelineTests set its 1.5 tolerance around.
        // What separates two demosaics is a few pixels differing by a lot, which only the extremes
        // see. So the outlier COUNT past that floor is the sensitive detector and max is the sanity
        // bound, not the other way round.
        control.Max.ShouldBeGreaterThan(24,
            "the fixture must be able to tell two demosaics apart, or the parity assertion is vacuous");
        control.OutlierFraction.ShouldBeGreaterThan(vng.OutlierFraction + 0.001,
            "a different algorithm must disagree with the CPU VNG in more places than the same one does");

        vng.Max.ShouldBeLessThan(24, "a wrong branch, not a tie-break");
        // 0.05%, which is ~98 of the 196608 bytes: zero were measured, and the headroom is for a
        // driver that rounds a step() edge the other way. VNG's direction test is a DISCRETE choice
        // (grad <= 1.5 * minGrad), so where two gradients are all but equal a last-ulp difference
        // legitimately admits or drops a direction, and the two candidate values need not be close.
        // Still an order below the 0.471% the smallest real slip produced.
        vng.OutlierFraction.ShouldBeLessThan(0.0005, "the two VNG implementations agree byte for byte");
    }

    private (double Mean, int Max, double OutlierFraction) Compare(byte[] cpuRgba, byte[] gpuRgba, string label)
    {
        // 1, not 0: one byte is the CPU-truncate / GPU-round floor that every comparison here carries
        // (see the caller), so counting from 2 up counts only real disagreements.
        const int PerByteTolerance = 1;

        cpuRgba.Length.ShouldBe(gpuRgba.Length);

        long absDiffSum = 0;
        var max = 0;
        var outliers = 0;
        Span<long> perChannelSum = stackalloc long[3];
        Span<int> perChannelMax = stackalloc int[3];

        for (var i = 0; i < cpuRgba.Length; i += 4)
        {
            for (var c = 0; c < 3; c++)
            {
                var d = Math.Abs(cpuRgba[i + c] - gpuRgba[i + c]);
                absDiffSum += d;
                perChannelSum[c] += d;
                if (d > max) max = d;
                if (d > perChannelMax[c]) perChannelMax[c] = d;
                if (d > PerByteTolerance) outliers++;
            }
        }

        var bytes = cpuRgba.Length / 4 * 3;
        var mean = absDiffSum / (double)bytes;
        var outlierFraction = outliers / (double)bytes;
        output.WriteLine($"{label}: mean={mean:F4}  max={max}  outliers(>{PerByteTolerance})={outlierFraction:P3}");
        // Per channel, because a red/blue swap is the failure this shape of code invites and it shows
        // as two large numbers and one small one rather than as a uniformly bad mean.
        output.WriteLine($"    R mean={perChannelSum[0] / (double)(bytes / 3):F4} max={perChannelMax[0]}" +
                         $"  G mean={perChannelSum[1] / (double)(bytes / 3):F4} max={perChannelMax[1]}" +
                         $"  B mean={perChannelSum[2] / (double)(bytes / 3):F4} max={perChannelMax[2]}");
        return (mean, max, outlierFraction);
    }

    /// <summary>
    /// Uploads the raw mosaic as a single channel and renders it through the RawBayer shader path at
    /// <paramref name="debayerMode"/>, which is the same route the live viewer takes for a CFA frame.
    /// </summary>
    private byte[] RenderMosaic(float[,] mosaic, int debayerMode)
    {
        var ctx = gpu.Ctx!;
        var renderer = gpu.Renderer!;
        var pipeline = gpu.Pipeline!;

        var flat = new float[Width * Height];
        Buffer.BlockCopy(mosaic, 0, flat, 0, flat.Length * sizeof(float));

        // All three slots get the mosaic although only channel 0 is sampled on this path: the
        // descriptor set binds three images whatever the source mode, and leaving 1 and 2 at whatever
        // size the previous test in this collection left them is how a shared fixture produces a
        // validation error that has nothing to do with the test that hit it.
        for (var ch = 0; ch < 3; ch++)
        {
            pipeline.UploadChannelTexture(flat, ch, Width, Height);
        }

        renderer.BeginOffscreenFrame(new RGBAColor32(0, 0, 0, 255)).ShouldBeTrue();
        var cmd = renderer.CurrentCommandBuffer;

        var u = Passthrough;
        pipeline.UpdateStretchUBO(
            cmd: cmd,
            channelCount: 1,
            stretchMode: (int)u.Mode,
            normFactor: u.NormFactor,
            curvesBoost: 0f,
            curvesMidpoint: 0.25f,
            hdrAmount: 0f,
            hdrKnee: 0.8f,
            pedestal: u.Pedestal,
            shadows: u.Shadows,
            midtones: u.Midtones,
            highlights: u.Highlights,
            rescale: u.Rescale,
            gridMode: 0,
            gridSpacingRA: 0f, gridSpacingDec: 0f, gridLineWidth: 0f,
            imageW: Width, imageH: Height,
            crPix1: 0, crPix2: 0, crValRA: 0, crValDec: 0,
            cdMatrix: ReadOnlySpan<float>.Empty,
            whiteBalance: u.WhiteBalance,
            bgNeutralization: u.BackgroundNeutralization,
            curvesMode: 0,
            curveData: ReadOnlySpan<float>.Empty,
            imageSource: VkFitsImagePipeline.ImageSource.RawBayer,
            bayerOffsetX: 0, bayerOffsetY: 0,
            lumaWeights: u.LumaWeights,
            lumaStretch: u.LumaStretch,
            lumaBlend: u.LumaBlend,
            normalizeScale: u.NormalizeScale,
            debayerMode: debayerMode);

        pipeline.RecordImageDraw(cmd, ctx, 0, 0, Width, Height, OffscreenGpuFixture.Width, OffscreenGpuFixture.Height);
        renderer.EndOffscreenFrame();

        return OffscreenGpuFixtureBase.ExtractSubRect(
            ctx.ReadbackOffscreenRgba(), OffscreenGpuFixture.Width, Width, Height);
    }

    private static Image BuildRggbImage(float[,] mosaic)
    {
        var meta = new ImageMeta(
            Instrument: "synthetic",
            ExposureStartTime: DateTimeOffset.UnixEpoch,
            ExposureDuration: TimeSpan.FromSeconds(1),
            FrameType: FrameType.Light,
            Telescope: "synthetic",
            PixelSizeX: 3.76f,
            PixelSizeY: 3.76f,
            FocalLength: 530,
            FocusPos: 0,
            Filter: Filter.None,
            BinX: 1, BinY: 1,
            CCDTemperature: -10,
            SensorType: SensorType.RGGB,
            BayerOffsetX: 0, BayerOffsetY: 0,
            RowOrder: RowOrder.TopDown,
            Latitude: 0f, Longitude: 0f,
            Gain: 100,
            Aperture: 130,
            SensorModel: "IMX533");

        // maxValue 1 so DebayerAsync's unit-scale divisor is a no-op: the mosaic is already in the
        // [0, 1] domain the shader's epsilons assume, which is the domain AstroImageDocument hands
        // both the texture upload and DisplayRasterExport in the real viewer.
        return new Image([mosaic], BitDepth.Float32, maxValue: 1f, minValue: 0f, pedestal: 0f, meta);
    }

    /// <summary>
    /// A scene built to make direction choices observable, sampled through an RGGB CFA.
    /// </summary>
    /// <remarks>
    /// A smooth field is useless here: every demosaic averages symmetrically over one and they all
    /// agree, so a parity test on it passes against a broken shader. Each feature below is present
    /// because it breaks a specific symmetry -- stars for the radial rim where MHC rings and VNG does
    /// not, a hard diagonal for the case where the horizontal and vertical interpolants differ, a
    /// near-Nyquist stripe pattern for where a demosaic either resolves detail or invents colour, and
    /// a little noise so the gradient comparisons are never exactly tied on flat sky.
    /// </remarks>
    private static float[,] BuildMosaic()
    {
        var data = new float[Height, Width];
        var rng = new Random(20260907);

        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                var (r, g, b) = Scene(x, y);
                // RGGB at offset (0, 0): red on even/even, blue on odd/odd, green on the diagonal.
                var v = (y & 1) == 0
                    ? ((x & 1) == 0 ? r : g)
                    : ((x & 1) == 0 ? g : b);
                data[y, x] = Math.Clamp(v + ((float)rng.NextDouble() - 0.5f) * 0.004f, 0f, 1f);
            }
        }

        return data;
    }

    private static (float R, float G, float B) Scene(int x, int y)
    {
        var sky = 0.06f + 0.02f * (x / (float)Width) + 0.01f * (y / (float)Height);
        var r = sky * 1.10f;
        var g = sky;
        var b = sky * 0.85f;

        AddStar(ref r, ref g, ref b, x, y, Width * 0.30f, Height * 0.35f, 1.4f, 0.80f, 0.72f, 0.58f);
        AddStar(ref r, ref g, ref b, x, y, Width * 0.62f, Height * 0.58f, 1.9f, 0.42f, 0.51f, 0.72f);
        AddStar(ref r, ref g, ref b, x, y, Width * 0.45f, Height * 0.78f, 1.1f, 0.25f, 0.25f, 0.25f);

        if (x + y > (Width + Height) * 0.72f)
        {
            r += 0.10f;
            g += 0.06f;
            b += 0.03f;
        }

        if (((x / 2) & 1) == 0 && y > Height * 0.05f && y < Height * 0.20f)
        {
            r += 0.05f;
            g += 0.05f;
            b += 0.05f;
        }

        return (r, g, b);
    }

    private static void AddStar(ref float r, ref float g, ref float b, int x, int y,
        float cx, float cy, float sigma, float ar, float ag, float ab)
    {
        var dx = x - cx;
        var dy = y - cy;
        var falloff = MathF.Exp(-(dx * dx + dy * dy) / (2f * sigma * sigma));
        r += ar * falloff;
        g += ag * falloff;
        b += ab * falloff;
    }
}
