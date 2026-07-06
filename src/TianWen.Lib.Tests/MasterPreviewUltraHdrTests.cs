using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using SharpAstro.Jpeg;
using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Stacking;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Pins the Ultra HDR (gain-map JPEG) export: the highlight-recovery HDR rendition
/// (<see cref="Image.RenderHdrLinearRgb"/>) and the end-to-end assembly through
/// <see cref="MasterPreviewRenderer.RenderAsync"/> (<c>ultraHdrPath</c>).
/// <para>
/// The load-bearing property is that the MTF stretch flattens a bright, fully-structured
/// core to a white plate in SDR (<see cref="Image.RenderStretchedRgba16"/> clamps it to
/// 1.0), and the gain-map HDR rendition RECOVERS that clipped structure -- per pixel,
/// above SDR white -- from the linear master's PRE-MTF signal. So: SDR clips two distinct
/// bright levels to the same white; HDR keeps them apart and above 1.0.
/// </para>
/// </summary>
[Collection("Stacking")]
public class MasterPreviewUltraHdrTests
{
    private const int W = 48;
    private const int H = 48;

    // Sample sites: faint background, dim core block, bright core block. The two core
    // blocks are far above any plausible stretch white point, so both clip in SDR.
    private static readonly (int X, int Y) Bg = (3, 3);
    private static readonly (int X, int Y) CoreDim = (12, 12);
    private static readonly (int X, int Y) CoreBright = (30, 30);

    private static bool InBlock((int X, int Y) c, int x, int y)
        => x >= c.X - 2 && x < c.X + 3 && y >= c.Y - 2 && y < c.Y + 3;

    /// <summary>
    /// Synthetic linear RGB master mimicking a normalized stack (Normalizer maps the
    /// background near 0 and bright cores to several x SDR white): a faint neutral
    /// background with deterministic noise, plus two neutral core blocks at 1.5 and 4.0.
    /// The auto-stretch white point lands at value 1.0 (shadows ~ median, rescale = 1/(1 -
    /// shadows)), so BOTH cores clip to the same white in SDR -- losing the 1.5-vs-4.0
    /// gradient the linear data still holds, which the gain map recovers.
    /// </summary>
    private static Image SyntheticCoreMaster()
    {
        var r = new float[H, W];
        var g = new float[H, W];
        var b = new float[H, W];
        for (var y = 0; y < H; y++)
        {
            for (var x = 0; x < W; x++)
            {
                var n = ((x * 1103515245 + y * 12345) & 0xFFFF) / 65535f * 0.01f; // [0, 0.01)
                float v = 0.02f + n;
                if (InBlock(CoreDim, x, y)) v = 1.5f;
                else if (InBlock(CoreBright, x, y)) v = 4.0f;
                r[y, x] = v;
                g[y, x] = v;
                b[y, x] = v;
            }
        }
        var meta = new ImageMeta("synth", DateTime.UnixEpoch, TimeSpan.FromSeconds(60),
            FrameType.Light, "", 3.76f, 3.76f, 500, -1, Filter.Luminance, 1, 1,
            float.NaN, SensorType.Color, 0, 0, RowOrder.TopDown, float.NaN, float.NaN);
        return new Image([r, g, b], BitDepth.Float32, 1.0f, 0f, 0f, meta);
    }

    private static async Task<StretchUniforms> SolveUniformsAsync(Image img, CancellationToken ct)
    {
        var renderer = new MasterPreviewRenderer(catalogDb: null, NullLogger.Instance);
        // Empty output + neutral WB override: solve only, no SPCC / catalog / file needed.
        var render = await renderer.RenderAsync(
            img, img.ImageMeta, wcs: null, statsSource: null, outputPath: string.Empty,
            whiteBalanceOverride: (1f, 1f, 1f), ct: ct);
        return render.Uniforms;
    }

    private static float R(ushort[] rgba, (int X, int Y) p) => rgba[(p.Y * W + p.X) * 4];
    private static float HdrR(float[] hdr, (int X, int Y) p) => hdr[(p.Y * W + p.X) * 3];

