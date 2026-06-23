using System;
using System.Collections.Immutable;
using System.Drawing;
using TianWen.Lib.Stat;

namespace TianWen.Lib.Imaging.Planetary;

/// <summary>
/// Tracks a fixed set of alignment points from the reference frame into each captured frame and produces
/// that frame's <see cref="DisplacementMesh"/>. For each AP it phase-correlates the reference patch against
/// the frame patch at the globally-predicted location; the residual is the local seeing distortion the
/// whole-disk global shift missed at that point. The sparse residual field is interpolated to a per-pixel
/// mesh. Matching runs on the luminance proxy, so the one mesh co-registers all CFA sub-planes.
/// </summary>
public sealed class AlignmentPointMatcher
{
    private readonly int _patchSize;
    private readonly int _width;
    private readonly int _height;
    private readonly ImmutableArray<Point> _apCenters;
    private readonly float[][] _referencePatches;

    private AlignmentPointMatcher(int patchSize, int width, int height, ImmutableArray<Point> apCenters, float[][] referencePatches)
    {
        _patchSize = patchSize;
        _width = width;
        _height = height;
        _apCenters = apCenters;
        _referencePatches = referencePatches;
    }

    /// <summary>The alignment-point centres being tracked (reference-frame coordinates).</summary>
    public ImmutableArray<Point> AlignmentPoints => _apCenters;

    /// <summary>
    /// Caches a luminance patch of <paramref name="patchSize"/> (power of two) per AP centre from the
    /// reference frame.
    /// </summary>
    public static AlignmentPointMatcher FromReference(Image reference, ImmutableArray<Point> apCenters, int patchSize = 32)
    {
        ArgumentNullException.ThrowIfNull(reference);
        if (!ComplexFft.IsPowerOfTwo(patchSize))
        {
            throw new ArgumentException($"patchSize must be a power of two, got {patchSize}.", nameof(patchSize));
        }

        var patches = new float[apCenters.Length][];
        for (var i = 0; i < apCenters.Length; i++)
        {
            var p = apCenters[i];
            var patch = new float[patchSize * patchSize];
            PlanetaryTile.ExtractLuma(reference, p.X, p.Y, patchSize, patch);
            patches[i] = patch;
        }

        return new AlignmentPointMatcher(patchSize, reference.Width, reference.Height, apCenters, patches);
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
        var patch = new float[_patchSize * _patchSize];
        for (var i = 0; i < _apCenters.Length; i++)
        {
            var p = _apCenters[i];
            PlanetaryTile.ExtractLuma(frame, p.X + rgx, p.Y + rgy, _patchSize, patch);
            var residual = PhaseCorrelation.Estimate(_referencePatches[i], patch, _patchSize, _patchSize, applyWindow: true);
            shifts[i] = new AlignmentPointShift(p.X, p.Y, (float)residual.Dx, (float)residual.Dy);
        }

        return DisplacementMesh.Build(_width, _height, rgx, rgy, shifts, nodeSpacing, influence);
    }
}
