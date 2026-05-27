using System;
using System.Runtime.CompilerServices;

namespace TianWen.Lib.Imaging;

/// <summary>
/// HDR10 PQ encoding helpers: convert a 16-bit sRGB display-referred RGBA
/// buffer (the output of <see cref="Image.RenderStretchedRgba16"/>) into a
/// BT.2020 + SMPTE ST 2084 (PQ) encoded buffer suitable for tagging with
/// <c>cICP {9, 16, 0, 1}</c> (HDR10) in a PNG-3 file.
///
/// <para>The pipeline per pixel:</para>
/// <list type="number">
///   <item>Treat the input value as a perceptual sRGB code value and apply
///   the sRGB EOTF to recover linear sRGB.</item>
///   <item>Convert primaries from sRGB to BT.2020 via the ITU-R BT.2087-0
///   matrix (D65 white point).</item>
///   <item>Scale the linear [0, 1] range to <c>peakNits / 10000</c> so that
///   <c>1.0</c> stretched maps to <paramref name="peakNits"/> nits on the
///   HDR display (PQ's reference is 10000 nits = 1.0 input).</item>
///   <item>Apply the PQ OETF (SMPTE ST 2084) to obtain a [0, 1] PQ-coded
///   value.</item>
///   <item>Quantise to 16-bit for the PNG sample.</item>
/// </list>
/// </summary>
public static class Bt2020Pq
{
    /// <summary>
    /// In-place conversion of a 16-bit RGBA buffer from sRGB display-referred
    /// (as produced by <see cref="Image.RenderStretchedRgba16"/>) to BT.2020 +
    /// PQ encoding suitable for an HDR10 PNG. Alpha channel is left untouched
    /// at full opacity.
    /// </summary>
    /// <param name="rgba64">RGBA buffer in host byte order; length must be
    /// a multiple of 4. Modified in place.</param>
    /// <param name="peakNits">Display luminance assigned to stretched value
    /// <c>1.0</c>. Cinema HDR10 grades typically use 1000; ITU-R BT.2408
    /// reference white is 203; extreme grades target 4000. Default 1000.</param>
    public static void EncodeInPlace(Span<ushort> rgba64, float peakNits = 1000f, bool gamutToBt2020 = true)
    {
        if (rgba64.Length % 4 != 0)
            throw new ArgumentException("Buffer length must be a multiple of 4 (RGBA samples)", nameof(rgba64));
        ValidatePeakNits(peakNits);

        var nitsScale = peakNits / 10000f;
        var pixelCount = rgba64.Length / 4;
        for (var i = 0; i < pixelCount; i++)
        {
            var o = i * 4;
            var rPerc = rgba64[o + 0] / 65535f;
            var gPerc = rgba64[o + 1] / 65535f;
            var bPerc = rgba64[o + 2] / 65535f;

            EncodePixel(rPerc, gPerc, bPerc, nitsScale, gamutToBt2020, out var rPq, out var gPq, out var bPq);

            rgba64[o + 0] = (ushort)(Math.Clamp(rPq, 0f, 1f) * 65535f + 0.5f);
            rgba64[o + 1] = (ushort)(Math.Clamp(gPq, 0f, 1f) * 65535f + 0.5f);
            rgba64[o + 2] = (ushort)(Math.Clamp(bPq, 0f, 1f) * 65535f + 0.5f);
            // Alpha stays at 65535
        }
    }

    /// <summary>
    /// Float-sample variant for the TIFF dual-stretch companion writer.
    /// Input is RGB-interleaved <see cref="Span{Single}"/> in
    /// <c>[0, 1]</c> sRGB display-referred space (what
    /// <c>WriteStretchedFloatTiffAsync</c> assembles from the per-channel
    /// plate spans). After this call the same buffer holds <c>[0, 1]</c>
    /// PQ-coded floats in BT.2020 primaries, ready to be written into a
    /// 32-bit IEEE-float TIFF tagged with cICP HDR10.
    /// </summary>
    /// <param name="rgbInterleaved">Buffer of R, G, B floats interleaved
    /// (length = pixelCount × 3). Modified in place.</param>
    /// <param name="peakNits">Same semantics as the ushort overload.</param>
    public static void EncodeInPlace(Span<float> rgbInterleaved, float peakNits = 1000f, bool gamutToBt2020 = true)
    {
        if (rgbInterleaved.Length % 3 != 0)
            throw new ArgumentException("Buffer length must be a multiple of 3 (RGB samples)", nameof(rgbInterleaved));
        ValidatePeakNits(peakNits);

        var nitsScale = peakNits / 10000f;
        var pixelCount = rgbInterleaved.Length / 3;
        for (var i = 0; i < pixelCount; i++)
        {
            var o = i * 3;
            EncodePixel(rgbInterleaved[o + 0], rgbInterleaved[o + 1], rgbInterleaved[o + 2],
                nitsScale, gamutToBt2020, out var rPq, out var gPq, out var bPq);
            // Keep float precision; clamp negatives but allow super-1.0
            // outputs through. The float TIFF reader is HDR-aware (cicp
            // declares "PQ"), so >1.0 floats are interpreted via the PQ
            // EOTF -- a value like 1.05 in the file means "slightly above
            // PQ peak code, decoder will clip as it normally does."
            rgbInterleaved[o + 0] = MathF.Max(rPq, 0f);
            rgbInterleaved[o + 1] = MathF.Max(gPq, 0f);
            rgbInterleaved[o + 2] = MathF.Max(bPq, 0f);
        }
    }

