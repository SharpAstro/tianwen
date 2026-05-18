using System;
using System.Runtime.InteropServices;

namespace TianWen.Lib.Imaging.Calibration;

/// <summary>
/// Applies bias / dark / flat master frames to a light frame. Each master is
/// optional — pass <c>null</c> for any step the caller doesn't want applied.
/// <para>
/// Formula: <c>calibrated = max(light - bias - dark + pedestal, 0) / max(flat, epsilon)</c>.
/// The pedestal is added on the dark subtraction (the deeper of the two; bias
/// subtraction alone rarely needs an offset). The flat-denominator clamp
/// prevents inf/NaN on dead sensor pixels.
/// </para>
/// <para>
/// Two entry points for different consumers:
/// </para>
/// <list type="bullet">
/// <item>
/// <see cref="Apply"/> — whole-frame, returns a new <see cref="Image"/>. Used
/// by master-flat verification tests and one-off light calibration. Chains
/// <see cref="Image.Subtract"/> + <see cref="Image.Divide"/> from Phase 1.
/// </item>
/// <item>
/// <see cref="ApplyTile"/> — single-channel, region-based, span output. Used by
/// the Phase 8 tile-pipelined integrator so a full calibrated <see cref="Image"/>
/// never materialises. Reads the corresponding region of each master directly
/// from the held <c>float[,]</c> backing arrays — zero copy.
/// </item>
/// </list>
/// </summary>
/// <param name="Bias">Master bias frame, or <c>null</c> to skip bias subtraction.</param>
/// <param name="Dark">Master dark frame, or <c>null</c> to skip dark subtraction.</param>
/// <param name="Flat">Master flat frame (median ~ 1.0), or <c>null</c> to skip
/// flat division.</param>
/// <param name="Pedestal">ADU offset added per pixel before the non-negative
/// clamp. SetiAstro's <c>subtract_dark_with_pedestal</c> trick — prevents the
/// clamp from zeroing out background pixels when the dark mean exceeds the
/// light's measured background. Suggested 100-1000 for raw ADU data, or
/// 0.001-0.01 for normalised [0, 1] float data. Default 0 (no offset).</param>
/// <param name="FlatEpsilon">Lower bound on the flat divisor to prevent
/// division by zero on dead sensor cells. Default 1e-6f.</param>
public sealed record Calibrator(
    Image? Bias = null,
    Image? Dark = null,
    Image? Flat = null,
    float Pedestal = 0f,
    float FlatEpsilon = 1e-6f)
{
    /// <summary>
    /// Returns a calibrated copy of <paramref name="light"/>. Bias and dark are
    /// subtracted (with pedestal applied on the dark step), the result is
    /// clamped to non-negative, then divided by the flat. Each master's
    /// presence is optional; null masters skip that step.
    /// </summary>
    /// <exception cref="ArgumentException">A master's shape doesn't match the
    /// light's. Surfaced from <see cref="Image.Subtract"/> / <see cref="Image.Divide"/>.</exception>
    public Image Apply(Image light)
    {
        var result = light;

        if (Bias is { } bias)
        {
            // Bias subtraction with no pedestal — bias is small (~camera
            // electronic offset), shouldn't drive pixels negative.
            result = result.Subtract(bias);
        }

        if (Dark is { } dark)
        {
            // Pedestal applied here, on the deeper subtract. Subtract clamps
            // to >= 0 after adding the pedestal so the dark-pedestal trick
            // takes effect at the right time.
            result = result.Subtract(dark, addedPedestal: Pedestal);
        }

        if (Flat is { } flat)
        {
            result = result.Divide(flat, epsilon: FlatEpsilon);
        }

        return result;
    }

    /// <summary>
    /// Tile-mode calibration: applies the same arithmetic as <see cref="Apply"/>
    /// but to a single-channel slice of a light frame. Reads the corresponding
    /// region of each master directly from its <c>float[,]</c> backing array —
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
        ValidateRegionInBounds(biasChannel, regionX, regionY, regionWidth, regionHeight, "bias");
        ValidateRegionInBounds(darkChannel, regionX, regionY, regionWidth, regionHeight, "dark");
        ValidateRegionInBounds(flatChannel, regionX, regionY, regionWidth, regionHeight, "flat");

        var pedestal = Pedestal;
        var epsilon = FlatEpsilon;

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
                    v -= darkChannel[srcY, srcX];
                    v += pedestal;
                }
                if (v < 0f) v = 0f;
                if (flatChannel is not null)
                {
                    var f = flatChannel[srcY, srcX];
                    v /= f > epsilon ? f : epsilon;
                }
                dst[rowOffset + x] = v;
            }
        }
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
