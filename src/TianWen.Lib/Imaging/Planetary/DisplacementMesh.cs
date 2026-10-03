using System;

namespace TianWen.Lib.Imaging.Planetary;

/// <summary>An alignment point's reference position and its measured local residual shift (the part of
/// the frame-to-reference displacement the global shift did not capture at that point).</summary>
public readonly record struct AlignmentPointShift(float X, float Y, float ResidualX, float ResidualY);

/// <summary>
/// A smooth per-pixel displacement field on a coarse node grid: warping samples the frame at
/// <c>(x + OffsetX, y + OffsetY)</c> to register it onto the reference. Built from scattered
/// alignment-point residuals blended (Gaussian-weighted, regularised toward the baseline) over a global
/// shift, so the field equals the global shift far from any AP and bends toward each AP's local
/// correction nearby -- the seeing-distortion mesh a single affine transform cannot represent.
/// <para>
/// A frame carried to its capture's epoch (docs/plans/planetary-restoration.md, R6 part 2) adds its de-rotation beneath: a
/// <see cref="DerotationField"/>, per pixel, since the planet's rotation moves a disk's middle and leaves its limb where it is,
/// which a node grid a disk's radius apart cannot follow. The nodes then carry the registration over it, and a sample is
/// relit by the field (<see cref="RelightAt"/>). The field is the stacker's, refilled for every frame, so a mesh built on
/// one is that frame's only.
/// </para>
/// </summary>
public sealed class DisplacementMesh
{
    private readonly int _cols;
    private readonly int _rows;
    private readonly float _spacing;
    private readonly float[] _offX;
    private readonly float[] _offY;
    private readonly DerotationField? _derotation;

    private DisplacementMesh(int cols, int rows, float spacing, float[] offX, float[] offY, DerotationField? derotation)
    {
        _cols = cols;
        _rows = rows;
        _spacing = spacing;
        _offX = offX;
        _offY = offY;
        _derotation = derotation;
    }

    /// <summary>The de-rotation beneath the registration, or null for a frame taken as it is.</summary>
    public DerotationField? Derotation => _derotation;

    /// <summary>Node grid dimensions (columns x rows), exposed for tests / diagnostics.</summary>
    public (int Cols, int Rows) NodeGrid => (_cols, _rows);

    /// <summary>
    /// The displacement at <c>(x, y)</c> in frame coordinates, bilinearly interpolated over the node grid
    /// (clamped at the edges).
    /// </summary>
    public (float OffsetX, float OffsetY) Sample(float x, float y)
    {
        var gx = x / _spacing;
        var gy = y / _spacing;

        var x0 = (int)MathF.Floor(gx);
        var y0 = (int)MathF.Floor(gy);
        var fx = gx - x0;
        var fy = gy - y0;

        x0 = Math.Clamp(x0, 0, _cols - 1);
        y0 = Math.Clamp(y0, 0, _rows - 1);
        var x1 = Math.Min(x0 + 1, _cols - 1);
        var y1 = Math.Min(y0 + 1, _rows - 1);

        var i00 = (y0 * _cols) + x0;
        var i10 = (y0 * _cols) + x1;
        var i01 = (y1 * _cols) + x0;
        var i11 = (y1 * _cols) + x1;

        var ox = Bilinear(_offX[i00], _offX[i10], _offX[i01], _offX[i11], fx, fy);
        var oy = Bilinear(_offY[i00], _offY[i10], _offY[i01], _offY[i11], fx, fy);
        if (_derotation is { } derotation)
        {
            var (dx, dy) = derotation.OffsetAt(x, y);
            (ox, oy) = (ox + dx, oy + dy);
        }
        return (ox, oy);
    }

    /// <summary>What a sample landing at <c>(x, y)</c> is multiplied by: its de-rotation's relight, or one.</summary>
    public float RelightAt(float x, float y) => _derotation?.RelightAt(x, y) ?? 1f;

    private static float Bilinear(float v00, float v10, float v01, float v11, float fx, float fy)
        => (v00 * (1 - fx) * (1 - fy)) + (v10 * fx * (1 - fy)) + (v01 * (1 - fx) * fy) + (v11 * fx * fy);

