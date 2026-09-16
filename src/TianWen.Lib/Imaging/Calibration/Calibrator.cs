using System;
using System.Runtime.InteropServices;

namespace TianWen.Lib.Imaging.Calibration;

/// <summary>
/// Applies bias / dark / flat master frames to a light frame. Each master is
/// optional: pass <c>null</c> for any step the caller doesn't want applied.
/// <para>
/// Formula: <c>calibrated = flat &gt; epsilon ? max(light - bias - dark + pedestal, 0) / flat : NaN</c>.
/// The pedestal is added on the dark subtraction (the deeper of the two; bias
/// subtraction alone rarely needs an offset). A flat pixel at or below
/// <see cref="FlatEpsilon"/> carries no calibration, so the output is absent.
/// </para>
/// <para>
/// Two entry points for different consumers:
/// </para>
/// <list type="bullet">
/// <item>
/// <see cref="Apply"/>: whole-frame, returns a new <see cref="Image"/>. Used
/// by master-flat verification tests and one-off light calibration. Chains
/// <see cref="Image.Subtract"/> + <see cref="Image.Divide"/> from Phase 1.
/// </item>
/// <item>
/// <see cref="ApplyTile"/>: single-channel, region-based, span output. Used by
/// the Phase 8 tile-pipelined integrator so a full calibrated <see cref="Image"/>
/// never materialises. Reads the corresponding region of each master directly
/// from the held <c>float[,]</c> backing arrays: zero copy.
/// </item>
/// </list>
/// </summary>
/// <param name="Bias">Master bias frame, or <c>null</c> to skip bias subtraction.</param>
/// <param name="Dark">Master dark frame, or <c>null</c> to skip dark subtraction.</param>
/// <param name="Flat">Master flat frame (median ~ 1.0), or <c>null</c> to skip
/// flat division.</param>
/// <param name="Pedestal">ADU offset added per pixel before the non-negative
/// clamp. SetiAstro's <c>subtract_dark_with_pedestal</c> trick; prevents the
/// clamp from zeroing out background pixels when the dark mean exceeds the
/// light's measured background. Suggested 100-1000 for raw ADU data, or
/// 0.001-0.01 for normalised [0, 1] float data. Default 0 (no offset).</param>
/// <param name="FlatEpsilon">The throughput below which a flat pixel carries no calibration, as a
/// fraction of the flat's mean (<see cref="MasterFrameBuilder.BuildFlatMasterAsync"/> normalises every
/// frame to mean 1 before combining, so the stored value IS the relative throughput). Such a pixel is
/// marked ABSENT, <see cref="float.NaN"/>, not divided. Default <b>0.02</b>.
/// <para><b>It used to be 1e-6 and it used to CLAMP, and that pair is the bug it now fixes.</b> Dividing
/// by <c>max(flat, 1e-6)</c> does not prevent a division by zero, it turns one into a multiplication by
/// a million and hands back a finite number no consumer can tell from data. Found in the wild on
/// `Eta Car SII NB / QHYCCD / 2024-03-02`, whose overscan columns are exactly 0 in the flat: the master
/// peaks at 2.46e8 against a sky of 213, and 245,827,712 x 1e-6 = 245.83 is precisely the ADU value that
/// went in. NaN is what the rest of the pipeline already understands, so absence travels correctly where
/// that number travelled silently.</para>
/// <para><b>0.02 is measured, not chosen.</b> Over the 18 master flats of the `2026-09-12-clamped` bake:
/// 15 carry no pixel at all below 0.3 of the mean, and the three that do (6,770 exactly-zero pixels in
/// total) have populations that are complete by 0.02 and add nothing between 0.02 and 0.3. So the dead
/// population and the shallowest real optical attenuation are separated by an empty band 15x wide, and
/// any threshold inside it selects the same pixels. 0.02 sits at the bottom of that band, which leaves
/// the margin on the side that matters: a genuinely vignetted corner is never mistaken for a dead one.
/// Re-measure before raising it, since a faster optic vignettes deeper.</para></param>
/// <param name="DarkScale">Multiplier on the dark's THERMAL component, for a dark whose exposure
/// does not match the light's. Dark current accumulates linearly with time, so the physically
/// correct factor is <c>t_light / t_dark</c> and nothing needs fitting. 1.0 (the default) is an
/// exact no-op and leaves every existing caller byte-identical.
/// <para>Scaling requires <paramref name="DarkBias"/>, because only the thermal part scales: a
/// master dark built from RAW darks carries the sensor's electronic offset baked in, and that
/// offset is a property of the readout, not of exposure time. Multiplying the whole dark would
/// scale the pedestal too and wreck the background.</para>
/// <para><b>Measured motivation.</b> 47 of the 64 sessions in the reference dataset were calibrated
/// with a dark that did not match their lights, most of them 60s lights against a 120s dark, and
/// the sub-PSF residue in the stacked masters concentrated in exactly those sessions.</para></param>
/// <param name="DarkBias">Master bias belonging to the DARK, used ONLY to split it into offset and
/// thermal parts for <paramref name="DarkScale"/>. It is never subtracted from the light on its own:
/// the scaled dark still carries the offset, so the existing "do not pass Bias alongside a raw
/// master dark" invariant is preserved rather than bypassed. Ignored when
/// <paramref name="DarkScale"/> is 1.</param>
public sealed record Calibrator(
    Image? Bias = null,
    Image? Dark = null,
    Image? Flat = null,
    float Pedestal = 0f,
    float FlatEpsilon = 0.02f,
    float DarkScale = 1f,
    Image? DarkBias = null)
{
    /// <summary>
    /// Which masters these frames were actually calibrated with, as the resolver's own group slugs,
    /// or null when nobody recorded it.
    /// </summary>
    /// <remarks>
    /// <para><b>An image cannot say what calibrated it, and until this existed nothing else could
    /// either.</b> When <see cref="FlatEpsilon"/> changed what a dead flat pixel does, working out
    /// which stacked masters were affected needed the question "which session used which flat", and
    /// the answer was recorded in no bake artefact, no manifest and no log. It could only be had by
    /// re-running the whole bake, which is an hour to learn something the run already knew and threw
    /// away.</para>
    /// <para>Carried here rather than returned separately because this object IS what the resolver
    /// hands the caller, so there is no signature anywhere to keep in step, and a consumer that does
    /// not care never sees it. The value is the master's group slug, the same string the cache names
    /// its file with, so it joins straight to <c>masters/</c> on disk.</para>
    /// </remarks>
    public CalibrationProvenance? Provenance { get; init; }

    /// <summary>Below this the scale is treated as exactly 1 and the dark is used verbatim, so a
    /// caller computing a ratio that lands a hair off 1.0 does not silently take the scaling path
    /// and require a bias it has no reason to supply.</summary>
    private const float ScaleEpsilon = 1e-4f;

    /// <summary>Whether the thermal component is actually being rescaled.</summary>
    private bool ScalesDark => Dark is not null && MathF.Abs(DarkScale - 1f) > ScaleEpsilon;

    /// <summary>
    /// Fails at construction rather than per pixel: a scale with no bias to split the dark cannot
    /// be applied correctly, and silently ignoring it would quietly emit mis-calibrated frames,
    /// which is the failure mode this whole parameter exists to end.
    /// </summary>
    /// <exception cref="ArgumentException">A scale other than 1 was given without a
    /// <see cref="DarkBias"/>, or the scale is not a positive finite number.</exception>
    public Calibrator EnsureValid()
    {
        if (!float.IsFinite(DarkScale) || DarkScale <= 0f)
        {
            throw new ArgumentException($"DarkScale must be a positive finite number; got {DarkScale}.");
        }
        if (ScalesDark && DarkBias is null)
        {
            throw new ArgumentException(
                $"DarkScale={DarkScale:F4} needs a DarkBias to separate the dark's electronic offset " +
                "from its thermal signal; scaling a raw master dark whole would scale the pedestal too.");
        }
        return this;
    }

    /// <summary>
    /// Calibrates <paramref name="light"/>: bias and dark are subtracted (with
    /// pedestal applied on the dark step), the result is clamped to
    /// non-negative, then divided by the flat. Each master's presence is
    /// optional; null masters skip that step.
    /// <para><b>Frame ownership: this CONSUMES <paramref name="light"/> and the caller owns the
    /// result</b> (convention 4). Release the result, exactly once, and never touch the input again.
    /// One rule, whatever the configuration.</para>
    /// <para><b>It used to be convention 5, and this was the load-bearing example.</b> With no
    /// masters every step is skipped and the return value IS the input, so ownership of the result
    /// depended on runtime configuration and thirteen call sites wrote
    /// <c>if (!ReferenceEquals(calibrated, raw)) raw.Release();</c> longhand -- silently wrong if
    /// they got it backwards, because releasing a frame you do not own recycles pixels another
    /// holder is still reading. Taking ownership at the door retires that: each step releases the
    /// frame it just consumed (a no-op for an unbuffered intermediate, and the real handback for a
    /// pooled or camera-owned input), and the no-masters path returns the input with its ownership
    /// passed along. No copy, no allocation change, and nothing for a caller to decide.</para>
    /// <para>The alternative the plan expected to take -- always copy -- was measured and refuted:
    /// it adds 7.2 ms and 32 MiB per light and removes no allocation, because the copy's destination
    /// is the frame the caller keeps either way. See "The P1 measurement" in
    /// <c>docs/plans/frame-lifecycle.md</c>.</para>
    /// </summary>
    /// <exception cref="ArgumentException">A master's shape doesn't match the
    /// light's. Surfaced from <see cref="Image.Subtract"/> / <see cref="Image.Divide"/>.</exception>
    public Image Apply(Image light)
    {
        // Ownership arrives with the argument, so every step below can release what it consumed
        // WITHOUT asking whether it owns it -- the answer is always yes. For an intermediate with no
        // ref-counted buffer that release is a no-op and the frame stays readable (convention 2);
        // for a pooled or camera-owned input it is the handback that makes unconditional pooling
        // possible upstream.
        var result = light;

        if (Bias is { } bias)
        {
            // Bias subtraction with no pedestal: bias is small (~camera
            // electronic offset), shouldn't drive pixels negative.
            var next = result.Subtract(bias);
            result.Release();
            result = next;
        }

        if (Dark is { } dark)
        {
            // Rescale the thermal component when the dark's exposure does not match the light's.
            // The offset stays put: effective = bias + (dark - bias) * scale, which is the dark
            // itself when scale is 1.
            if (ScalesDark && DarkBias is { } darkBias)
            {
                dark = ScaleDarkThermal(dark, darkBias, DarkScale);
            }

            // Pedestal applied here, on the deeper subtract. Subtract clamps
            // to >= 0 after adding the pedestal so the dark-pedestal trick
            // takes effect at the right time.
            var next = result.Subtract(dark, addedPedestal: Pedestal);
            result.Release();
            result = next;
        }

        if (Flat is { } flat)
        {
            var next = result.Divide(flat, epsilon: FlatEpsilon);
            result.Release();
            result = next;
        }

        return result;
    }

    /// <summary>
    /// Tile-mode calibration: applies the same arithmetic as <see cref="Apply"/>
    /// but to a single-channel slice of a light frame. Reads the corresponding
    /// region of each master directly from its <c>float[,]</c> backing array; 
    /// no full calibrated image is materialised.
    /// </summary>
    /// <param name="lightTile">Light-frame tile pixels, row-major,
    /// length <c>regionWidth * regionHeight</c>.</param>
    /// <param name="channel">Channel index. Must be < master's
    /// <see cref="Image.ChannelCount"/> for any provided master.</param>
    /// <param name="regionX">Left edge of the tile in master coordinates (0-based).</param>
    /// <param name="regionY">Top edge of the tile in master coordinates (0-based).</param>
    /// <param name="regionWidth">Tile width in pixels.</param>
    /// <param name="regionHeight">Tile height in pixels.</param>
    /// <param name="dst">Output buffer for the calibrated tile, row-major,
    /// length <c>regionWidth * regionHeight</c>. May alias <paramref name="lightTile"/>.</param>
    /// <exception cref="ArgumentException">Buffer lengths don't match
    /// <c>regionWidth * regionHeight</c>, or the region falls outside a
    /// master's bounds.</exception>
    public void ApplyTile(
        ReadOnlySpan<float> lightTile,
        int channel,
        int regionX, int regionY, int regionWidth, int regionHeight,
        Span<float> dst)
    {
        var expected = regionWidth * regionHeight;
        if (lightTile.Length != expected || dst.Length != expected)
        {
            throw new ArgumentException(
                $"Tile spans must have length regionWidth*regionHeight = {expected}; got light={lightTile.Length}, dst={dst.Length}.");
        }

        var biasChannel = Bias?.GetChannelArray(channel);
        var darkChannel = Dark?.GetChannelArray(channel);
        var flatChannel = Flat?.GetChannelArray(channel);
        // Only read the dark's bias when it is actually going to be used, so the common
        // scale-of-1 path costs nothing and needs no bias present.
        var scalesDark = ScalesDark;
        var darkBiasChannel = scalesDark ? DarkBias?.GetChannelArray(channel) : null;
        ValidateRegionInBounds(biasChannel, regionX, regionY, regionWidth, regionHeight, "bias");
        ValidateRegionInBounds(darkChannel, regionX, regionY, regionWidth, regionHeight, "dark");
        ValidateRegionInBounds(darkBiasChannel, regionX, regionY, regionWidth, regionHeight, "dark bias");
        ValidateRegionInBounds(flatChannel, regionX, regionY, regionWidth, regionHeight, "flat");

        var pedestal = Pedestal;
        var epsilon = FlatEpsilon;
        var darkScale = scalesDark && darkBiasChannel is not null ? DarkScale : 1f;

        for (var y = 0; y < regionHeight; y++)
        {
            var srcY = regionY + y;
            var rowOffset = y * regionWidth;
            for (var x = 0; x < regionWidth; x++)
            {
                var srcX = regionX + x;
                var v = lightTile[rowOffset + x];

                if (biasChannel is not null) v -= biasChannel[srcY, srcX];
                if (darkChannel is not null)
                {
                    var d = darkChannel[srcY, srcX];
                    if (darkBiasChannel is not null)
                    {
                        var b = darkBiasChannel[srcY, srcX];
                        d = b + (d - b) * darkScale;
                    }
                    v -= d;
                    v += pedestal;
                }
                if (v < 0f) v = 0f;
                if (flatChannel is not null)
                {
                    var f = flatChannel[srcY, srcX];
                    v = f > epsilon ? v / f : float.NaN;
                }
                dst[rowOffset + x] = v;
            }
        }
    }

    /// <summary>
    /// <c>bias + (dark - bias) * scale</c>, per pixel. Materialises a new dark rather than folding
    /// the arithmetic into <see cref="Image.Subtract"/> so the whole-frame and tile paths apply an
    /// identical expression; <see cref="Apply"/> is the one-off path (verification tests, single
    /// light frames) while the hot integrator path is <see cref="ApplyTile"/>, which does it inline
    /// with no allocation.
    /// </summary>
    private static Image ScaleDarkThermal(Image dark, Image bias, float scale)
    {
        var channelCount = dark.ChannelCount;
        var scaled = new float[channelCount][,];
        for (var c = 0; c < channelCount; c++)
        {
            var d = dark.GetChannelArray(c);
            var b = bias.GetChannelArray(c);
            var h = d.GetLength(0);
            var w = d.GetLength(1);
            if (b.GetLength(0) != h || b.GetLength(1) != w)
            {
                throw new ArgumentException(
                    $"Dark bias shape {b.GetLength(1)}x{b.GetLength(0)} does not match dark {w}x{h}.");
            }
            var outCh = new float[h, w];
            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    var bv = b[y, x];
                    outCh[y, x] = bv + (d[y, x] - bv) * scale;
                }
            }
            scaled[c] = outCh;
        }
        // Shares no buffer with the source dark, so Buffer stays null and release ownership
        // remains entirely with the caller's original image.
        return new Image(scaled, dark.BitDepth, dark.MaxValue, dark.MinValue, dark.Pedestal, dark.ImageMeta,
            dark.SamplesAreUnitReferred);
    }

    private static void ValidateRegionInBounds(float[,]? channel, int rx, int ry, int rw, int rh, string name)
    {
        if (channel is null) return;
        var h = channel.GetLength(0);
        var w = channel.GetLength(1);
        if (rx < 0 || ry < 0 || rx + rw > w || ry + rh > h)
        {
            throw new ArgumentException(
                $"Tile region [{rx},{ry} {rw}x{rh}] falls outside {name} master bounds {w}x{h}.");
        }
    }
}
