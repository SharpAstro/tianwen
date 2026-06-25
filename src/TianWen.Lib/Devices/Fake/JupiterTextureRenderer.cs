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
/// defocus PSF and per-frame turbulence land in later phases. Output is mono luminance (Rec.709) for now,
/// matching the fake's existing 1-channel video path; a colour (Bayer) mode is a follow-up.
/// </para>
/// </summary>
internal static class JupiterTextureRenderer
{
    private const string ResourceSuffix = "jupiter.rgb.gz";

    // Decoded once (thread-safe via Lazy); the texture is immutable after load.
    private static readonly Lazy<JupiterTexture> _texture = new(LoadTexture);

    /// <summary>Jupiter's geometric oblateness (polar radius / equatorial radius).</summary>
    public const double Oblateness = 0.935;

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
        double readNoise = 8.0,
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
        return SyntheticPlanetRenderer.ComposeWithNoise(body, width, height, maxAdu, skyBackground, readNoise, noiseSeed, dest);
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

    // Immutable decoded texture with bilinear luminance sampling (Rec.709 weights, normalised to [0, 1]).
    private sealed class JupiterTexture(int width, int height, byte[] rgb)
    {
        public int Width => width;
        public int Height => height;

        public double SampleLuminance(double fx, double fy)
        {
            var x0 = (int)Math.Floor(fx);
            var y0 = (int)Math.Floor(fy);
            var wx = fx - x0;
            var wy = fy - y0;
            var x1 = Math.Clamp(x0 + 1, 0, width - 1);
            var y1 = Math.Clamp(y0 + 1, 0, height - 1);
            x0 = Math.Clamp(x0, 0, width - 1);
            y0 = Math.Clamp(y0, 0, height - 1);

            var l00 = Lum(x0, y0);
            var l10 = Lum(x1, y0);
            var l01 = Lum(x0, y1);
            var l11 = Lum(x1, y1);
            var top = l00 + ((l10 - l00) * wx);
            var bot = l01 + ((l11 - l01) * wx);
            return top + ((bot - top) * wy);
        }

        private double Lum(int x, int y)
        {
            var i = ((y * width) + x) * 3;
            return ((0.2126 * rgb[i]) + (0.7152 * rgb[i + 1]) + (0.0722 * rgb[i + 2])) / 255.0;
        }
    }
}
