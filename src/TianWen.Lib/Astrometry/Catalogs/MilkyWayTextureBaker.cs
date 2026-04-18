using System;
using System.Buffers.Binary;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Astrometry.Catalogs;

/// <summary>
/// Tunable parameters for baking an equirectangular Milky Way texture. See the
/// field comments in <c>tools/generate_milkyway.cs</c> for physical motivation.
/// Defaults match the "production" bake used at commit time.
/// </summary>
public sealed record MilkyWayBakerOptions(
    int Width = 2048,
    int Height = 1024,
    float MinMagnitude = 8.5f,
    float ExtinctionK = 10.0f,
    float BlurSigma = 1.0f,
    float ColorBlurSigma = 6.0f,
    float ColorSaturation = 2.0f,
    float ColorWarmth = 0.25f,
    float DustReddening = 0.3f,
    float LuminanceMix = 0.85f,
    float BrightnessScale = 0.5f);

/// <summary>
/// Pre-loaded inputs for the baker: full Tycho-2 star list, optional Planck
/// radiance map, optional Planck dust opacity map. Load once, then bake many
/// times with varying <see cref="MilkyWayBakerOptions"/> (e.g. from a GA
/// optimiser) at ~1 s per bake at 2048x1024.
/// </summary>
public sealed class MilkyWayBakerInputs
{
    public int Width { get; }
    public int Height { get; }
    public Tycho2StarLite[] Stars { get; }
    public float[]? Radiance { get; }
    public float[]? DustOpacity { get; }

    private MilkyWayBakerInputs(int width, int height, Tycho2StarLite[] stars, float[]? radiance, float[]? dust)
    {
        Width = width;
        Height = height;
        Stars = stars;
        Radiance = radiance;
        DustOpacity = dust;
    }

    public static async Task<MilkyWayBakerInputs> LoadAsync(
        ICelestialObjectDB db,
        int width,
        int height,
        string? radiancePath,
        string? dustPath,
        CancellationToken ct)
    {
        if (height * 2 != width)
        {
            throw new ArgumentException($"Height ({height}) must equal width ({width}) / 2 for an equirectangular map.");
        }

        // Stream Tycho-2 in 64k batches. Tycho2StarCount is lazy-populated on
        // the first CopyTycho2Stars call, so probing a 1-element span up front
        // materialises the count. After that a single contiguous copy is safe.
        Span<Tycho2StarLite> probe = stackalloc Tycho2StarLite[1];
        db.CopyTycho2Stars(probe);
        var stars = new Tycho2StarLite[db.Tycho2StarCount];
        var written = 0;
        while (written < stars.Length)
        {
            var n = db.CopyTycho2Stars(stars.AsSpan(written), written);
            if (n == 0) break;
            written += n;
            ct.ThrowIfCancellationRequested();
        }
        if (written < stars.Length)
        {
            Array.Resize(ref stars, written);
        }

        var radiance = radiancePath is not null ? LoadFloatEquirectangular(radiancePath, width, height) : null;
        var dust = dustPath is not null ? LoadFloatEquirectangular(dustPath, width, height) : null;

        // Suppress unused-parameter warning for ct; the heavy work is synchronous loads
        // that would need their own token plumbing to be genuinely cancellable.
        await Task.CompletedTask;
        return new MilkyWayBakerInputs(width, height, stars, radiance, dust);
    }

    private static float[] LoadFloatEquirectangular(string path, int expectedW, int expectedH)
    {
        var bytes = File.ReadAllBytes(path);
        var expected = expectedW * expectedH * 4;
        if (bytes.Length != expected)
        {
            throw new InvalidDataException(
                $"Float equirectangular map {path} size {bytes.Length} does not match expected {expected} " +
                $"({expectedW}x{expectedH} float32).");
        }
        var result = new float[expectedW * expectedH];
        Buffer.BlockCopy(bytes, 0, result, 0, bytes.Length);
        return result;
    }
}

