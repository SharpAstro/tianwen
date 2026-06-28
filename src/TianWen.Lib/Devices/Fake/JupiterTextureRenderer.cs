using System;
using System.IO;
using System.IO.Compression;

namespace TianWen.Lib.Devices.Fake;

/// <summary>
/// Renders the fake planetary video frame from a REAL Jupiter image (NASA/ESA Hubble OPAL 2024, embedded as
/// a gzipped raw RGB blob) instead of the procedural disk in <see cref="SyntheticPlanetRenderer"/>. The
/// caller supplies the disk centre + <b>equatorial radius in pixels</b> -- derived from the telescope's
/// pixel scale and Jupiter's apparent diameter -- so the planet appears at the physically-correct size for
/// the simulated optics. Jupiter's geometric oblateness (polar = 0.935 x equatorial) is applied as a
/// vertical squash, and a per-frame seeing blur + shot/read noise (shared with
/// <see cref="SyntheticPlanetRenderer"/>) model the atmosphere + sensor so the lucky-imaging grader has
/// sharp/soft frames to rank.
/// <para>
/// <b>Phase 1</b> of the image-based simulation: texture load + angular scaling + drift. The diffraction /
/// defocus PSF and per-frame turbulence land in later phases. Two output modes share the geometry +
/// seeing + noise: <see cref="Render"/> emits mono luminance (Rec.709) for a mono sensor; <see cref="RenderBayer"/>
/// emits a raw RGGB Bayer mosaic for a colour sensor, so the live stack debayers to a colour Jupiter and the
/// wavelet deblur runs on real colour data.
/// </para>
/// </summary>
internal static class JupiterTextureRenderer
{
    private const string ResourceSuffix = "jupiter.rgb.gz";

    // Decoded once (thread-safe via Lazy); the texture is immutable after load.
    private static readonly Lazy<JupiterTexture> _texture = new(LoadTexture);

    /// <summary>Jupiter's geometric oblateness (polar radius / equatorial radius).</summary>
    public const double Oblateness = 0.935;

    /// <summary>Default effective full-well in electrons (electron count at full ADU). Low = high gain =
    /// grainy single frames. Calibrated against a real 8-bit planetary SER: a disk at ~40% scale showed
    /// ~8% per-frame grain (~170 e-), i.e. a ~340 e- full-well at the disk's exposure level.</summary>
    public const double DefaultFullWellElectrons = 340.0;

    /// <summary>Default read noise in electrons (Gaussian), from the same SER's sky frame-difference.</summary>
    public const double DefaultReadNoiseElectrons = 1.3;

    /// <summary>
    /// Renders Jupiter into a <c>float[height, width]</c> array in ADU. <paramref name="equatorialRadius"/>
    /// is the disk's equatorial radius in pixels; the polar radius is <see cref="Oblateness"/> x that.
    /// </summary>
    public static float[,] Render(
        int width,
        int height,
        double centerX,
        double centerY,
        double equatorialRadius,
        double blurSigma = 0.6,
        double maxAdu = 65535.0,
        double bodyLevel = 0.85,
        double skyBackground = 300.0,
        double fullWellElectrons = DefaultFullWellElectrons,
        double readNoiseElectrons = DefaultReadNoiseElectrons,
        int noiseSeed = 0,
        float[,]? dest = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(equatorialRadius);

        var tex = _texture.Value;
        var peak = maxAdu * bodyLevel;
        var polarRadius = equatorialRadius * Oblateness;

        // 1) Sharp body: sample the texture across the (oblate) disk; outside the ellipse is left at 0 (sky --
        // sky background + noise are added in the shared compose pass). nx/ny are disk-local coords in [-1, 1].
        var sharp = new float[height, width];
        for (var y = 0; y < height; y++)
        {
            var ny = (y - centerY) / polarRadius;
            if (ny is < -1.0 or > 1.0)
            {
                continue;
            }

            var ty = ((ny * 0.5) + 0.5) * (tex.Height - 1);
            var ny2 = ny * ny;
            for (var x = 0; x < width; x++)
            {
                var nx = (x - centerX) / equatorialRadius;
                if ((nx * nx) + ny2 > 1.0)
                {
                    continue; // outside the disk -> sky
                }

                var tx = ((nx * 0.5) + 0.5) * (tex.Width - 1);
                sharp[y, x] = (float)(peak * tex.SampleLuminance(tx, ty));
            }
        }

        // 2) Seeing blur + 3) sky/noise compose -- shared with the procedural renderer so both noise identically.
        var body = blurSigma > 0.35 ? SyntheticPlanetRenderer.GaussianBlur(sharp, width, height, blurSigma) : sharp;
        return SyntheticPlanetRenderer.ComposeWithNoise(body, width, height, maxAdu, skyBackground,
            fullWellElectrons, readNoiseElectrons, noiseSeed, dest);
    }

