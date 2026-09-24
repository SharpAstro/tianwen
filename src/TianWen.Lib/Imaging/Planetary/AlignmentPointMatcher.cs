using System;
using System.Collections.Immutable;
using System.Numerics;
using TianWen.Lib.Geometry;
using TianWen.Lib.Stat;

namespace TianWen.Lib.Imaging.Planetary;

/// <summary>
/// Tracks a fixed set of alignment points from the reference frame into each captured frame and produces
/// that frame's <see cref="DisplacementMesh"/>. For each AP it phase-correlates the reference patch against
/// the frame patch at the globally-predicted location; the residual is the local seeing distortion the
/// whole-disk global shift missed at that point. The sparse residual field is interpolated to a per-pixel
/// mesh. Matching runs on the luminance proxy, so the one mesh co-registers all CFA sub-planes.
/// <para><b>Each reference patch is transformed ONCE</b>, here, not per frame: the reference is fixed, so
/// its Hann-windowed spectrum is too, and re-transforming it for every point of every frame was one of the
/// three FFTs a match paid for (numerically identical, pinned by
/// <c>Precomputed_reference_spectrum_matches_single_call_exactly</c>). <b>One <see cref="BuildMesh"/> at a
/// time per instance</b>, because the frame patch and its spectrum are per-instance scratch; together those
/// were two new 16 KB spectra per point per frame at a 32 px patch.</para>
/// </summary>
public sealed class AlignmentPointMatcher
{
    private readonly int _patchSize;
    private readonly int _width;
    private readonly int _height;
    private readonly ImmutableArray<PixelPoint> _apCenters;
    private readonly Complex[][] _referenceSpectra;
    private readonly float[] _patch;
    private readonly Complex[] _spectrumScratch;

    private AlignmentPointMatcher(int patchSize, int width, int height, ImmutableArray<PixelPoint> apCenters, Complex[][] referenceSpectra)
    {
        _patchSize = patchSize;
        _width = width;
        _height = height;
        _apCenters = apCenters;
        _referenceSpectra = referenceSpectra;
        _patch = new float[patchSize * patchSize];
        _spectrumScratch = new Complex[patchSize * patchSize];
    }

    /// <summary>The alignment-point centres being tracked (reference-frame coordinates).</summary>
    public ImmutableArray<PixelPoint> AlignmentPoints => _apCenters;

    /// <summary>
    /// Caches a luminance patch of <paramref name="patchSize"/> (power of two) per AP centre from the
    /// reference frame.
    /// </summary>
    public static AlignmentPointMatcher FromReference(Image reference, ImmutableArray<PixelPoint> apCenters, int patchSize = 32)
    {
        ArgumentNullException.ThrowIfNull(reference);
        if (!ComplexFft.IsPowerOfTwo(patchSize))
        {
            throw new ArgumentException($"patchSize must be a power of two, got {patchSize}.", nameof(patchSize));
        }

        var spectra = new Complex[apCenters.Length][];
        var patch = new float[patchSize * patchSize];
        for (var i = 0; i < apCenters.Length; i++)
        {
            var p = apCenters[i];
            PlanetaryTile.ExtractLuma(reference, p.X, p.Y, patchSize, patch);
            spectra[i] = PhaseCorrelation.PrepareReferenceSpectrum(patch, patchSize, patchSize, applyWindow: true);
        }

        return new AlignmentPointMatcher(patchSize, reference.Width, reference.Height, apCenters, spectra);
    }

    /// <summary>
    /// Builds the displacement mesh for <paramref name="frame"/> given its whole-disk global shift
    /// <c>(globalDx, globalDy)</c> (from <see cref="GlobalAligner"/>). Reference and frame patches are
    /// extracted on integer-rounded centres, so the integer baseline <c>round(global)</c> carries the
    /// bulk offset and each phase-correlation residual carries the AP's full sub-pixel local correction
    /// (the same integer-baseline / sub-pixel-residual split the global aligner uses).
    /// </summary>
    public DisplacementMesh BuildMesh(Image frame, float globalDx, float globalDy, float nodeSpacing = 32f, float influence = 48f)
    {
        ArgumentNullException.ThrowIfNull(frame);

        var rgx = MathF.Round(globalDx);
        var rgy = MathF.Round(globalDy);

        var shifts = _apCenters.Length == 0 ? [] : new AlignmentPointShift[_apCenters.Length];
        for (var i = 0; i < _apCenters.Length; i++)
        {
            var p = _apCenters[i];
            // ExtractLuma writes every sample, so the reused patch carries nothing from the last point.
            PlanetaryTile.ExtractLuma(frame, p.X + rgx, p.Y + rgy, _patchSize, _patch);
            var residual = PhaseCorrelation.Estimate(_referenceSpectra[i], _patch, _patchSize, _patchSize, _spectrumScratch, applyWindow: true);
            shifts[i] = new AlignmentPointShift(p.X, p.Y, (float)residual.Dx, (float)residual.Dy);
        }

        return DisplacementMesh.Build(_width, _height, rgx, rgy, shifts, nodeSpacing, influence);
    }
}
