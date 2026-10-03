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
    private readonly float[] _extractScratch;
    private readonly Complex[] _spectrumScratch;
    private readonly bool _whiten;
    private readonly float[][]? _referenceSearch;
    private readonly double[]? _differenceScratch;
    private readonly double[][]? _referenceSignal;
    private readonly double[]? _referenceFloor;
    private readonly (double Xx, double Yy, double Xy)[] _gradientEnergy;

    /// <summary>How far, px, a square difference searches each way (<see cref="PlanetaryPointEstimator.SquareDifference"/>): a point's
    /// residual is the local warp alone once its patch is cut at the exact global shift, well under this.</summary>
    public const int SquareDifferenceMargin = 3;

    private AlignmentPointMatcher(int patchSize, int width, int height, ImmutableArray<PixelPoint> apCenters, Complex[][] referenceSpectra, bool whiten,
        float[][]? referenceSearch, double[][]? referenceSignal, double[]? referenceFloor, (double Xx, double Yy, double Xy)[] gradientEnergy)
    {
        _whiten = whiten;
        _referenceSearch = referenceSearch;
        _referenceSignal = referenceSignal;
        _referenceFloor = referenceFloor;
        _gradientEnergy = gradientEnergy;
        _differenceScratch = referenceSearch is null ? null : new double[(patchSize * patchSize) + (((2 * SquareDifferenceMargin) + 1) * ((2 * SquareDifferenceMargin) + 1))];
        _patchSize = patchSize;
        _width = width;
        _height = height;
        _apCenters = apCenters;
        _referenceSpectra = referenceSpectra;
        _patch = new float[patchSize * patchSize];
        _extractScratch = new float[PlanetaryTile.ScratchLength(patchSize)];
        _spectrumScratch = new Complex[patchSize * patchSize];
    }

    /// <summary>
    /// A matcher on the same reference points with scratch of its own, for another thread: <see cref="Match(Image, float, float, DerotationField?, Span{AlignmentPointShift})"/>
    /// writes this one's scratch, while the reference spectra, read only, are shared. It matches exactly as this one does.
    /// </summary>
    internal AlignmentPointMatcher Twin()
        => new AlignmentPointMatcher(_patchSize, _width, _height, _apCenters, _referenceSpectra, _whiten, _referenceSearch, _referenceSignal, _referenceFloor, _gradientEnergy);

    /// <summary>The alignment-point centres being tracked (reference-frame coordinates).</summary>
    public ImmutableArray<PixelPoint> AlignmentPoints => _apCenters;

    /// <summary>
    /// Caches a luminance patch of <paramref name="patchSize"/> (power of two) per AP centre from the
    /// reference frame. <paramref name="whiten"/> picks phase correlation or a plain cross-correlation: on a single 8-bit
    /// frame a whitened 16 px patch is placed to 1.1 px RMS, a plain one to 0.35 (<c>AlignmentPointMatchingTests</c>).
    /// </summary>
    public static AlignmentPointMatcher FromReference(Image reference, ImmutableArray<PixelPoint> apCenters, int patchSize = 32, bool whiten = true)
        => FromReference(reference, apCenters, patchSize, whiten, PlanetaryPointEstimator.Correlation);

    /// <summary>
    /// <see cref="FromReference(Image, ImmutableArray{PixelPoint}, int, bool)"/> reading each point's shift by
    /// <paramref name="estimator"/>: a square difference keeps, per point, the reference patch
    /// <see cref="SquareDifferenceMargin"/> px larger each side that it searches.
    /// </summary>
    public static AlignmentPointMatcher FromReference(Image reference, ImmutableArray<PixelPoint> apCenters, int patchSize, bool whiten, PlanetaryPointEstimator estimator)
    {
        ArgumentNullException.ThrowIfNull(reference);
        if (!ComplexFft.IsPowerOfTwo(patchSize))
        {
            throw new ArgumentException($"patchSize must be a power of two, got {patchSize}.", nameof(patchSize));
        }

        var spectra = new Complex[apCenters.Length][];
        var search = estimator == PlanetaryPointEstimator.SquareDifference ? new float[apCenters.Length][] : null;
        var weighted = estimator == PlanetaryPointEstimator.WeightedCorrelation;
        var signal = weighted ? new double[apCenters.Length][] : null;
        var floor = weighted ? new double[apCenters.Length] : null;
        var gradients = new (double Xx, double Yy, double Xy)[apCenters.Length];
        var patch = new float[patchSize * patchSize];
        var searchSize = patchSize + (2 * SquareDifferenceMargin);
        for (var i = 0; i < apCenters.Length; i++)
        {
            var p = apCenters[i];
            PlanetaryTile.ExtractLuma(reference, p.X, p.Y, patchSize, patch);
            // A weighted correlation weighs the plain cross-spectrum, so its reference is never whitened.
            spectra[i] = PhaseCorrelation.PrepareReferenceSpectrum(patch, patchSize, patchSize, applyWindow: true, whiten && !weighted);
            gradients[i] = GradientEnergy(patch, patchSize);
            if (search is not null)
            {
                search[i] = new float[searchSize * searchSize];
                PlanetaryTile.ExtractLuma(reference, p.X, p.Y, searchSize, search[i]);
            }
            if (signal is not null && floor is not null)
            {
                // The reference's signal power at each frequency: its power less its own floor, none where it holds none.
                floor[i] = PhaseCorrelation.SpectralFloor(spectra[i], patchSize, patchSize);
                signal[i] = new double[spectra[i].Length];
                for (var k = 0; k < spectra[i].Length; k++)
                {
                    var c = spectra[i][k];
                    signal[i][k] = Math.Max(0, (c.Real * c.Real) + (c.Imaginary * c.Imaginary) - floor[i]);
                }
            }
        }

        return new AlignmentPointMatcher(patchSize, reference.Width, reference.Height, apCenters, spectra, whiten, search, signal, floor, gradients);
    }

    // The sums over a patch of its squared gradients, by central differences over its interior: the Fisher information a shift has
    // in it, per unit of noise variance.
    private static (double Xx, double Yy, double Xy) GradientEnergy(ReadOnlySpan<float> patch, int size)
    {
        double xx = 0, yy = 0, xy = 0;
        for (var y = 1; y < size - 1; y++)
        {
            for (var x = 1; x < size - 1; x++)
            {
                var gx = 0.5 * (patch[(y * size) + x + 1] - patch[(y * size) + x - 1]);
                var gy = 0.5 * (patch[((y + 1) * size) + x] - patch[((y - 1) * size) + x]);
                xx += gx * gx;
                yy += gy * gy;
                xy += gx * gy;
            }
        }
        return (xx, yy, xy);
    }

    /// <summary>
    /// The noise variance a pixel of <paramref name="frame"/> carries at point <paramref name="i"/>: its patch, cut where the frame's
    /// global shift puts the point, read by its windowed spectrum's white floor (<see cref="PhaseCorrelation.SpectralFloor"/>). What
    /// <see cref="Bound"/> is taken against. One call at a time per instance, as <see cref="Match(Image, float, float, Span{AlignmentPointShift})"/>.
    /// </summary>
    internal double PatchNoise(Image frame, float globalDx, float globalDy, int i)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var p = _apCenters[i];
        PlanetaryTile.ExtractLumaAt(frame, p.X + globalDx, p.Y + globalDy, _patchSize, _patch, _extractScratch);
        var spectrum = PhaseCorrelation.PrepareReferenceSpectrum(_patch, _patchSize, _patchSize, applyWindow: true, whiten: false);
        return PhaseCorrelation.SpectralFloor(spectrum, _patchSize, _patchSize) / PhaseCorrelation.WindowEnergy(_patchSize, _patchSize);
    }

    /// <summary>
    /// The Cramer-Rao bound on point <paramref name="i"/>'s shift for a frame whose pixels each carry <paramref name="noiseVariance"/>
    /// (Pham et al. 2005), px squared on each axis: the diagonal of the inverse Fisher information, the reference patch's squared
    /// gradients summed over its box and divided by that variance. The reference is taken as noiseless, as a stack of many frames
    /// nearly is; no unbiased estimator using the patch's pixels does better.
    /// </summary>
    internal (double X, double Y) Bound(int i, double noiseVariance)
    {
        var (xx, yy, xy) = _gradientEnergy[i];
        var det = (xx * yy) - (xy * xy);
        return det > 0 ? (noiseVariance * yy / det, noiseVariance * xx / det) : (double.PositiveInfinity, double.PositiveInfinity);
    }

    /// <summary>
    /// Builds the displacement mesh for <paramref name="frame"/> given its whole-disk global shift
    /// <c>(globalDx, globalDy)</c> (from <see cref="GlobalAligner"/>): the global shift itself, exactly, and each point's
    /// residual over it (<see cref="Match"/>), the local warp the whole-disk shift missed there.
    /// </summary>
    public DisplacementMesh BuildMesh(Image frame, float globalDx, float globalDy, float nodeSpacing = 32f, float influence = 48f, float residualGain = 1f)
        => BuildMesh(frame, globalDx, globalDy, derotation: null, nodeSpacing, influence, residualGain);

    /// <summary>
    /// <see cref="BuildMesh(Image, float, float, float, float)"/> for a frame carried to its capture's epoch by
    /// <paramref name="derotation"/> (docs/plans/planetary-restoration.md, R6 part 2): each point matched where the rotation and
    /// the shift put it, and the mesh built over the field.
    /// </summary>
    public DisplacementMesh BuildMesh(Image frame, float globalDx, float globalDy, DerotationField? derotation, float nodeSpacing = 32f, float influence = 48f,
        float residualGain = 1f)
    {
        ArgumentNullException.ThrowIfNull(frame);

        var shifts = _apCenters.Length == 0 ? [] : new AlignmentPointShift[_apCenters.Length];
        Match(frame, globalDx, globalDy, derotation, shifts);
        return DisplacementMesh.Build(_width, _height, globalDx, globalDy, shifts, derotation, nodeSpacing, influence, residualGain: residualGain);
    }

    /// <summary>
    /// The displacement mesh from points already matched (or pooled, <see cref="AlignmentPointTracks"/>): each a residual over
    /// the global shift (<paramref name="globalDx"/>, <paramref name="globalDy"/>), as <see cref="Match"/> writes them.
    /// </summary>
    public DisplacementMesh BuildMesh(float globalDx, float globalDy, ReadOnlySpan<AlignmentPointShift> shifts, float nodeSpacing = 32f, float influence = 48f,
        float residualGain = 1f)
    {
        return DisplacementMesh.Build(_width, _height, globalDx, globalDy, shifts, nodeSpacing, influence, residualGain: residualGain);
    }

    /// <summary>
    /// Matches every alignment point of <paramref name="frame"/> given its whole-disk shift, writing each point's residual
    /// over that shift into <paramref name="destination"/> (one per point, in <see cref="AlignmentPoints"/> order): what
    /// <see cref="BuildMesh(Image, float, float, float, float)"/> interpolates, and what the capture statistics read the warp
    /// from (docs/plans/planetary-restoration.md, R2). Each frame patch is cut at the point moved by the shift EXACTLY
    /// (<see cref="PlanetaryTile.ExtractLumaAt"/>), so a residual is the local warp alone. Cut at the rounded shift, a residual
    /// had to carry the shift's own fraction too, and where a patch holds little to place it by (along a planet's belts) it
    /// locked to the whole pixel: every frame's mesh with it, a mesh stack misregistered by up to half a pixel, and a Bayer
    /// drizzle past the sensor grid left red and blue columns no drop reached (R5a).
    /// </summary>
    public void Match(Image frame, float globalDx, float globalDy, Span<AlignmentPointShift> destination)
        => Match(frame, globalDx, globalDy, derotation: null, destination);

    // Point i's shift by the windowed correlation, from the patch just cut: weighted by the maximum-likelihood weight when the
    // matcher was made for it.
    private (double Dx, double Dy) Correlate(int i)
    {
        var residual = _referenceSignal is { } signal && _referenceFloor is { } floor
            ? PhaseCorrelation.EstimateWeighted(_referenceSpectra[i], signal[i], floor[i], _patch, _patchSize, _patchSize, _spectrumScratch)
            : PhaseCorrelation.Estimate(_referenceSpectra[i], _patch, _patchSize, _patchSize, _spectrumScratch, applyWindow: true, _whiten);
        return (residual.Dx, residual.Dy);
    }

    /// <summary>
    /// <see cref="Match(Image, float, float, Span{AlignmentPointShift})"/> for a frame carried to its capture's epoch: each
    /// point's patch cut where the rotation put it as well as the shift (the <paramref name="derotation"/>'s offset at the
    /// point), so a residual is again the local warp alone.
    /// </summary>
    public void Match(Image frame, float globalDx, float globalDy, DerotationField? derotation, Span<AlignmentPointShift> destination)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentOutOfRangeException.ThrowIfLessThan(destination.Length, _apCenters.Length);

        for (var i = 0; i < _apCenters.Length; i++)
        {
            var p = _apCenters[i];
            var (rx, ry) = derotation?.OffsetAt(p.X, p.Y) ?? (0f, 0f);
            // The extraction writes every sample, so the reused patch carries nothing from the last point.
            PlanetaryTile.ExtractLumaAt(frame, p.X + globalDx + rx, p.Y + globalDy + ry, _patchSize, _patch, _extractScratch);
            var (dx, dy) = _referenceSearch is { } search && _differenceScratch is { } scratch
                ? SquareDifferenceShift.Estimate(search[i], _patch, _patchSize, SquareDifferenceMargin, scratch)
                : Correlate(i);
            destination[i] = new AlignmentPointShift(p.X, p.Y, (float)dx, (float)dy);
        }
    }
}
