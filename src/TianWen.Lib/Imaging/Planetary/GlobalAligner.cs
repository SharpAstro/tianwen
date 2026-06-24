using System;
using System.Drawing;
using System.Numerics;
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
    private readonly Complex[] _referenceSpectrum;
    private readonly double _refCenterX;
    private readonly double _refCenterY;

    private GlobalAligner(int tileSize, Complex[] referenceSpectrum, double refCenterX, double refCenterY)
    {
        _tileSize = tileSize;
        _referenceSpectrum = referenceSpectrum;
        _refCenterX = refCenterX;
        _refCenterY = refCenterY;
    }

    /// <summary>The power-of-two tile edge used for phase correlation.</summary>
    public int TileSize => _tileSize;

    /// <summary>Reference disk centre (x, y) in frame coordinates.</summary>
    public (double X, double Y) ReferenceCenter => (_refCenterX, _refCenterY);

    /// <summary>
    /// Builds an aligner from the reference frame: finds its disk COM over <paramref name="region"/> and
    /// caches the Hann-windowed forward FFT of a <paramref name="tileSize"/> x <paramref name="tileSize"/>
    /// planet-centred luminance tile, so each <see cref="Estimate"/> correlates against the precomputed
    /// reference spectrum instead of re-transforming the (fixed) reference per frame.
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
        PlanetaryTile.ExtractLuma(reference, rcx, rcy, tileSize, tile);
        // Precompute the reference tile's Hann-windowed forward FFT once; every Estimate correlates the
        // frame tile against this cached spectrum instead of re-transforming the fixed reference per frame.
        var spectrum = PhaseCorrelation.PrepareReferenceSpectrum(tile, tileSize, tileSize, applyWindow: true);
        return new GlobalAligner(tileSize, spectrum, rcx, rcy);
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
        PlanetaryTile.ExtractLuma(frame, rcx, rcy, _tileSize, tile);

        // Bulk shift = integer rounded-COM difference (matches the tile centring); the phase-correlation
        // residual is the full sub-pixel remainder between the two integer-centred tiles. Window on --
        // real, non-periodic imagery.
        var residual = PhaseCorrelation.Estimate(_referenceSpectrum, tile, _tileSize, _tileSize, applyWindow: true);

        var dx = (rcx - _refCenterX) + residual.Dx;
        var dy = (rcy - _refCenterY) + residual.Dy;
        return new PhaseCorrelation.Shift(dx, dy, residual.PeakValue);
    }
}