/// <summary>
/// Composites a Milky Way equirectangular BGRA texture from real survey data:
/// Tycho-2 photometry binned for colour + density, optional Planck GNILC
/// radiance as the continuous luminance source, optional Planck dust opacity
/// for exp(-k * tau) extinction, and a Planckian-locus colour map from the
/// flux-weighted mean B-V per pixel.
/// <para>
/// Pipeline mirrors <c>tools/generate_milkyway.cs</c>; see that file for the
/// narrative explanation of each step. Split out so the same pipeline can be
/// driven both by the one-shot bake tool and by a GA optimiser that sweeps
/// options against a reference image.
/// </para>
/// </summary>
public static class MilkyWayTextureBaker
{
    /// <summary>
    /// Bake a Milky Way texture into <paramref name="bgra"/> (must be
    /// <c>Width * Height * 4</c> bytes). Allocates working buffers internally.
    /// </summary>
    public static void Bake(MilkyWayBakerInputs inputs, MilkyWayBakerOptions opts, Span<byte> bgra)
    {
        if (opts.Width != inputs.Width || opts.Height != inputs.Height)
        {
            throw new ArgumentException(
                $"Options dimensions ({opts.Width}x{opts.Height}) do not match pre-loaded inputs ({inputs.Width}x{inputs.Height}).");
        }
        var width = opts.Width;
        var height = opts.Height;
        if (bgra.Length != width * height * 4)
        {
            throw new ArgumentException($"bgra span length {bgra.Length} != expected {width * height * 4}.");
        }

        // -------------------------------------------------------------------
        // Bin stars into flux + B-V accumulators. Only stars fainter than
        // MinMagnitude contribute — bright stars are drawn as point sprites
        // by the instanced renderer, and smearing their halos through the
        // colour blur here produces visible warm/cool blobs around e.g. Antares.
        // -------------------------------------------------------------------
        var bvWeightedSum = new float[width * height];
        var bvFluxForColor = new float[width * height];
        var tychoLuminanceFlux = new float[width * height];

        var minMag = opts.MinMagnitude;
        foreach (var star in inputs.Stars)
        {
            if (float.IsNaN(star.VMag)) continue;
            if (star.VMag < minMag) continue;

            var raRad = star.RaHours * (MathF.PI / 12f);
            var decRad = star.DecDeg * (MathF.PI / 180f);
            var raSigned = raRad;
            if (raSigned > MathF.PI) raSigned -= 2f * MathF.PI;
            var u = raSigned / (2f * MathF.PI) + 0.5f;
            var v = 0.5f - decRad / MathF.PI;

            var px = (int)(u * width);
            var py = (int)(v * height);
            if (px < 0) px = 0; else if (px >= width) px = width - 1;
            if (py < 0) py = 0; else if (py >= height) py = height - 1;
            var idx = py * width + px;

            var f = MathF.Exp(-0.921034f * star.VMag);
            tychoLuminanceFlux[idx] += f;
            bvFluxForColor[idx] += f;
            bvWeightedSum[idx] += f * star.BMinusV;
        }

        // -------------------------------------------------------------------
        // Luminance channel: radiance (preferred, continuous) blended with
        // log(Tycho-2) for stellar-density cues, else pure Tycho-2 (needs
        // large blur to avoid salt-and-pepper).
        // -------------------------------------------------------------------
        float[] flux;
        if (inputs.Radiance is not null)
        {
            var radiance = inputs.Radiance;
            var radianceSorted = (float[])radiance.Clone();
            Array.Sort(radianceSorted);
            var rFloor = MathF.Max(radianceSorted[(int)(radianceSorted.Length * 0.01f)], 1e-12f);
            var radianceLog = new float[radiance.Length];
            for (var i = 0; i < radiance.Length; i++)
            {
                radianceLog[i] = MathF.Log(MathF.Max(radiance[i], rFloor) / rFloor);
            }

            if (opts.LuminanceMix >= 1.0f)
            {
                flux = radianceLog;
            }
            else
            {
                // Tycho-2 is a point catalog: each star deposits flux into one
                // pixel, so without heavy spatial smoothing you get per-star
                // stippling near the galactic plane where star density still
                // varies pixel-to-pixel. Radiance is already continuous and
                // doesn't need as much. Floor Tycho-2's blur at ~3 px so low
                // user BlurSigma (e.g. GA-picked 0.30 for sharp LMC rim) no
                // longer exposes the stippling.
                var tychoBlurSigma = MathF.Max(opts.BlurSigma, 3.0f);
                var tychoBlurred = GaussianBlur(tychoLuminanceFlux, width, height, tychoBlurSigma);

                // log1p transform for Tycho-2 flux: empty pixels land at 0
                // (not log(1e-12) = -27) so after normalisation their
                // single-star jitter stays in the linear-tiny regime rather
                // than being amplified by ~10x onto a log scale. Scale is
                // auto-calibrated so the mean-flux pixel maps to log(2).
                var tychoMean = 0f;
                for (var i = 0; i < tychoBlurred.Length; i++) tychoMean += tychoBlurred[i];
                tychoMean /= tychoBlurred.Length;
                var tychoScale = 1f / MathF.Max(tychoMean, 1e-12f);
                var tychoLog = new float[tychoBlurred.Length];
                for (var i = 0; i < tychoBlurred.Length; i++)
                {
                    tychoLog[i] = MathF.Log(1f + tychoBlurred[i] * tychoScale);
                }

                NormaliseInPlace(radianceLog);
                NormaliseInPlace(tychoLog);
                flux = new float[radianceLog.Length];
                var m = opts.LuminanceMix;
                for (var i = 0; i < flux.Length; i++)
                {
                    flux[i] = m * radianceLog[i] + (1f - m) * tychoLog[i];
                }
            }
        }
        else
        {
            flux = tychoLuminanceFlux;
        }

        // -------------------------------------------------------------------
        // Colour channel: blur numerator + denominator separately, then divide.
        // Yields a spatially smooth B-V ratio even where pixels had 0-1 stars.
        // -------------------------------------------------------------------
        var bvFluxSmoothed = GaussianBlur(bvFluxForColor, width, height, opts.ColorBlurSigma);
        var bvSumSmoothed = GaussianBlur(bvWeightedSum, width, height, opts.ColorBlurSigma);
        var bv = new float[width * height];

        // Smooth fallback to neutral (B-V = 0.65) in low-flux regions. The raw
        // ratio bvSum / bvFlux is numerically unstable where bvFlux is near
        // zero (empty off-plane pixels): a lone Tycho-2 star's B-V leaks into
        // an otherwise-empty neighbourhood and, amplified by ColorSaturation,
        // shows up as coloured pepper specks. The fix is a confidence-weighted
        // blend: pixels with plenty of flux see the true flux-weighted B-V,
        // pixels with almost none smoothly blend toward 0.65 (solar-type).
        //
        // FluxConfidenceFloor is calibrated against the mean lit-pixel flux so
        // the threshold scales with how bright the catalog is in this bake.
        var bvFluxMean = 0f;
        for (var i = 0; i < bvFluxSmoothed.Length; i++) bvFluxMean += bvFluxSmoothed[i];
        bvFluxMean /= bvFluxSmoothed.Length;
        // Confidence "reaches 50%" at ~5 % of the all-sky mean flux: any pixel
        // thinner than that slides toward neutral, keeping saturation-amplified
        // noise out of the dark regions.
        var confidenceFloor = MathF.Max(bvFluxMean * 0.05f, 1e-12f);

        for (var i = 0; i < bvFluxSmoothed.Length; i++)
        {
            var f = bvFluxSmoothed[i];
            var confidence = f / (f + confidenceFloor);
            var ratio = f > 1e-12f ? bvSumSmoothed[i] / f : 0.65f;
            bv[i] = confidence * ratio + (1f - confidence) * 0.65f;
        }

        // -------------------------------------------------------------------
        // Blur the luminance channel.
        // -------------------------------------------------------------------
        if (opts.BlurSigma > 0)
        {
            flux = GaussianBlur(flux, width, height, opts.BlurSigma);
        }

        // -------------------------------------------------------------------
        // Dust extinction: multiply flux by exp(-k * tau). Also stash a
        // percentile-normalised tau copy for colour reddening later.
        // -------------------------------------------------------------------
        float[]? dustNormalised = null;
        if (inputs.DustOpacity is not null)
        {
            var dust = inputs.DustOpacity;
            for (var i = 0; i < flux.Length; i++)
            {
                flux[i] *= MathF.Exp(-opts.ExtinctionK * dust[i]);
            }

            var dustSorted = (float[])dust.Clone();
            Array.Sort(dustSorted);
            var dust99 = dustSorted[(int)(dustSorted.Length * 0.99f)];
            dustNormalised = new float[dust.Length];
            if (dust99 > 0)
            {
                var inv = 1f / dust99;
                for (var i = 0; i < dust.Length; i++)
                {
                    dustNormalised[i] = MathF.Min(dust[i] * inv, 1.5f);
                }
            }
        }

        // -------------------------------------------------------------------
        // Normalise flux to a display range. Clip at p25 so three-quarters
        // of the sky goes pitch black, cap at p99.5 so the bulge doesn't blow out.
        // -------------------------------------------------------------------
        var sorted = (float[])flux.Clone();
        Array.Sort(sorted);
        var lowVal = sorted[(int)(sorted.Length * 0.25f)];
        var highVal = sorted[(int)(sorted.Length * 0.995f)];
        var range = highVal - lowVal;
        if (range <= 0) range = 1;

        var bvMean = 0f;
        var bvCount = 0;
        for (var i = 0; i < flux.Length; i++)
        {
            if (flux[i] > lowVal) { bvMean += bv[i]; bvCount++; }
        }
        bvMean = bvCount > 0 ? bvMean / bvCount : 0.65f;

        // -------------------------------------------------------------------
        // Compose BGRA.
        // -------------------------------------------------------------------
        for (var py = 0; py < height; py++)
        {
            for (var px = 0; px < width; px++)
            {
                var idx = py * width + px;
                var normalised = (flux[idx] - lowVal) / range;
                normalised = MathF.Max(0, MathF.Min(1, normalised));
                var brightness = MathF.Sqrt(normalised) * opts.BrightnessScale;

                var dustRedden = dustNormalised is not null ? dustNormalised[idx] * opts.DustReddening : 0f;
                var amplifiedBv = bvMean + (bv[idx] - bvMean) * opts.ColorSaturation + opts.ColorWarmth + dustRedden;
                var (r, g, b) = BVToRgb(amplifiedBv);
                var outIdx = idx * 4;
                bgra[outIdx + 0] = ToByte(b * brightness);
                bgra[outIdx + 1] = ToByte(g * brightness);
                bgra[outIdx + 2] = ToByte(r * brightness);
                bgra[outIdx + 3] = ToByte(brightness);
            }
        }
    }