    [Fact]
    public async Task RenderHdrLinearRgb_recovers_clipped_core_structure_above_sdr_white()
    {
        var img = SyntheticCoreMaster();
        var uniforms = await SolveUniformsAsync(img, TestContext.Current.CancellationToken);

        var rgba = new ushort[W * H * 4];
        img.RenderStretchedRgba16(uniforms, rgba);

        const float headroom = 5f;
        var hdr = new float[W * H * 3];
        img.RenderHdrLinearRgb(uniforms, rgba, hdr, headroom);

        // SDR flattened BOTH bright blocks to the same white plate -- the structure is gone.
        R(rgba, CoreDim).ShouldBe(65535f, "dim core should clip white in SDR");
        R(rgba, CoreBright).ShouldBe(65535f, "bright core should clip white in SDR");

        // HDR recovered the gradient: both cores sit ABOVE SDR white (1.0) and the brighter
        // one is brighter -- the flat SDR plate is a monotone gradient again.
        var hdrDim = HdrR(hdr, CoreDim);
        var hdrBright = HdrR(hdr, CoreBright);
        hdrDim.ShouldBeGreaterThan(1f, "dim core must be recovered above SDR white");
        hdrBright.ShouldBeGreaterThan(hdrDim, "brighter core must stay brighter (structure recovered)");

        // Rolled off within the display headroom (never a runaway peak no display reaches).
        hdrBright.ShouldBeLessThanOrEqualTo(headroom);

        // The faint background is NOT pushed into HDR -- gain is exactly 1 there, so the HDR
        // rendition equals the linearized SDR base and SDR + HDR viewers agree everywhere the
        // stretch did not clip.
        HdrR(hdr, Bg).ShouldBeLessThan(1f, "background must stay in the SDR range (no spurious boost)");
        HdrR(hdr, Bg).ShouldBe(Bt2020Pq.SrgbEotf(R(rgba, Bg) / 65535f), 1e-4f);
    }

    [Fact]
    public async Task RenderAsync_writes_a_readable_gainmap_jpeg_with_real_headroom()
    {
        var img = SyntheticCoreMaster();
        var renderer = new MasterPreviewRenderer(catalogDb: null, NullLogger.Instance);
        var jpgPath = Path.Combine(Path.GetTempPath(), $"tianwen_uhdr_{Guid.NewGuid():N}.jpg");
        try
        {
            // outputPath empty -> no PNG; ultraHdrPath set -> emit only the gain-map JPEG.
            await renderer.RenderAsync(
                img, img.ImageMeta, wcs: null, statsSource: null, outputPath: string.Empty,
                whiteBalanceOverride: (1f, 1f, 1f), ultraHdrPath: jpgPath,
                ct: TestContext.Current.CancellationToken);

            File.Exists(jpgPath).ShouldBeTrue();
            var bytes = await File.ReadAllBytesAsync(jpgPath, TestContext.Current.CancellationToken);

            // Round-trips through the Android Ultra HDR / hdrgm reader.
            JpegGainMap.TryRead(bytes, out var gm).ShouldBeTrue("output must parse as a gain-map JPEG");
            gm.ShouldNotBeNull();
            gm.Base.Width.ShouldBe(W);
            gm.Base.Height.ShouldBe(H);

            // The fitted gain map carries REAL recovered headroom -- a flat/no-op render would
            // land at the format's ~1.0625 floor. A clipped bright core pushes it well past that.
            gm.Metadata.HdrCapacityMax.ShouldBeGreaterThan(1.5,
                "clipped core should fit substantial HDR headroom");

            // At full display headroom the reconstructed core exceeds SDR white (1.0 linear).
            var recon = gm.ReconstructHdr(displayHeadroom: gm.Metadata.HdrCapacityMax);
            var reconR = ReconLinearR(recon, CoreBright.X, CoreBright.Y);
            reconR.ShouldBeGreaterThan(1f, "reconstructed core must exceed SDR white");
        }
        finally
        {
            if (File.Exists(jpgPath)) File.Delete(jpgPath);
        }
    }

    // Reconstructed HDR is Float32, 3-channel, interleaved row-major (RasterImage.Pixels is bytes).
    private static float ReconLinearR(SharpAstro.Codecs.Abstractions.RasterImage recon, int x, int y)
    {
        var floats = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(recon.Pixels);
        return floats[(y * recon.Width + x) * 3];
    }
}