    /// <summary>
    /// Renders Jupiter as a single-channel <b>RGGB Bayer mosaic</b> in ADU (the raw colour-sensor frame a
    /// planetary camera delivers): each pixel keeps only its CFA channel. The full-colour optical image is
    /// blurred by the seeing PSF <i>before</i> the CFA samples it (per-channel blur, then mosaic) -- blurring
    /// the mosaic itself would cross-contaminate adjacent R/G/B photosites and produce false colour fringes,
    /// so the order matters for getting the downstream demosaic + deblur right. <paramref name="bayerOffsetX"/>
    /// / <paramref name="bayerOffsetY"/> place red on the 2x2 tile (the codebase parity convention: red sits
    /// where both offsets are even). The result feeds <see cref="Imaging.Image.SplitBayerChannels"/> /
    /// <see cref="Imaging.Image.DebayerAsync"/> exactly like a real RGGB frame.
    /// </summary>
    public static float[,] RenderBayer(
        int width,
        int height,
        double centerX,
        double centerY,
        double equatorialRadius,
        int bayerOffsetX = 0,
        int bayerOffsetY = 0,
        double blurSigma = 0.6,
        double maxAdu = 65535.0,
        double bodyLevel = 0.85,
        double skyBackground = 300.0,
        double fullWellElectrons = DefaultFullWellElectrons,
        double readNoiseElectrons = DefaultReadNoiseElectrons,
        int noiseSeed = 0,
        float[,]? dest = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(equatorialRadius);

        var tex = _texture.Value;
        var peak = maxAdu * bodyLevel;
        var polarRadius = equatorialRadius * Oblateness;

        // 1) Sharp full-colour body: sample R/G/B from the texture across the (oblate) disk into three
        // planes. Outside the ellipse stays 0 (sky -- added in the shared compose pass).
        var sharpR = new float[height, width];
        var sharpG = new float[height, width];
        var sharpB = new float[height, width];
        for (var y = 0; y < height; y++)
        {
            var ny = (y - centerY) / polarRadius;
            if (ny is < -1.0 or > 1.0)
            {
                continue;
            }

            var ty = ((ny * 0.5) + 0.5) * (tex.Height - 1);
            var ny2 = ny * ny;
            for (var x = 0; x < width; x++)
            {
                var nx = (x - centerX) / equatorialRadius;
                if ((nx * nx) + ny2 > 1.0)
                {
                    continue; // outside the disk -> sky
                }

                var tx = ((nx * 0.5) + 0.5) * (tex.Width - 1);
                var (r, g, b) = tex.SampleRgb(tx, ty);
                sharpR[y, x] = (float)(peak * r);
                sharpG[y, x] = (float)(peak * g);
                sharpB[y, x] = (float)(peak * b);
            }
        }

        // 2) Per-channel seeing blur: the optical image is blurred BEFORE the CFA samples it.
        if (blurSigma > 0.35)
        {
            sharpR = SyntheticPlanetRenderer.GaussianBlur(sharpR, width, height, blurSigma);
            sharpG = SyntheticPlanetRenderer.GaussianBlur(sharpG, width, height, blurSigma);
            sharpB = SyntheticPlanetRenderer.GaussianBlur(sharpB, width, height, blurSigma);
        }

        // 3) Mosaic: each pixel keeps only its CFA channel (RGGB; 0=R, 1=G, 2=B).
        var mosaic = new float[height, width];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                mosaic[y, x] = BayerChannel(x, y, bayerOffsetX, bayerOffsetY) switch
                {
                    0 => sharpR[y, x],
                    2 => sharpB[y, x],
                    _ => sharpG[y, x],
                };
            }
        }

        // 4) Sky + shot/read noise on the mosaic -- shared with the procedural + mono paths.
        return SyntheticPlanetRenderer.ComposeWithNoise(mosaic, width, height, maxAdu, skyBackground,
            fullWellElectrons, readNoiseElectrons, noiseSeed, dest);
    }

    // RGGB Bayer pattern [R, G1] / [G2, B]; channel 0=R, 1=G, 2=B. Matches the convention in
    // SyntheticStarFieldRenderer.RenderBayer + the codebase's red-at-even-offset parity.
    private static int BayerChannel(int x, int y, int offsetX, int offsetY)
    {
        var bx = (x + offsetX) & 1;
        var by = (y + offsetY) & 1;
        if (bx == 0 && by == 0)
        {
            return 0; // R
        }
        if (bx == 1 && by == 1)
        {
            return 2; // B
        }
        return 1; // G (both G1 and G2)
    }

    private static JupiterTexture LoadTexture()
    {
        var assembly = typeof(JupiterTextureRenderer).Assembly;
        var manifest = Array.Find(assembly.GetManifestResourceNames(), n => n.EndsWith(ResourceSuffix, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"Embedded Jupiter texture '{ResourceSuffix}' not found.");
        using var rawStream = assembly.GetManifestResourceStream(manifest)
            ?? throw new InvalidOperationException($"GetManifestResourceStream returned null for {manifest}");
        using var gz = new GZipStream(rawStream, CompressionMode.Decompress);
        using var buf = new MemoryStream();
        gz.CopyTo(buf);
        var bytes = buf.ToArray();

        // 8-byte little-endian header (width, height) then row-major RGB (3 bytes/pixel).
        var width = BitConverter.ToInt32(bytes, 0);
        var height = BitConverter.ToInt32(bytes, 4);
        var rgb = new byte[width * height * 3];
        Array.Copy(bytes, 8, rgb, 0, rgb.Length);
        return new JupiterTexture(width, height, rgb);
    }

    // Immutable decoded texture with bilinear sampling (per-channel RGB or Rec.709 luminance), normalised
    // to [0, 1]. The bilinear corner/weight math lives in one place (SampleRgb); SampleLuminance is the
    // weighted reduction of it, so the mono and colour paths sample identically.
    private sealed class JupiterTexture(int width, int height, byte[] rgb)
    {
        public int Width => width;
        public int Height => height;

        public double SampleLuminance(double fx, double fy)
        {
            var (r, g, b) = SampleRgb(fx, fy);
            return (0.2126 * r) + (0.7152 * g) + (0.0722 * b);
        }

        public (double R, double G, double B) SampleRgb(double fx, double fy)
        {
            var x0 = (int)Math.Floor(fx);
            var y0 = (int)Math.Floor(fy);
            var wx = fx - x0;
            var wy = fy - y0;
            var x1 = Math.Clamp(x0 + 1, 0, width - 1);
            var y1 = Math.Clamp(y0 + 1, 0, height - 1);
            x0 = Math.Clamp(x0, 0, width - 1);
            y0 = Math.Clamp(y0, 0, height - 1);

            return (
                Bilerp(x0, y0, x1, y1, wx, wy, 0),
                Bilerp(x0, y0, x1, y1, wx, wy, 1),
                Bilerp(x0, y0, x1, y1, wx, wy, 2));
        }

        private double Bilerp(int x0, int y0, int x1, int y1, double wx, double wy, int channel)
        {
            var c00 = Chan(x0, y0, channel);
            var c10 = Chan(x1, y0, channel);
            var c01 = Chan(x0, y1, channel);
            var c11 = Chan(x1, y1, channel);
            var top = c00 + ((c10 - c00) * wx);
            var bot = c01 + ((c11 - c01) * wx);
            return top + ((bot - top) * wy);
        }

        private double Chan(int x, int y, int channel) => rgb[(((y * width) + x) * 3) + channel] / 255.0;
    }
}