    private static void ValidatePeakNits(float peakNits)
    {
        if (peakNits is <= 0f or > 10000f)
            throw new ArgumentOutOfRangeException(nameof(peakNits), "peakNits must be in (0, 10000]");
    }

    /// <summary>
    /// Per-pixel core: takes a perceptual-sRGB <c>(r, g, b)</c> in
    /// <c>[0, 1]</c> and returns the PQ-coded <c>(r, g, b)</c> in
    /// <c>[0, 1]</c>. Both the ushort and float in-place encoders dispatch
    /// to this so the math has exactly one source of truth.
    ///
    /// <para>When <paramref name="gamutToBt2020"/> is <c>true</c> the
    /// linear-sRGB intermediate is converted to BT.2020 primaries (canonical
    /// HDR10). When <c>false</c> the gamut conversion is skipped -- the
    /// caller is signalling cICP <c>{BT.709 primaries, PQ transfer}</c>
    /// ("narrow-gamut HDR"), so the PQ encoding stays in sRGB-primary space
    /// and avoids the desaturation that consumer HDR pipelines sometimes
    /// produce when they don't apply the inverse BT.2020-to-display
    /// gamut matrix on output.</para>
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void EncodePixel(float rPerc, float gPerc, float bPerc, float nitsScale, bool gamutToBt2020,
        out float rPq, out float gPq, out float bPq)
    {
        // sRGB EOTF: perceptual -> linear sRGB
        var rLin = SrgbEotf(rPerc);
        var gLin = SrgbEotf(gPerc);
        var bLin = SrgbEotf(bPerc);

        float rOut, gOut, bOut;
        if (gamutToBt2020)
        {
            // sRGB primaries -> BT.2020 primaries (ITU-R BT.2087-0, D65 white).
            // Both spaces share the same D65 reference white so this is a
            // straight 3x3 chromaticity-only matrix; no white-point adapt.
            rOut = 0.6274f * rLin + 0.3293f * gLin + 0.0433f * bLin;
            gOut = 0.0691f * rLin + 0.9195f * gLin + 0.0114f * bLin;
            bOut = 0.0164f * rLin + 0.0880f * gLin + 0.8956f * bLin;
        }
        else
        {
            // Stay in sRGB primaries; cICP will say {BT.709, PQ, 0, 1}.
            rOut = rLin;
            gOut = gLin;
            bOut = bLin;
        }

        // Scale linear values to PQ's [0, 1] = [0, 10000 nits] domain.
        // 1.0 linear post-EOTF (= sRGB white) maps to peakNits.
        rOut *= nitsScale;
        gOut *= nitsScale;
        bOut *= nitsScale;

        // PQ OETF -> [0, 1] code value
        rPq = PqOetf(rOut);
        gPq = PqOetf(gOut);
        bPq = PqOetf(bOut);
    }

    /// <summary>
    /// sRGB EOTF -- maps a perceptual sRGB code value in [0, 1] to a linear
    /// sRGB intensity in [0, 1]. Piecewise linear/gamma per IEC 61966-2-1.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float SrgbEotf(float v)
        => v <= 0.04045f
            ? v / 12.92f
            : MathF.Pow((v + 0.055f) / 1.055f, 2.4f);

    /// <summary>
    /// SMPTE ST 2084 (PQ) OETF -- maps a linear luminance value in [0, 1]
    /// (where 1.0 = 10000 nits) to a PQ-encoded code value in [0, 1].
    /// Constants from SMPTE ST 2084-2014 Annex.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float PqOetf(float l)
    {
        if (l <= 0f) return 0f;
        const float m1 = 0.1593017578125f;   // 2610/16384
        const float m2 = 78.84375f;          // (2523/4096) * 128
        const float c1 = 0.8359375f;         // 3424/4096
        const float c2 = 18.8515625f;        // (2413/4096) * 32
        const float c3 = 18.6875f;           // (2392/4096) * 32
        var lm1 = MathF.Pow(l, m1);
        return MathF.Pow((c1 + c2 * lm1) / (1f + c3 * lm1), m2);
    }
}
