using System;
using System.Drawing;
using TianWen.Lib.Stat;

namespace TianWen.Lib.Imaging.Planetary;

/// <summary>
/// Global (whole-disk, translation-only) frame alignment: the planetary aligner's bootstrap. Per the
/// plan, each frame is coarse-located by its disk centre of mass (cheap, drift-robust), then a sub-pixel
/// residual is recovered by phase-correlating a planet-centred luminance tile against the reference's.
/// The combined shift is the displacement of the frame's planet relative to the reference's -- consumed
/// directly by <see cref="Image.AccumulateTranslatedInto"/>. This is the live-path aligner and the
/// per-frame coarse step the Phase 5 alignment-point mesh refines.
/// </summary>
public sealed class GlobalAligner
{
    private readonly int _tileSize;
    private readonly float[] _referenceTile;
    private readonly double _refCenterX;
    private readonly double _refCenterY;

    private GlobalAligner(int tileSize, float[] referenceTile, double refCenterX, double refCenterY)
    {
        _tileSize = tileSize;
        _referenceTile = referenceTile;
        _refCenterX = refCenterX;
        _refCenterY = refCenterY;
    }

    /// <summary>The power-of-two tile edge used for phase correlation.</summary>
    public int TileSize => _tileSize;

    /// <summary>Reference disk centre (x, y) in frame coordinates.</summary>
    public (double X, double Y) ReferenceCenter => (_refCenterX, _refCenterY);

    /// <summary>
    /// Builds an aligner from the reference frame: finds its disk COM over <paramref name="region"/> and
    /// caches a <paramref name="tileSize"/> x <paramref name="tileSize"/> planet-centred luminance tile.
    /// </summary>
    public static GlobalAligner FromReference(Image reference, Rectangle region, int tileSize)
    {
        ArgumentNullException.ThrowIfNull(reference);
        if (!ComplexFft.IsPowerOfTwo(tileSize))
        {
            throw new ArgumentException($"tileSize must be a power of two, got {tileSize}.", nameof(tileSize));
        }

        var (cx, cy) = PlanetaryDisk.CenterOfMass(reference, region);

        // The tile is centred on the INTEGER-rounded COM, so the cached centre must be that same integer:
        // the bulk shift between two frames is then their rounded-COM difference (an integer), and the
        // phase-correlation residual carries the entire sub-pixel offset. Storing the sub-pixel COM here
        // would double-count the fractional part (it would appear in both the bulk shift and the residual).
        var rcx = Math.Round(cx);
        var rcy = Math.Round(cy);
        var tile = new float[tileSize * tileSize];
        ExtractLumaTile(reference, rcx, rcy, tileSize, tile);
        return new GlobalAligner(tileSize, tile, rcx, rcy);
    }

    /// <summary>
    /// Estimates the shift of <paramref name="frame"/> relative to the reference: the disk-COM bulk offset
    /// plus the phase-correlation sub-pixel residual. The result's <c>(Dx, Dy)</c> feed
    /// <see cref="Image.AccumulateTranslatedInto"/> directly (sample the frame at <c>(x + Dx, y + Dy)</c>).
    /// </summary>
    public PhaseCorrelation.Shift Estimate(Image frame, Rectangle region)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var (cx, cy) = PlanetaryDisk.CenterOfMass(frame, region);
        var rcx = Math.Round(cx);
        var rcy = Math.Round(cy);

        var tile = new float[_tileSize * _tileSize];
        ExtractLumaTile(frame, rcx, rcy, _tileSize, tile);

        // Bulk shift = integer rounded-COM difference (matches the tile centring); the phase-correlation
        // residual is the full sub-pixel remainder between the two integer-centred tiles. Window on --
        // real, non-periodic imagery.
        var residual = PhaseCorrelation.Estimate(_referenceTile, tile, _tileSize, _tileSize, applyWindow: true);

        var dx = (rcx - _refCenterX) + residual.Dx;
        var dy = (rcy - _refCenterY) + residual.Dy;
        return new PhaseCorrelation.Shift(dx, dy, residual.PeakValue);
    }

    /// <summary>
    /// Fills <paramref name="dst"/> (length <c>size*size</c>) with a luminance-proxy tile centred on the
    /// integer-rounded disk centre, zero-padded where the tile extends past the frame edge.
    /// </summary>
    private static void ExtractLumaTile(Image frame, double centerX, double centerY, int size, float[] dst)
    {
        var originX = (int)Math.Round(centerX) - (size / 2);
        var originY = (int)Math.Round(centerY) - (size / 2);
        int w = frame.Width, h = frame.Height, channels = frame.ChannelCount;
        var inv = 1f / channels;

        for (var ty = 0; ty < size; ty++)
        {
            var sy = originY + ty;
            var dstRow = ty * size;
            for (var tx = 0; tx < size; tx++)
            {
                var sx = originX + tx;
                var v = 0f;
                if (sx >= 0 && sx < w && sy >= 0 && sy < h)
                {
                    for (var c = 0; c < channels; c++)
                    {
                        v += frame[c, sy, sx];
                    }

                    v *= inv;
                }

                dst[dstRow + tx] = v;
            }
        }
    }
}