    /// <summary>
    /// Convenience overload: allocate a fresh BGRA buffer and return it.
    /// </summary>
    public static byte[] Bake(MilkyWayBakerInputs inputs, MilkyWayBakerOptions opts)
    {
        var bgra = new byte[opts.Width * opts.Height * 4];
        Bake(inputs, opts, bgra);
        return bgra;
    }

    /// <summary>
    /// Write <paramref name="bgra"/> with an 8-byte int32 LE width+height header
    /// to the given path. Matches the format the runtime loader in
    /// <c>SkyMapTab.TryLoadMilkyWayTexture</c> expects (sans lzip framing).
    /// </summary>
    public static void WriteRaw(string path, int width, int height, ReadOnlySpan<byte> bgra)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        using var fs = File.Create(path);
        Span<byte> header = stackalloc byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(header, width);
        BinaryPrimitives.WriteInt32LittleEndian(header[4..], height);
        fs.Write(header);
        fs.Write(bgra);
    }

    // -----------------------------------------------------------------------
    // Helpers (internal: used by unit tests if any are added later).
    // -----------------------------------------------------------------------

    internal static float[] GaussianBlur(float[] src, int w, int h, float sigma)
    {
        // Vertical pass is constant-sigma (declination pixels are uniform on the sphere).
        var vRadius = (int)MathF.Ceiling(sigma * 3f);
        var vKernel = BuildGaussian(sigma, vRadius);

        var tmp = new float[src.Length];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var sum = 0f;
                for (var k = -vRadius; k <= vRadius; k++)
                {
                    var sy = y + k;
                    if (sy < 0) sy = 0; else if (sy >= h) sy = h - 1; // pole clamp
                    sum += src[sy * w + x] * vKernel[k + vRadius];
                }
                tmp[y * w + x] = sum;
            }
        }

        // Horizontal pass: sigma scales with 1/cos(dec) so the angular footprint is
        // isotropic on the sphere. Clamp cos(dec) >= 0.01 so polar-cap rows don't
        // degenerate to a full-row convolution.
        var dst = new float[src.Length];
        for (var y = 0; y < h; y++)
        {
            var dec = (Math.PI / 2.0) - ((y + 0.5) / h) * Math.PI;
            var cosDec = Math.Max(0.01, Math.Cos(dec));
            var rowSigma = (float)(sigma / cosDec);
            var rowRadius = Math.Min((int)MathF.Ceiling(rowSigma * 3f), w / 2);
            var rowKernel = BuildGaussian(rowSigma, rowRadius);

            var rowBase = y * w;
            for (var x = 0; x < w; x++)
            {
                var sum = 0f;
                for (var k = -rowRadius; k <= rowRadius; k++)
                {
                    var sx = (x + k + w) % w; // RA wrap
                    sum += tmp[rowBase + sx] * rowKernel[k + rowRadius];
                }
                dst[rowBase + x] = sum;
            }
        }
        return dst;
    }

    internal static void NormaliseInPlace(float[] a)
    {
        var max = 0f;
        for (var i = 0; i < a.Length; i++) if (a[i] > max) max = a[i];
        if (max <= 0) return;
        var inv = 1f / max;
        for (var i = 0; i < a.Length; i++) a[i] *= inv;
    }

    internal static float[] BuildGaussian(float sigma, int radius)
    {
        var kernel = new float[radius * 2 + 1];
        var twoSigmaSq = 2f * sigma * sigma;
        var norm = 0f;
        for (var i = -radius; i <= radius; i++)
        {
            kernel[i + radius] = MathF.Exp(-i * i / twoSigmaSq);
            norm += kernel[i + radius];
        }
        for (var i = 0; i < kernel.Length; i++) kernel[i] /= norm;
        return kernel;
    }

    // B-V colour index to linear RGB via Planckian locus.
    internal static (float R, float G, float B) BVToRgb(float bv)
    {
        bv = MathF.Max(-0.4f, MathF.Min(2.0f, bv));
        var t = 4600f * (1f / (0.92f * bv + 1.7f) + 1f / (0.92f * bv + 0.62f));

        float r, g, b;
        if (t <= 6600)
        {
            r = 1f;
            g = Clamp01(0.39f * MathF.Log(t / 100f) - 0.634f);
        }
        else
        {
            r = Clamp01(1.293f * MathF.Pow(t / 100f - 60f, -0.1332f));
            g = Clamp01(1.129f * MathF.Pow(t / 100f - 60f, -0.0755f));
        }

        if (t >= 6600) b = 1f;
        else if (t <= 1900) b = 0f;
        else b = Clamp01(0.543f * MathF.Log(t / 100f - 10f) - 1.186f);

        return (r, g, b);
    }

    private static float Clamp01(float x) => x < 0 ? 0 : x > 1 ? 1 : x;
    private static byte ToByte(float x) => (byte)MathF.Max(0, MathF.Min(255, x * 255f));
}