    /// <summary>
    /// Builds a mesh over a <paramref name="width"/> x <paramref name="height"/> frame from a global shift
    /// plus the alignment-point residuals. Each node's offset is
    /// <c>(globalDx, globalDy) + Sum(w_i * residual_i) / (Sum(w_i) + regularization)</c> with Gaussian
    /// weights <c>w_i = exp(-dist^2 / (2 * influence^2))</c>, so the field tends to the global shift where
    /// there are no nearby APs and toward an AP's <c>(global + residual)</c> where one dominates. Each residual's departure from
    /// the points' mean is first scaled by <paramref name="residualGain"/> (<see cref="PlanetaryStackOptions.MeshGain"/>, one by
    /// default: as read). The mean is the points' correction to the global shift, a rigid move read with a shrink of its own, and is
    /// kept as read: scaled with the rest, it moved every frame off its registration (#1081).
    /// </summary>
    public static DisplacementMesh Build(int width, int height, float globalDx, float globalDy,
        ReadOnlySpan<AlignmentPointShift> alignmentPoints, float nodeSpacing = 32f, float influence = 48f, float regularization = 0.25f, float residualGain = 1f)
        => Build(width, height, globalDx, globalDy, alignmentPoints, derotation: null, nodeSpacing, influence, regularization, residualGain);

    /// <summary>
    /// <see cref="Build(int, int, float, float, ReadOnlySpan{AlignmentPointShift}, float, float, float)"/> over a frame's
    /// <paramref name="derotation"/>: each point's residual is then over its de-rotated place (what
    /// <see cref="AlignmentPointMatcher.Match(Image, float, float, DerotationField?, Span{AlignmentPointShift})"/> measures).
    /// </summary>
    public static DisplacementMesh Build(int width, int height, float globalDx, float globalDy,
        ReadOnlySpan<AlignmentPointShift> alignmentPoints, DerotationField? derotation, float nodeSpacing = 32f, float influence = 48f, float regularization = 0.25f,
        float residualGain = 1f)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(nodeSpacing);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(influence);

        var cols = Math.Max(2, (int)MathF.Ceiling(width / nodeSpacing) + 1);
        var rows = Math.Max(2, (int)MathF.Ceiling(height / nodeSpacing) + 1);
        var offX = new float[cols * rows];
        var offY = new float[cols * rows];
        var twoInfluence2 = 2f * influence * influence;
        // The residuals the blend takes: as read, or with their departure from the points' mean scaled.
        Span<float> rx = alignmentPoints.Length <= 1024 ? stackalloc float[alignmentPoints.Length] : new float[alignmentPoints.Length];
        Span<float> ry = alignmentPoints.Length <= 1024 ? stackalloc float[alignmentPoints.Length] : new float[alignmentPoints.Length];
        double meanX = 0, meanY = 0;
        foreach (var ap in alignmentPoints)
        {
            meanX += ap.ResidualX;
            meanY += ap.ResidualY;
        }
        (meanX, meanY) = alignmentPoints.Length > 0 ? (meanX / alignmentPoints.Length, meanY / alignmentPoints.Length) : (0, 0);
        for (var i = 0; i < alignmentPoints.Length; i++)
        {
            var ap = alignmentPoints[i];
            (rx[i], ry[i]) = residualGain == 1f
                ? (ap.ResidualX, ap.ResidualY)
                : ((float)(meanX + (residualGain * (ap.ResidualX - meanX))), (float)(meanY + (residualGain * (ap.ResidualY - meanY))));
        }

        for (var r = 0; r < rows; r++)
        {
            var py = r * nodeSpacing;
            for (var c = 0; c < cols; c++)
            {
                var px = c * nodeSpacing;
                double sumW = regularization, sumRx = 0, sumRy = 0;
                for (var i = 0; i < alignmentPoints.Length; i++)
                {
                    var ap = alignmentPoints[i];
                    var dx = px - ap.X;
                    var dy = py - ap.Y;
                    var w = Math.Exp(-((dx * dx) + (dy * dy)) / twoInfluence2);
                    sumW += w;
                    sumRx += w * rx[i];
                    sumRy += w * ry[i];
                }

                var idx = (r * cols) + c;
                offX[idx] = globalDx + (float)(sumRx / sumW);
                offY[idx] = globalDy + (float)(sumRy / sumW);
            }
        }

        return new DisplacementMesh(cols, rows, nodeSpacing, offX, offY, derotation);
    }
}
